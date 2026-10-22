using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Microsoft.Extensions.AI;
using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.Services.Context;

/// <summary>A chat with activity in a curator's window: the messages in the window and everything before it.</summary>
internal sealed record CuratorChat(Chat Chat, IReadOnlyList<ChatMessage> Window, IReadOnlyList<ChatMessage> Before)
{
    public string ShortId => Chat.Id.ToString("N")[..8];
    public int OwnerMessages => Window.Count(ChatDigest.IsOwnerMessage);
}

/// <summary>A global memory a project curator suggests; the next all-projects review decides on it.</summary>
internal sealed record CuratorProposal(string Id, string FromProject, string Key, string Content, string Category, string Evidence);

/// <summary>
/// One curator session over one scope (a project, or everything outside projects) for one window of time: the chats
/// with activity in the window, the tools the session reads and writes with, and what it changed.
/// </summary>
internal sealed partial class ContextCuratorScope
{
    internal const int PartChars = 9_000;
    internal const int MaxToolCalls = 40;
    internal const int MemoryHardLimit = ContextCuratorPrompts.MemoryTargetChars * 3 / 2;

    // Project curators run side by side; their changes to the memory list take turns.
    private static readonly object MemoryWrites = new();

    private readonly DataStore _dataStore;
    private readonly ChatHistoryService _history;
    private readonly List<CuratorProposal> _proposals;
    private readonly IReadOnlyCollection<Guid> _updatedProjects;
    private readonly HashSet<string> _readParts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<string>> _parts = [];
    private string? _briefSeen;
    private int _toolCalls;
    private int _memoryChanges;

    public ContextCuratorScope(
        DataStore dataStore,
        ChatHistoryService history,
        Project? project,
        ContextBrief brief,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<CuratorChat> chats,
        List<CuratorProposal> proposals,
        IReadOnlyCollection<Guid> updatedProjects,
        string owner,
        bool canWriteMemories)
    {
        _dataStore = dataStore;
        _history = history;
        Project = project;
        Brief = brief;
        From = from;
        To = to;
        Chats = chats;
        _proposals = proposals;
        _updatedProjects = updatedProjects;
        Owner = owner;
        CanWriteMemories = canWriteMemories;
    }

    public Project? Project { get; }
    public ContextBrief Brief { get; }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public IReadOnlyList<CuratorChat> Chats { get; }
    public string Owner { get; }
    public bool CanWriteMemories { get; }
    public string Name => Project?.Name ?? "All projects";
    public bool BriefChanged { get; private set; }
    public int ToolCalls => Volatile.Read(ref _toolCalls);
    public int MemoryChanges => Volatile.Read(ref _memoryChanges);

    public string SystemPrompt => Project is null
        ? ContextCuratorPrompts.Global(Owner, CanWriteMemories)
        : ContextCuratorPrompts.Project(Owner, Project.Name, CanWriteMemories);

    private int BriefLimit => Project is null ? ContextCuratorPrompts.GlobalBriefLimit : ContextCuratorPrompts.ProjectBriefLimit;

    // ───────────── initial prompt ─────────────

