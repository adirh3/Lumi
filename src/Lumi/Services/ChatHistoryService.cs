using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Backs the agentic chat-history tools (<c>search_chats</c> / <c>read_chat</c>). Lets Lumi find
/// past conversations the user references and read their transcripts without leaving the chat.
///
/// Search reuses the warmed <see cref="GlobalSearchService"/> content index when available (the
/// same engine that powers the in-app search overlay) and falls back to a lightweight title/content
/// scan over <see cref="DataStore"/> when it is not. Reading is non-mutating: cold chats are read
/// straight from disk via <see cref="DataStore.ReadChatMessagesAsync"/> so browsing history never
/// permanently materializes chats or bloats memory.
/// </summary>
public sealed class ChatHistoryService
{
    private const int DefaultSearchLimit = 8;
    private const int MaxSearchLimit = 25;
    private const int DefaultReadMessages = 60;
    private const int MaxReadMessages = 400;
    private const int MaxMessageChars = 1_600;
    private const int MaxToolOutputChars = 280;
    private const int MaxTranscriptChars = 18_000;
    private const int MaxPlanChars = 1_500;

    private readonly DataStore _dataStore;
    private readonly GlobalSearchService? _searchService;
    private readonly Func<DateTimeOffset> _nowProvider;

    public ChatHistoryService(
        DataStore dataStore,
        GlobalSearchService? searchService,
        Func<DateTimeOffset>? nowProvider = null)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _searchService = searchService;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Finds chats matching a free-text query (topics, phrases, names, and time hints like
    /// "yesterday" or "last week"). Returns a compact, ranked list with the stable chat ID for each
    /// hit so the model can follow up with <see cref="ReadChatAsync"/>. An empty query lists the
    /// most recently active chats.
    /// </summary>
    public async Task<string> SearchChatsAsync(
        string? query,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var max = Math.Clamp(limit ?? DefaultSearchLimit, 1, MaxSearchLimit);
        var trimmed = (query ?? "").Trim();

        var snapshot = CaptureSnapshot();
        if (snapshot.Chats.Count == 0)
            return "There are no saved chats yet.";

        var candidates = string.IsNullOrEmpty(trimmed)
            ? ListRecentChats(snapshot, max)
            : await FindChatsAsync(snapshot, trimmed, max, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
            return $"No chats matched \"{trimmed}\". Try a broader query, a different keyword, or search_chats with an empty query to list recent chats.";

        var builder = new StringBuilder();
        builder.Append(string.IsNullOrEmpty(trimmed)
            ? $"Most recent {candidates.Count} chat(s):"
            : $"Found {candidates.Count} chat(s) matching \"{trimmed}\" (most relevant first):");
        builder.Append('\n');

        var index = 1;
        foreach (var candidate in candidates)
        {
            builder.Append('\n');
            builder.Append(index++).Append(". ").Append(Quote(candidate.Chat.Title)).Append('\n');
            builder.Append("   id: ").Append(candidate.Chat.Id).Append('\n');
            builder.Append("   ").Append(DescribeChatMeta(snapshot, candidate.Chat));
            if (!string.IsNullOrWhiteSpace(candidate.Snippet))
                builder.Append('\n').Append("   match: ").Append(Truncate(Collapse(candidate.Snippet), 220));
            builder.Append('\n');
        }

        builder.Append("\nUse read_chat with one of the ids above to read a full conversation.");
        return builder.ToString();
    }

    /// <summary>
    /// Reads a single chat's transcript. <paramref name="chatIdentifier"/> may be a chat ID, an
    /// exact title, or a descriptive phrase — when it is not an exact match the service searches and
    /// either reads the clear winner or returns the candidates so the model can pick by ID. The
    /// transcript is windowed to the most recent <paramref name="maxMessages"/> messages and capped
    /// in size so very long chats stay readable. The header also reports the chat's actionable
    /// context — its workspace (git worktree path or project folder), additional context directories,
    /// any saved plan, active skills/MCP servers, and model/token usage — so the model can act on the
    /// conversation (e.g. inspect the uncommitted code in that chat's workspace).
    /// </summary>
    public async Task<string> ReadChatAsync(
        string? chatIdentifier,
        int? maxMessages = null,
        bool includeReasoning = false,
        bool includeToolCalls = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chatIdentifier))
            return "Provide a chat id or title to read. Use search_chats first if you are unsure which chat to open.";

        var snapshot = CaptureSnapshot();
        var resolution = await ResolveChatAsync(snapshot, chatIdentifier.Trim(), cancellationToken).ConfigureAwait(false);
        if (resolution.Chat is null)
            return resolution.Message;

        var chat = resolution.Chat;
        var messages = await _dataStore.ReadChatMessagesAsync(chat, cancellationToken).ConfigureAwait(false);
        var window = Math.Clamp(maxMessages ?? DefaultReadMessages, 1, MaxReadMessages);

        return FormatTranscript(snapshot, chat, messages, window, includeReasoning, includeToolCalls);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    // AppData.Chats/Projects/Agents are plain lists mutated on the UI thread and are not
    // thread-safe; these tool handlers run on the background agent loop. Snapshot them once per
    // call (mirroring GlobalSearchService) so the rest of the work iterates a stable copy instead
    // of risking "Collection was modified" while the user creates or deletes chats mid-read.
    private HistorySnapshot CaptureSnapshot()
        => new(
            _dataStore.Data.Chats.ToArray(),
            _dataStore.Data.Projects.ToArray(),
            _dataStore.Data.Agents.ToArray(),
            _dataStore.Data.Skills.ToArray());

    private static List<ChatCandidate> ListRecentChats(HistorySnapshot snapshot, int limit)
        => snapshot.Chats
            .OrderByDescending(static chat => chat.UpdatedAt)
            .Take(limit)
            .Select(static chat => new ChatCandidate(chat, ""))
            .ToList();

    private async Task<List<ChatCandidate>> FindChatsAsync(
        HistorySnapshot snapshot,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (_searchService is not null)
        {
            var matches = await _searchService
                .SearchAsync(query, GlobalSearchExecutionMode.Full, cancellationToken)
                .ConfigureAwait(false);

            var ranked = matches
                .Where(static match => match.Category == GlobalSearchCategory.Chats)
                .Select(match => (Chat: match.Item as Chat, Snippet: ExtractSnippet(match)))
                .Where(static tuple => tuple.Chat is not null)
                .Take(limit)
                .Select(static tuple => new ChatCandidate(tuple.Chat!, tuple.Snippet))
                .ToList();

            if (ranked.Count > 0)
                return ranked;
        }

        return FallbackSearch(snapshot, query, limit);
    }

    private List<ChatCandidate> FallbackSearch(HistorySnapshot snapshot, string query, int limit)
    {
        var scored = new List<(ChatCandidate Candidate, int Score, DateTimeOffset Updated)>();

        foreach (var chat in snapshot.Chats)
        {
            var score = 0;
            var snippet = "";

            if (chat.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                score += 100;

            if (score == 0)
            {
                var chatSnapshot = _dataStore.GetChatSearchSnapshot(chat);
                foreach (var message in chatSnapshot.Messages)
                {
                    var hit = message.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (hit < 0)
                        continue;

                    score += 40;
                    snippet = BuildSnippet(message.Text, hit, query.Length);
                    break;
                }
            }

            if (score > 0)
                scored.Add((new ChatCandidate(chat, snippet), score, chat.UpdatedAt));
        }

        return scored
            .OrderByDescending(static entry => entry.Score)
            .ThenByDescending(static entry => entry.Updated)
            .Take(limit)
            .Select(static entry => entry.Candidate)
            .ToList();
    }

    // ── Resolution ──────────────────────────────────────────────────────────────

    private async Task<ChatResolution> ResolveChatAsync(
        HistorySnapshot snapshot,
        string identifier,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(identifier, out var id))
        {
            var byId = snapshot.Chats.FirstOrDefault(chat => chat.Id == id);
            return byId is not null
                ? new ChatResolution(byId, "")
                : new ChatResolution(null, $"No chat has the id {id}. Use search_chats to find the right chat.");
        }

        var exactMatches = snapshot.Chats
            .Where(chat => string.Equals(chat.Title, identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
            return new ChatResolution(exactMatches[0], "");

        if (exactMatches.Count > 1)
            return new ChatResolution(null, DescribeAmbiguity(snapshot, identifier, exactMatches.Select(chat => new ChatCandidate(chat, ""))));

        var candidates = await FindChatsAsync(snapshot, identifier, 6, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            return new ChatResolution(null, $"No chats found matching \"{identifier}\". Try search_chats with a different query.");

        if (candidates.Count == 1)
            return new ChatResolution(candidates[0].Chat, "");

        return new ChatResolution(null, DescribeAmbiguity(snapshot, identifier, candidates));
    }

    private string DescribeAmbiguity(HistorySnapshot snapshot, string identifier, IEnumerable<ChatCandidate> candidates)
    {
        var builder = new StringBuilder();
        builder.Append($"Several chats match \"{identifier}\". Call read_chat again with one of these ids:\n");
        foreach (var candidate in candidates)
        {
            builder.Append("\n• ").Append(Quote(candidate.Chat.Title)).Append('\n');
            builder.Append("  id: ").Append(candidate.Chat.Id).Append('\n');
            builder.Append("  ").Append(DescribeChatMeta(snapshot, candidate.Chat));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    // ── Transcript formatting ────────────────────────────────────────────────────

    private string FormatTranscript(
        HistorySnapshot snapshot,
        Chat chat,
        IReadOnlyList<ChatMessage> messages,
        int window,
        bool includeReasoning,
        bool includeToolCalls)
    {
        var lines = messages
            .Where(message => ShouldShow(message, includeReasoning, includeToolCalls))
            .Select(message => FormatMessage(message, includeReasoning))
            .Where(static line => !string.IsNullOrEmpty(line))
            .ToList();

        var builder = new StringBuilder();
        builder.Append(Quote(chat.Title)).Append('\n');
        builder.Append("id: ").Append(chat.Id).Append('\n');
        builder.Append(DescribeChatMeta(snapshot, chat)).Append('\n');
        AppendChatMetadata(builder, snapshot, chat);

        if (lines.Count == 0)
        {
            builder.Append("──────────\n");
            builder.Append("This chat has no readable messages yet.");
            return builder.ToString();
        }

        // Window to the most recent messages, then — if the size cap is still exceeded — drop the
        // OLDEST messages of that window first. The latest exchange (what the user is usually asking
        // about) must always survive, so budget characters from the newest message backward.
        var windowed = lines.Count > window ? lines.Skip(lines.Count - window).ToList() : lines;

        var budget = Math.Max(0, MaxTranscriptChars - builder.Length - 160);
        var kept = new List<string>(windowed.Count);
        var used = 0;
        for (var i = windowed.Count - 1; i >= 0; i--)
        {
            var cost = windowed[i].Length + 2;
            if (kept.Count > 0 && used + cost > budget)
                break;

            kept.Add(windowed[i]);
            used += cost;
        }

        kept.Reverse();

        var omitted = lines.Count - kept.Count;
        if (omitted > 0)
            builder.Append($"(showing the latest {kept.Count} of {lines.Count} messages — increase maxMessages to see the earlier {omitted})\n");

        builder.Append("──────────\n");

        foreach (var line in kept)
            builder.Append('\n').Append(line).Append('\n');

        return builder.ToString();
    }

    private static bool ShouldShow(ChatMessage message, bool includeReasoning, bool includeToolCalls)
    {
        switch (message.Role)
        {
            case "system":
                return false;
            case "reasoning":
                return includeReasoning && !string.IsNullOrWhiteSpace(message.Content);
            case "tool":
                return includeToolCalls;
            default:
                return !string.IsNullOrWhiteSpace(message.Content);
        }
    }

    private string FormatMessage(ChatMessage message, bool includeReasoning)
    {
        switch (message.Role)
        {
            case "user":
                return $"User: {Truncate(message.Content.Trim(), MaxMessageChars)}";
            case "assistant":
            {
                var speaker = string.IsNullOrWhiteSpace(message.Author) ? "Lumi" : message.Author!.Trim();
                return $"{speaker}: {Truncate(message.Content.Trim(), MaxMessageChars)}";
            }
            case "reasoning":
                return includeReasoning ? $"[reasoning] {Truncate(Collapse(message.Content), MaxToolOutputChars)}" : "";
            case "error":
                return $"[error] {Truncate(message.Content.Trim(), MaxMessageChars)}";
            case "tool":
                return FormatToolMessage(message);
            default:
                return string.IsNullOrWhiteSpace(message.Content)
                    ? ""
                    : $"{message.Role}: {Truncate(message.Content.Trim(), MaxMessageChars)}";
        }
    }

    private string FormatToolMessage(ChatMessage message)
    {
        var toolName = message.ToolName ?? "tool";
        var friendly = ToolDisplayHelper.GetFriendlyToolDisplay(toolName, message.Author, null).Name;
        var label = string.IsNullOrWhiteSpace(friendly) ? toolName : friendly;

        var detail = !string.IsNullOrWhiteSpace(message.ToolOutput)
            ? Collapse(message.ToolOutput!)
            : Collapse(message.Content);

        return string.IsNullOrWhiteSpace(detail)
            ? $"[tool: {label}]"
            : $"[tool: {label}] {Truncate(detail, MaxToolOutputChars)}";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private string DescribeChatMeta(HistorySnapshot snapshot, Chat chat)
    {
        var parts = new List<string>();

        var projectName = chat.ProjectId is { } projectId
            ? snapshot.Projects.FirstOrDefault(project => project.Id == projectId)?.Name
            : null;
        if (!string.IsNullOrWhiteSpace(projectName))
            parts.Add($"project: {projectName}");

        var agentName = chat.AgentId is { } agentId
            ? snapshot.Agents.FirstOrDefault(agent => agent.Id == agentId)?.Name
            : null;
        if (!string.IsNullOrWhiteSpace(agentName))
            parts.Add($"Lumi: {agentName}");

        parts.Add($"updated {FormatRelativeTime(chat.UpdatedAt)}");
        return string.Join(" · ", parts);
    }

    // Surfaces the chat's actionable context (read_chat only) so the model can act on the
    // conversation — most importantly the workspace folder behind "do what we did in that chat".
    private static void AppendChatMetadata(StringBuilder builder, HistorySnapshot snapshot, Chat chat)
    {
        var project = chat.ProjectId is { } projectId
            ? snapshot.Projects.FirstOrDefault(candidate => candidate.Id == projectId)
            : null;

        var workspace = DescribeWorkspace(chat, project);
        if (workspace is not null)
            builder.Append("workspace: ").Append(workspace).Append('\n');

        if (project is not null)
        {
            var contextDirs = SnapshotList(project.AdditionalContextDirectories)
                .Where(static dir => !string.IsNullOrWhiteSpace(dir))
                .Select(static dir => dir.Trim())
                .ToList();
            if (contextDirs.Count > 0)
                builder.Append("context dirs: ").Append(string.Join(", ", contextDirs)).Append('\n');
        }

        var usage = DescribeModelUsage(chat);
        if (usage is not null)
            builder.Append(usage).Append('\n');

        var skills = DescribeActiveSkills(snapshot, chat);
        if (skills is not null)
            builder.Append("skills: ").Append(skills).Append('\n');

        var mcpServers = SnapshotList(chat.ActiveMcpServerNames)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToList();
        if (mcpServers.Count > 0)
            builder.Append("mcp servers: ").Append(string.Join(", ", mcpServers)).Append('\n');

        if (!string.IsNullOrWhiteSpace(chat.PlanContent))
        {
            builder.Append("plan:\n");
            builder.Append(Truncate(chat.PlanContent.Trim(), MaxPlanChars)).Append('\n');
        }
    }

    private static string? DescribeWorkspace(Chat chat, Project? project)
    {
        var projectDir = project?.WorkingDirectory is { Length: > 0 } pd ? pd : null;

        // The stored worktree path is the worktree root; map it to the project's subpath so the
        // reported location matches where the chat actually runs (GetEffectiveWorkingDirectoryForChat).
        var worktree = chat.WorktreePath is { Length: > 0 } w
            ? GitService.ResolveWorktreeWorkingDirectory(w, projectDir)
            : null;

        // Mirror GetEffectiveWorkingDirectoryForChat: an existing worktree wins, otherwise the chat
        // effectively runs from the project working directory. Only report a stale path when neither
        // effective directory exists on disk, so the agent learns the real working location.
        if (worktree is not null && SafeDirectoryExists(worktree))
            return $"{worktree} (git worktree)";

        if (projectDir is not null && SafeDirectoryExists(projectDir))
            return worktree is not null
                ? $"{projectDir} (project folder — git worktree {worktree} no longer on disk)"
                : $"{projectDir} (project folder)";

        if (worktree is not null)
            return $"{worktree} (git worktree — no longer on disk)";

        if (projectDir is not null)
            return $"{projectDir} (project folder — no longer on disk)";

        return null;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    // The per-chat/per-project lists (skill ids/names, MCP servers, context dirs) are mutated in
    // place on the UI thread (e.g. skill/MCP deletion iterates every chat); copy one once before
    // enumerating so the background agent loop reads a private array instead of the live model list
    // (which would otherwise throw "Collection was modified"). Mirrors the top-level snapshot.
    private static T[] SnapshotList<T>(List<T> list) => list.ToArray();

    private static string? DescribeModelUsage(Chat chat)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(chat.LastModelUsed))
            parts.Add($"model: {chat.LastModelUsed!.Trim()}");

        if (chat.TotalInputTokens > 0 || chat.TotalOutputTokens > 0)
            parts.Add($"tokens: {FormatTokenCount(chat.TotalInputTokens)} in / {FormatTokenCount(chat.TotalOutputTokens)} out");

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string FormatTokenCount(long count)
    {
        if (count < 1_000)
            return count.ToString(CultureInfo.InvariantCulture);
        if (count < 1_000_000)
            return (count / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";

        return (count / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    private static string? DescribeActiveSkills(HistorySnapshot snapshot, Chat chat)
    {
        var names = new List<string>();

        foreach (var skillId in SnapshotList(chat.ActiveSkillIds))
        {
            var skill = snapshot.Skills.FirstOrDefault(candidate => candidate.Id == skillId);
            if (skill is not null && !string.IsNullOrWhiteSpace(skill.Name))
                names.Add(skill.Name.Trim());
        }

        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count > 0 ? string.Join(", ", distinct) : null;
    }

    private static string ExtractSnippet(GlobalSearchMatch match)
    {
        // Chat subtitles look like "<project> · <relative time> · <content snippet>"; surface only the
        // trailing content snippet when the match was a content hit so it reads as "what was said".
        if (!match.IsContentMatch || string.IsNullOrWhiteSpace(match.Subtitle))
            return "";

        var separator = match.Subtitle.LastIndexOf(" · ", StringComparison.Ordinal);
        return separator >= 0 ? match.Subtitle[(separator + 3)..] : match.Subtitle;
    }

    private static string BuildSnippet(string text, int hit, int queryLength)
    {
        var start = Math.Max(0, hit - 40);
        var end = Math.Min(text.Length, hit + queryLength + 60);
        var slice = text[start..end].Trim();
        var prefix = start > 0 ? "…" : "";
        var suffix = end < text.Length ? "…" : "";
        return $"{prefix}{Collapse(slice)}{suffix}";
    }

    private string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var now = _nowProvider();
        var delta = now - timestamp;

        if (delta < TimeSpan.Zero)
            return "just now";
        if (delta.TotalMinutes < 1)
            return "just now";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7)
            return $"{(int)delta.TotalDays}d ago";

        return timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Quote(string? title)
        => string.IsNullOrWhiteSpace(title) ? "\"(untitled chat)\"" : $"\"{title.Trim()}\"";

    private static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(character);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }

    private sealed record ChatCandidate(Chat Chat, string Snippet);

    private sealed record ChatResolution(Chat? Chat, string Message);

    private sealed record HistorySnapshot(
        IReadOnlyList<Chat> Chats,
        IReadOnlyList<Project> Projects,
        IReadOnlyList<LumiAgent> Agents,
        IReadOnlyList<Skill> Skills);
}