    public string BuildPrompt()
    {
        // The brief this session starts from; one the owner changes while it works isn't overwritten (UpdateBrief).
        _briefSeen = Brief.Content;
        var sb = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"It is {To.ToLocalTime():dddd, MMMM d, yyyy, HH:mm}.");
        if (Project is { } project)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Project: {project.Name}{(project.WorkingDirectory is { Length: > 0 } dir ? $" (folder {dir})" : "")}")
                .AppendLine(CultureInfo.InvariantCulture, $"Project instructions (excerpt): {(string.IsNullOrWhiteSpace(project.Instructions) ? "(none)" : ChatDigest.Excerpt(project.Instructions, 700))}");
        }
        else
        {
            sb.AppendLine().AppendLine("Project briefs:").Append(ProjectBriefs());
        }

        sb.AppendLine().Append(DescribeBrief());
        if (Project is not null)
        {
            sb.AppendLine()
                .AppendLine(CanWriteMemories ? "Project memories (yours to change; ids in brackets):" : "Project memories:")
                .AppendLine(MemoryList(projectOnly: true))
                .AppendLine()
                .AppendLine(CanWriteMemories
                    ? "Global memories (read-only for you; to add or change one, use create with scope \"global\" and the all-projects curator decides):"
                    : "Global memories:")
                .AppendLine(MemoryList(projectOnly: false));
        }
        else
        {
            sb.AppendLine()
                .AppendLine(CanWriteMemories ? "Global memories (yours to change; ids in brackets):" : "Global memories:")
                .AppendLine(MemoryList(projectOnly: false));
        }

        if (CanWriteMemories && DuplicateHints() is { Length: > 0 } hints)
        {
            sb.AppendLine()
                .AppendLine("Memories that look alike (merge them, or leave both if they really say different things):")
                .AppendLine(hints);
        }

        if (Project is null)
        {
            List<CuratorProposal> proposals;
            lock (_proposals)
                proposals = [.. _proposals];
            sb.AppendLine()
                .AppendLine("Proposals from project curators since your last run:")
                .AppendLine(proposals.Count == 0
                    ? "(none)"
                    : string.Join("\n", proposals.Select(p =>
                        $"- [{p.Id}] from {p.FromProject}: ({p.Category}) {p.Key}: {p.Content} (evidence: {ChatDigest.Excerpt(p.Evidence, 300)})")));
        }

        var label = Project is null ? "Chats outside projects" : "Chats";
        sb.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{label} with activity since your last run ({From.ToLocalTime():MMM d HH:mm} → {To.ToLocalTime():MMM d HH:mm}):")
            .AppendLine(ActivityList())
            .AppendLine("Do this run's update now.");
        return sb.ToString();
    }

    private string DescribeBrief()
    {
        var label = Project is null ? "global brief" : "brief";
        if (string.IsNullOrWhiteSpace(Brief.Content))
            return $"Current {label}: (none yet: this is your first run)\n";

        var sb = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Current {label} (last changed {Brief.UpdatedAt.ToLocalTime():MMM d HH:mm}):");
        if (Brief.EditedByUser)
            sb.AppendLine(CultureInfo.InvariantCulture, $"({Owner} edited it by hand since your last run; keep those edits unless newer chats show they are out of date.)");
        return sb.AppendLine(Brief.Content).ToString();
    }

    private string ProjectBriefs()
    {
        var projects = _dataStore.Data.Projects.ToArray().ToDictionary(project => project.Id);
        var briefs = _dataStore.Data.Briefs.ToArray()
            .Where(brief => brief.ProjectId is { } id && projects.ContainsKey(id) && !string.IsNullOrWhiteSpace(brief.Content))
            .Select(brief => (Project: projects[brief.ProjectId!.Value], Brief: brief))
            .OrderBy(item => item.Project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (briefs.Count == 0)
            return "(none yet)\n";

        var sb = new StringBuilder();
        foreach (var (project, brief) in briefs)
        {
            if (To - brief.UpdatedAt > TimeSpan.FromDays(14))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {project.Name}: quiet since {brief.UpdatedAt.ToLocalTime():MMM d}");
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                    $"### {project.Name} (last changed {brief.UpdatedAt.ToLocalTime():MMM d HH:mm}{(_updatedProjects.Contains(project.Id) ? "; updated since your last run" : "")})")
                .AppendLine(brief.Content)
                .AppendLine();
        }

        return sb.ToString();
    }

    public string ActivityList()
    {
        if (Chats.Count == 0)
            return "(none)";
        var sb = new StringBuilder();
        foreach (var chat in Chats)
        {
            var replies = chat.Window.Count(ChatDigest.IsReply);
            var tools = chat.Window.Count(message => message.Role == "tool");
            var jobRuns = chat.Window.Count(message => message.Role == "user" && ChatDigest.IsAutomated(message));
            var phone = chat.Window.Count(message => ChatDigest.IsOwnerMessage(message) && message.RemoteRequestId is not null);
            var parts = Parts(chat).Count;
            var first = chat.Window[0].Timestamp.ToLocalTime();
            var last = chat.Window[^1].Timestamp.ToLocalTime();
            var span = first.Date == last.Date ? $"{first:MMM d HH:mm}–{last:HH:mm}" : $"{first:MMM d HH:mm} – {last:MMM d HH:mm}";
            sb.Append(CultureInfo.InvariantCulture,
                $"- [{chat.ShortId}] \"{chat.Chat.Title}\": {Owner} {chat.OwnerMessages} msgs{(phone > 0 ? $" ({phone} from phone)" : "")}, Lumi {replies} replies, {tools} tool calls, {span}");
            if (parts > 1)
                sb.Append(CultureInfo.InvariantCulture, $", {parts} parts");
            if (jobRuns > 0)
                sb.Append(CultureInfo.InvariantCulture, $", job runs {jobRuns}");
            sb.AppendLine(chat.Before.Count == 0 ? ", new" : $", started {chat.Chat.CreatedAt.ToLocalTime():MMM d}");

            var owned = chat.Window.Where(ChatDigest.IsOwnerMessage).ToList();
            var firstAsk = owned.FirstOrDefault() ?? chat.Window.FirstOrDefault(message => message.Role == "user");
            if (firstAsk is not null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  first request: \"{ChatDigest.Excerpt(firstAsk.Content, 180)}\"");
            if (owned.Count > 1)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  latest request: \"{ChatDigest.Excerpt(owned[^1].Content, 180)}\"");
        }

        return sb.ToString();
    }

    private List<string> Parts(CuratorChat chat)
    {
        lock (_parts)
        {
            if (!_parts.TryGetValue(chat.Chat.Id, out var parts))
            {
                _parts[chat.Chat.Id] = parts = ChatDigest.SplitParts(
                    ChatDigest.SplitTurns(chat.Window).Select(turn => ChatDigest.RenderTurn(turn, Owner)),
                    PartChars);
            }

            return parts;
        }
    }

    // ───────────── tools ─────────────

    public List<AIFunction> BuildTools()
    {
        var global = Project is null;
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                async ([Description("Chat id (8 characters from the list, or an id from search_chats) or its exact title")] string chat,
                    [Description("Part to read, from 1 (default 1)")] int? part,
                    [Description("'since' (default): what happened since your last run. 'earlier': how the chat started and its latest turns before that.")] string? from) =>
                    await SafeAsync("read_chat", () => ReadChatAsync(chat, part ?? 1, from, CancellationToken.None)).ConfigureAwait(false),
                "read_chat",
                "Read a chat: the owner's messages, Lumi's replies and a summary of tool use. Long stretches come in parts in time order; the last part has the latest state. Each part can be read once.",
                AppDataJsonContext.Default.Options),
            AIFunctionFactory.Create(
                async ([Description("A few distinctive keywords")] string query) =>
                    await SafeAsync("search_chats", () => SearchChatsAsync(query, CancellationToken.None)).ConfigureAwait(false),
                "search_chats",
                global
                    ? "Search all chats (the owner's messages and Lumi's replies). Use it to check whether something was said or decided before."
                    : "Search this project's chats (the owner's messages and Lumi's replies). Use it to check whether something was said or decided before.",
                AppDataJsonContext.Default.Options),
            AIFunctionFactory.Create(
                ([Description("The complete new brief in markdown")] string content) =>
                    Safe("update_brief", () => UpdateBrief(content)),
                "update_brief",
                global ? "Replace the global brief." : "Replace this project's brief.",
                AppDataJsonContext.Default.Options),
        };

        if (CanWriteMemories)
        {
            tools.Add(AIFunctionFactory.Create(
                ([Description("create, update or delete")] string action,
                    [Description("For update and delete: the memory's id from the lists, e.g. a1b2c3")] string? memory,
                    [Description("Short label, e.g. 'Release branch policy' (create; on update it renames)")] string? key,
                    [Description("The fact, written to make sense on its own (create and update)")] string? content,
                    [Description("Project, Work, Preferences, Personal, Goals or Interests")] string? category,
                    [Description("Project curators: 'project' (default), or 'global' to propose a global memory to the all-projects curator")] string? scope,
                    [Description("Required for create and update: the chat title, the date and the owner's words")] string? evidence,
                    [Description("Required for delete: obsolete, wrong, 'duplicate of [id]' or 'merged into [id]'")] string? reason) =>
                    Safe("manage_memories", () => ManageMemories(action, memory, key, content, category, scope, evidence, reason)),
                "manage_memories",
                global
                    ? "Create, update or delete global memories (about the owner, or true across all of the owner's work)."
                    : "Create, update or delete this project's memories, or propose a global one.",
                AppDataJsonContext.Default.Options));
        }

        return tools;
    }

    private string? Guard()
        => Interlocked.Increment(ref _toolCalls) > MaxToolCalls
            ? "Tool budget reached. Finish now: call update_brief if the brief needs changes, then reply."
            : null;

    // A tool must never throw: the model would only see that it failed, and the run would lose why.
    private string Safe(string tool, Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Context] {Name}: {tool} failed: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> SafeAsync(string tool, Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Context] {Name}: {tool} failed: {ex}");
            return $"Error: {ex.Message}";
        }
    }

    internal async Task<string> ReadChatAsync(string chatRef, int part, string? from, CancellationToken cancellationToken)
    {
        if (Guard() is { } stop)
            return stop;

        var value = (chatRef ?? "").Trim().Trim('[', ']', '"');
        var window = Chats.FirstOrDefault(chat => chat.ShortId.Equals(value, StringComparison.OrdinalIgnoreCase)
                                                  || chat.Chat.Id.ToString().Equals(value, StringComparison.OrdinalIgnoreCase)
                                                  || chat.Chat.Title.Equals(value, StringComparison.OrdinalIgnoreCase));
        var target = window?.Chat ?? FindChatInScope(value);
        if (target is null)
            return "Unknown chat. Use the 8-character id from the list or an id from search_chats.";

        var earlier = window is null || string.Equals(from?.Trim(), "earlier", StringComparison.OrdinalIgnoreCase);
        List<string> parts;
        string label;
        if (!earlier)
        {
            parts = Parts(window!);
            label = "since your last run";
        }
        else
        {
            var before = window?.Before
                         ?? (await _dataStore.ReadChatMessagesAsync(target, cancellationToken).ConfigureAwait(false))
                             .Where(message => message.ParentToolCallId is null && message.Timestamp <= From)
                             .OrderBy(message => message.Timestamp)
                             .ToList();
            if (before.Count == 0)
                return $"\"{target.Title}\" has no history before your last run.";

            var turns = ChatDigest.SplitTurns(before);
            List<ChatDigest.Turn> picked = turns.Count <= 6 ? turns : [.. turns.Take(2), .. turns.TakeLast(4)];
            parts = ChatDigest.SplitParts(picked.Select(turn => ChatDigest.RenderTurn(turn, Owner, 900, 700, 1)), PartChars);
            label = turns.Count <= 6 ? "before your last run" : "before your last run: how it started and its latest turns";
        }

        part = Math.Clamp(part, 1, parts.Count);
        lock (_readParts)
        {
            if (!_readParts.Add($"{target.Id:N}:{(earlier ? "e" : "w")}:{part}"))
            {
                return $"You already read part {part} of {parts.Count} of \"{target.Title}\" ({label}). "
                       + (part < parts.Count ? $"Read part {part + 1} if you need more, or move on." : "Move on.");
            }
        }

        var header = $"\"{target.Title}\", {label}: part {part} of {parts.Count}.\n";
        var footer = part < parts.Count ? $"[Continues in part {part + 1}; the last part has the latest state.]\n" : "";
        return header + parts[part - 1] + footer;
    }

    private Chat? FindChatInScope(string value)
    {
        if (value.Length == 0)
            return null;
        var chats = _dataStore.Data.Chats.ToArray().Where(CanRead).ToList();
        if (Guid.TryParse(value, out var id))
            return chats.FirstOrDefault(chat => chat.Id == id);
        return chats.FirstOrDefault(chat => value.Length >= 6 && chat.Id.ToString("N").StartsWith(value, StringComparison.OrdinalIgnoreCase))
               ?? chats.FirstOrDefault(chat => string.Equals(chat.Title, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A project's curator stays inside its project; the all-projects curator may open any chat it finds. Chats
    /// started after the window are left for the next run.</summary>
    private bool CanRead(Chat chat) => chat.CreatedAt <= To && (Project is null || chat.ProjectId == Project.Id);

    internal async Task<string> SearchChatsAsync(string query, CancellationToken cancellationToken)
    {
        if (Guard() is { } stop)
            return stop;
        if (string.IsNullOrWhiteSpace(query))
            return "Give a few distinctive keywords.";
        return await _history.SearchChatsAsync(query, 8, CanRead, cancellationToken).ConfigureAwait(false);
    }

    internal string UpdateBrief(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "content is required.";

        // The owner edited or cleared the brief on the Memories page while this session worked: their version wins,
        // and the session builds on it.
        var current = Brief.Content;
        if (_briefSeen is { } seen && !string.Equals(current, seen, StringComparison.Ordinal))
        {
            _briefSeen = current;
            return string.IsNullOrWhiteSpace(current)
                ? $"Not saved: {Owner} cleared this brief while you were working. Write it fresh from the chats you read, then call update_brief again."
                : $"Not saved: {Owner} edited this brief while you were working. Their version is below. Add your update to it, keeping their edits unless the chats you read show they're out of date, then call update_brief again.\n\n{current}";
        }

        content = content.Trim();
        if (content.Length > BriefLimit)
            return $"Too long: {content.Length} characters, the limit is {BriefLimit}. Shorten it and call update_brief again.";

        var warnings = ValidateBrief(content, Project is null);
        Brief.Content = content;
        Brief.UpdatedAt = DateTimeOffset.Now;
        Brief.EditedByUser = false;
        _briefSeen = content;
        BriefChanged = true;
        return warnings.Count == 0
            ? "Brief saved."
            : $"Brief saved, but: {string.Join(" ", warnings)} Fix it and call update_brief again if it matters.";
    }

    [GeneratedRegex(@"\([^()]*?(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}(?:\s*[–-]\s*\d{1,2})?\)\s*\.?\s*$")]
    private static partial Regex SourcePattern();

    internal static List<string> ValidateBrief(string content, bool global)
    {
        var warnings = new List<string>();
        var bullets = content.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            .ToList();
        var uncited = bullets.Where(bullet => !SourcePattern().IsMatch(bullet)).ToList();
        if (uncited.Count > 0)
        {
            warnings.Add($"{uncited.Count} of {bullets.Count} bullets don't end with a source like (\"chat title\", Oct 19){(global ? " or (as of Oct 19)" : "")}: "
                         + string.Join(" | ", uncited.Take(2).Select(bullet => ChatDigest.Excerpt(bullet, 90))) + ".");
        }

        var required = global ? "## Projects" : "## Now";
        if (bullets.Count > 0 && !global && !content.Contains(required, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"There is no \"{required}\" section.");
        return warnings;
    }

    // ───────────── memories ─────────────

    private bool Writable(Memory memory) => Project is null ? memory.ProjectId is null : memory.ProjectId == Project.Id;

    private static string Short(Memory memory) => memory.Id.ToString("N")[..6];

    private static bool IsActive(Memory memory) => string.Equals(memory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase);

    private List<Memory> Visible()
        => _dataStore.Data.Memories.ToArray()
            .Where(memory => IsActive(memory) && (memory.ProjectId is null || (Project is not null && memory.ProjectId == Project.Id)))
            .ToList();

    internal string MemoryList(bool projectOnly)
    {
        var lines = Visible()
            .Where(memory => projectOnly ? memory.ProjectId is not null : memory.ProjectId is null)
            .OrderBy(memory => memory.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(memory => memory.Key, StringComparer.OrdinalIgnoreCase)
            .Select(memory =>
            {
                var age = memory.Source != MemorySources.Curator && memory.CreatedAt > From
                    ? $"new: saved by a chat on {memory.CreatedAt.ToLocalTime():MMM d}"
                    : $"updated {memory.UpdatedAt.ToLocalTime():MMM d}";
                return $"- [{Short(memory)}] ({memory.Category}) {memory.Key}: {ChatDigest.Excerpt(memory.Content, 500)} ({age})";
            })
            .ToList();
        return lines.Count == 0 ? "(none)" : string.Join("\n", lines);
    }

    /// <summary>Pairs of memories whose words overlap strongly and that this curator may change.</summary>
    internal string DuplicateHints()
    {
        var visible = Visible()
            .Select(memory => (Memory: memory, Terms: ChatDigest.Terms($"{memory.Key} {memory.Content}")))
            .ToList();
        var pairs = new List<(double Score, Memory A, Memory B)>();
        for (var i = 0; i < visible.Count; i++)
        {
            for (var j = i + 1; j < visible.Count; j++)
            {
                var (a, termsA) = visible[i];
                var (b, termsB) = visible[j];
                if (!Writable(a) && !Writable(b))
                    continue;
                var shared = termsA.Count(termsB.Contains);
                var union = termsA.Count + termsB.Count - shared;
                if (union > 0 && (double)shared / union >= 0.3)
                    pairs.Add(((double)shared / union, a, b));
            }
        }

        return string.Join("\n", pairs
            .OrderByDescending(pair => pair.Score)
            .Take(8)
            .Select(pair => $"- [{Short(pair.A)}] {pair.A.Key} ↔ [{Short(pair.B)}] {pair.B.Key}"));
    }

    internal string ManageMemories(string action, string? memory, string? key, string? content, string? category, string? scope, string? evidence, string? reason)
    {
        if (!CanWriteMemories)
            return "Automatic memories are turned off.";

        var normalizedContent = MemoryAgentService.NormalizeOrNull(content);
        if (normalizedContent is { Length: > MemoryHardLimit })
            return $"Too long ({normalizedContent.Length} characters). One fact, under {ContextCuratorPrompts.MemoryTargetChars} characters.";

        lock (MemoryWrites)
        {
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "create":
                    return Create(MemoryAgentService.NormalizeOrNull(key), normalizedContent, category, scope, evidence);
                case "update":
                    return Update(memory ?? key, memory is null ? null : MemoryAgentService.NormalizeOrNull(key), normalizedContent, category, evidence);
                case "delete":
                    return Delete(memory ?? key, reason);
                default:
                    return "action must be create, update or delete.";
            }
        }
    }

    private string Create(string? key, string? content, string? category, string? scope, string? evidence)
    {
        if (key is null || content is null)
            return "key and content are required.";
        if (string.IsNullOrWhiteSpace(evidence))
            return "evidence is required: the chat title, the date and the owner's words.";

        var normalizedCategory = MemoryAgentService.NormalizeCategory(category);
        if (Project is not null && string.Equals(scope?.Trim(), MemoryScopes.Global, StringComparison.OrdinalIgnoreCase))
        {
            lock (_proposals)
            {
                var proposal = new CuratorProposal($"p{_proposals.Count + 1}", Project.Name, key, content, normalizedCategory, evidence.Trim());
                _proposals.Add(proposal);
                return $"Proposed to the all-projects curator as {proposal.Id}.";
            }
        }

        var canonical = MemoryAgentService.CanonicalizeKey(key);
        if (Visible().FirstOrDefault(item => Writable(item) && MemoryAgentService.CanonicalizeKey(item.Key) == canonical) is { } existing)
        {
            Apply(existing, key: null, content, normalizedCategory);
            return $"A memory with this key existed, so it was updated: [{Short(existing)}] {existing.Key}.";
        }

        var now = DateTimeOffset.Now;
        var created = new Memory
        {
            Key = key,
            Content = content,
            Category = normalizedCategory,
            Scope = Project is null ? MemoryScopes.Global : MemoryScopes.Project,
            ProjectId = Project?.Id,
            Status = MemoryStatuses.Active,
            Source = MemorySources.Curator,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _dataStore.Data.Memories.Add(created);
        Interlocked.Increment(ref _memoryChanges);
        return $"Created [{Short(created)}] {created.Key}.";
    }

    private string Update(string? reference, string? newKey, string? content, string? category, string? evidence)
    {
        if (FindWritable(reference, out var problem) is not { } target)
            return problem;
        if (string.IsNullOrWhiteSpace(evidence))
            return "evidence is required: the chat title, the date and the owner's words.";

        Apply(target, newKey, content, category is null ? null : MemoryAgentService.NormalizeCategory(category));
        return $"Updated [{Short(target)}] {target.Key}.";
    }

    private void Apply(Memory memory, string? key, string? content, string? category)
    {
        if (key is not null)
            memory.Key = key;
        if (content is not null)
            memory.Content = content;
        if (category is not null)
            memory.Category = category;
        memory.Status = MemoryStatuses.Active;
        memory.Source = MemorySources.Curator;
        memory.UpdatedAt = DateTimeOffset.Now;
        memory.MaintenanceNote = null;
        Interlocked.Increment(ref _memoryChanges);
    }

    private string Delete(string? reference, string? reason)
    {
        if (FindWritable(reference, out var problem) is not { } target)
            return problem;
        if (string.IsNullOrWhiteSpace(reason))
            return "reason is required: obsolete, wrong, 'duplicate of [id]' or 'merged into [id]'.";

        _dataStore.Data.Memories.Remove(target);
        Interlocked.Increment(ref _memoryChanges);
        return $"Deleted [{Short(target)}] {target.Key}.";
    }

    private Memory? FindWritable(string? reference, out string problem)
    {
        problem = "";
        var value = (reference ?? "").Trim().Trim('[', ']', '"');
        if (value.Length == 0)
        {
            problem = "Give the memory's id in brackets.";
            return null;
        }

        var active = _dataStore.Data.Memories.ToArray().Where(IsActive).ToList();
        var canonical = MemoryAgentService.CanonicalizeKey(value);
        var match = active.FirstOrDefault(item => Short(item).Equals(value, StringComparison.OrdinalIgnoreCase))
                    ?? active.FirstOrDefault(item => Writable(item) && MemoryAgentService.CanonicalizeKey(item.Key) == canonical)
                    ?? active.FirstOrDefault(item => MemoryAgentService.CanonicalizeKey(item.Key) == canonical);
        if (match is null)
        {
            problem = $"No memory {value}. Use an id in brackets from the lists.";
            return null;
        }

        if (!Writable(match))
        {
            problem = Project is null
                ? "That is a project memory; only its project's curator changes it."
                : "That is a global memory. To change it, use create with scope \"global\" and the corrected content; the all-projects curator decides.";
            return null;
        }

        return match;
    }
}
