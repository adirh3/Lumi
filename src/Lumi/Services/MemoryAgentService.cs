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
using GitHub.Copilot;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public sealed class MemoryAgentService
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly object _checkpointSync = new();
    private readonly Dictionary<Guid, string> _lastCheckpointByChat = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _checkpointLocks = new();

    /// <summary>Global limit on concurrent background sessions (memory + suggestions)
    /// to prevent overwhelming the CLI process when multiple chats run in parallel.</summary>
    private static readonly SemaphoreSlim _globalSessionGate = new(2, 2);

    internal const string DefaultCategory = "General";
    private const int MaxMemoryContentLength = 700;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex FilePathRegex = new(
        @"(?:[a-z]:\\|\\\\|(?:^|\s)\.{1,2}[\\/][A-Za-z0-9_.-]+|(?:^|\s)/(?:users|home|mnt|var|etc|tmp)\b|\.slnx?\b|\.csproj\b|\.json\b|\.md\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] DurableValueSignals =
    [
        "my favorite", "favorite ", "i prefer", "i usually prefer", "i like", "i love", "i dislike", "i hate",
        "prefers ", "preferred ", "likes ", "dislikes ", "favorite food", "favorite color", "favorite movie",
        "favorite programming language", "preferred ide", "preferred editor", "preferred theme",
        "my name is", "call me", "go by ", "birthday", "born on", "live in", "lives in", "i'm from",
        "i am from", "location", "wife", "husband", "partner", "fiance", "fiancée", "engaged to",
        "mother", "father", "parent", "brother", "sister", "son", "daughter", "dog", "cat", "pet",
        "works as", "work as", "works at", "work at", "job title",
        "senior engineer", "software engineer", "developer", "designer", "product manager",
        "my hobby", "hobby is", "interests", "outside-work interests", "enjoys ", "i enjoy", "goal is",
        "dream skill", "way to unwind", "unwinds", "work motivation", "wants lumi", "preferred lumi help",
        "help with most"
    ];

    private static readonly string[] MemoryCorrectionSignals =
    [
        "forget that", "forget my", "don't remember", "do not remember", "stop remembering",
        "remove the memory", "delete the memory", "not true", "isn't true", "is no longer",
        "actually,", "correction", "update your memory", "remember instead"
    ];

    private static readonly string[] DurableProjectSignals =
    [
        "always", "never", "prefer", "prefers", "preferred", "use ", "uses ", "should use",
        "convention", "pattern", "workflow", "architecture", "constraint", "project rule",
        "for this project", "in this project", "in lumi", "for lumi", "when working on"
    ];

    private static readonly string[] TechnicalNoiseSignals =
    [
        "repo", "repository", "codebase", "worktree", "branch", "uncommitted", "pull request", " pr ",
        "commit", "git config", "git username", "safe.directory", "source repo", "working directory",
        "file path", "folder", "directory", "solution", "project file", "class ", "method ",
        "viewmodel", "xaml", "csproj", "sln", "build", "restore", "pack", "publish", "test failure",
        "debug", "bug", "stack trace", "exception", "error", "tool call", "mcp", "cli", "sdk",
        "powershell history", "command history", "vs code extensions", "extension", "package references",
        "frameworks", "submodule", "current task", "current project", "active project", "session state",
        "transcript", "helper session", "marker:"
    ];

    private static readonly string[] EphemeralSignals =
    [
        "currently", "right now", "today", "yesterday", "tomorrow", "this week", "last week", "next week",
        "for now", "temporary", "recently", "active branch", "current branch", "current activity",
        "working on", "asked for", "asked me", "the assistant", "lumi did", "in this chat", "this conversation"
    ];

    private static readonly string[] HardRejectSignals =
    [
        "memory_sync_done", "memchk_", "techchk_", "ignore test marker", "test marker",
        "system prompt", "developer instruction", "assistant message", "user message"
    ];

    private static void Log(string message) => Debug.WriteLine($"[MemoryAgent] {message}");

    public MemoryAgentService(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
    }

    public List<AIFunction> BuildRecallMemoryTools()
    {
        return [BuildRecallMemoryTool()];
    }

    public List<AIFunction> BuildMemoryTools(
        Func<Guid?> sourceChatIdProvider,
        string source = "chat",
        Func<Guid?>? sourceProjectIdProvider = null)
    {
        return
        [
            AIFunctionFactory.Create(
                async ([Description("Brief label for the memory (e.g. Birthday, Dog's name, Prefers dark mode)")] string key,
                  [Description("Full memory text with details")] string content,
                  [Description("Category: Personal, Preferences, Work, Project, etc. Default: General")] string? category,
                  [Description("Scope: global for user-wide memories, project for durable project-only conventions")] string? scope) =>
                {
                    var sourceChatId = sourceChatIdProvider();
                    return await SaveMemoryAsync(
                        key,
                        content,
                        category,
                        sourceChatId,
                        source,
                        scope: scope,
                        projectId: sourceProjectIdProvider?.Invoke()).ConfigureAwait(false);
                },
                "save_memory",
                "Save or update a persistent memory about the user"),

            AIFunctionFactory.Create(
                async ([Description("Key of the memory to update")] string key,
                  [Description("New content text (optional)")] string? content,
                  [Description("New key if renaming (optional)")] string? newKey,
                  [Description("New category (optional)")] string? category,
                  [Description("New scope: global or project (optional)")] string? scope) =>
                {
                    return await UpdateMemoryAsync(
                        key,
                        content,
                        newKey,
                        category,
                        source,
                        scope: scope,
                        projectId: sourceProjectIdProvider?.Invoke()).ConfigureAwait(false);
                },
                "update_memory",
                "Update an existing memory's content, key, or category"),

            AIFunctionFactory.Create(
                async ([Description("Key of the memory to remove")] string key) =>
                {
                    return await DeleteMemoryAsync(key).ConfigureAwait(false);
                },
                "delete_memory",
                "Remove a memory that is no longer relevant"),

            BuildRecallMemoryTool(),
        ];
    }

    public List<AIFunction> BuildMemoryTools(
        Guid? sourceChatId,
        string source = "chat",
        Guid? sourceProjectId = null)
    {
        return BuildMemoryTools(() => sourceChatId, source, () => sourceProjectId);
    }

    public async Task ProcessCheckpointAsync(MemoryAgentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        if (!checkpoint.IsValid)
            return;

        var gate = GetCheckpointGate(checkpoint.ChatId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCheckpointAlreadyProcessed(checkpoint.ChatId, checkpoint.InteractionSignature))
                return;

            if (!ShouldAnalyzeCheckpoint(checkpoint))
            {
                MarkCheckpointProcessed(checkpoint.ChatId, checkpoint.InteractionSignature);
                return;
            }

            await RunMemoryAgentAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            MarkCheckpointProcessed(checkpoint.ChatId, checkpoint.InteractionSignature);
        }
        catch (OperationCanceledException)
        {
            // Best-effort cancellation.
        }
        catch (Exception ex)
        {
            Log($"Checkpoint failed: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunMemoryAgentAsync(MemoryAgentCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        // Limit concurrent background sessions to avoid starving the CLI process
        // when multiple chats run in parallel.
        if (!await _globalSessionGate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
        {
            Log("Skipped checkpoint — global session limit reached");
            return;
        }

        try
        {
            var model = await PickLightweightModelAsync(cancellationToken).ConfigureAwait(false);
            var memoryTools = BuildMemoryTools(
                checkpoint.ChatId,
                source: "auto",
                checkpoint.ProjectId);
            await _copilotService.UseLightweightSessionAsync(
                new LightweightSessionOptions
                {
                    SystemPrompt = BuildSystemPrompt(),
                    Model = model,
                    Streaming = true,
                    Tools = memoryTools
                },
                async (session, innerCt) =>
                {
                var prompt = BuildCheckpointPrompt(checkpoint);
                await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = prompt },
                    TimeSpan.FromSeconds(90),
                    innerCt).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _globalSessionGate.Release();
        }
    }

    internal async Task<string> SaveMemoryAsync(
        string key,
        string content,
        string? category,
        Guid? sourceChatId = null,
        string source = "chat",
        CancellationToken cancellationToken = default,
        string? scope = null,
        Guid? projectId = null)
    {
        var normalizedKey = NormalizeOrNull(key);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedKey is null || normalizedContent is null)
            return "Ignored: key and content are required.";

        var normalizedCategory = NormalizeCategory(category);
        var normalizedScope = NormalizeScope(scope, projectId);
        var quality = EvaluateMemoryCandidate(normalizedKey, normalizedContent, normalizedCategory, normalizedScope);
        if (!quality.ShouldSave)
            return $"Ignored: {quality.Reason}";

        var memoryProjectId = normalizedScope == MemoryScopes.Project ? projectId : null;
        var existing = FindMemoryByKey(normalizedKey, normalizedScope, memoryProjectId);
        var matchedByRelatedTopic = false;
        if (existing is null)
        {
            existing = FindRelatedMemory(normalizedKey, normalizedContent, normalizedCategory, normalizedScope, memoryProjectId);
            matchedByRelatedTopic = existing is not null;
        }

        if (existing is not null)
        {
            if (!matchedByRelatedTopic
                && string.Equals(existing.Content, normalizedContent, StringComparison.Ordinal)
                && string.Equals(existing.Category, normalizedCategory, StringComparison.Ordinal))
            {
                return $"Memory already saved: {existing.Key}";
            }

            if (matchedByRelatedTopic)
                existing.Key = normalizedKey;
            existing.Content = normalizedContent;
            existing.Category = normalizedCategory;
            existing.Scope = normalizedScope;
            existing.ProjectId = memoryProjectId;
            existing.Status = MemoryStatuses.Active;
            existing.Source = source;
            if (sourceChatId.HasValue)
                existing.SourceChatId = sourceChatId.Value.ToString();
            existing.UpdatedAt = DateTimeOffset.Now;
            existing.Confidence = quality.Confidence;
            existing.MaintenanceNote = null;
        }
        else
        {
            _dataStore.Data.Memories.Add(new Memory
            {
                Key = normalizedKey,
                Content = normalizedContent,
                Category = normalizedCategory,
                Scope = normalizedScope,
                ProjectId = memoryProjectId,
                Status = MemoryStatuses.Active,
                Source = source,
                SourceChatId = sourceChatId?.ToString(),
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now,
                Confidence = quality.Confidence
            });
        }

        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        await RunAutomaticMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        return $"Memory saved: {normalizedKey}";
    }

    internal async Task<string> UpdateMemoryAsync(
        string key,
        string? content,
        string? newKey,
        string? category,
        string source = "chat",
        CancellationToken cancellationToken = default,
        string? scope = null,
        Guid? projectId = null)
    {
        var normalizedKey = NormalizeOrNull(key);
        if (normalizedKey is null)
            return "Ignored: key is required.";

        var requestedScope = NormalizeOrNull(scope) is null ? null : NormalizeScope(scope, projectId);
        var memory = FindMemoryByKey(normalizedKey, requestedScope, requestedScope == MemoryScopes.Project ? projectId : null);

        if (memory is null)
            return $"Memory not found: {normalizedKey}";

        var normalizedContent = NormalizeOrNull(content);
        var normalizedNewKey = NormalizeOrNull(newKey);
        var normalizedCategory = NormalizeOrNull(category) is { } providedCategory
            ? NormalizeCategory(providedCategory)
            : null;

        var candidateKey = normalizedNewKey ?? memory.Key;
        var candidateContent = normalizedContent ?? memory.Content;
        var candidateCategory = normalizedCategory ?? memory.Category;
        var candidateScope = requestedScope ?? NormalizeScope(memory.Scope, memory.ProjectId);
        var candidateProjectId = candidateScope == MemoryScopes.Project ? projectId ?? memory.ProjectId : null;
        var quality = EvaluateMemoryCandidate(candidateKey, candidateContent, candidateCategory, candidateScope);
        if (!quality.ShouldSave)
            return $"Ignored: {quality.Reason}";

        if (normalizedContent is not null)
            memory.Content = normalizedContent;
        if (normalizedNewKey is not null)
            memory.Key = normalizedNewKey;
        if (normalizedCategory is not null)
            memory.Category = normalizedCategory;
        if (requestedScope is not null)
        {
            memory.Scope = requestedScope;
            memory.ProjectId = candidateProjectId;
        }

        memory.Source = source;
        memory.Status = MemoryStatuses.Active;
        memory.UpdatedAt = DateTimeOffset.Now;
        memory.Confidence = quality.Confidence;
        memory.MaintenanceNote = null;

        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        await RunAutomaticMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        return $"Memory updated: {memory.Key}";
    }

    private async Task RunAutomaticMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (!_dataStore.Data.Settings.EnableMemoryAutoMaintenance)
            return;

        var maintenance = new MemoryMaintenanceService(_dataStore);
        await maintenance.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DeleteMemoryAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeOrNull(key);
        if (normalizedKey is null)
            return "Ignored: key is required.";

        var memory = FindMemoryByKey(normalizedKey);

        if (memory is null)
            return $"Memory not found: {normalizedKey}";

        _dataStore.Data.Memories.Remove(memory);
        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        return $"Memory deleted: {normalizedKey}";
    }

    private string RecallMemory(string key)
    {
        var normalizedKey = NormalizeOrNull(key);
        if (normalizedKey is null)
            return "Ignored: key is required.";

        var memory = FindMemoryByKey(normalizedKey);

        return memory?.Content ?? $"Memory not found: {normalizedKey}";
    }

    private AIFunction BuildRecallMemoryTool()
    {
        return AIFunctionFactory.Create(
            ([Description("Key of the memory to retrieve full content for")] string key) =>
            {
                return RecallMemory(key);
            },
            "recall_memory",
            "Fetch the full content of a memory by its key");
    }

    private Memory? FindMemoryByKey(string key, string? scope = null, Guid? projectId = null)
    {
        var canonical = CanonicalizeKey(key);
        return _dataStore.Data.Memories.FirstOrDefault(m =>
            string.Equals(CanonicalizeKey(m.Key), canonical, StringComparison.Ordinal)
            && MatchesMemoryScope(m, scope, projectId));
    }

    private Memory? FindRelatedMemory(string key, string content, string category, string scope, Guid? projectId)
    {
        var topic = ExtractMemoryTopic(key, content, category);
        if (topic is null)
            return null;

        return _dataStore.Data.Memories.FirstOrDefault(memory =>
            MatchesMemoryScope(memory, scope, projectId)
            && string.Equals(memory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase)
            &&
            string.Equals(
                ExtractMemoryTopic(memory.Key, memory.Content, memory.Category),
                topic,
                StringComparison.Ordinal));
    }

    private static bool MatchesMemoryScope(Memory memory, string? scope, Guid? projectId)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return true;

        var normalizedMemoryScope = NormalizeScope(memory.Scope, memory.ProjectId);
        if (!string.Equals(normalizedMemoryScope, scope, StringComparison.OrdinalIgnoreCase))
            return false;

        return scope != MemoryScopes.Project || memory.ProjectId == projectId;
    }

    internal static string BuildSystemPrompt()
    {
        return """
            You are Lumi's memory extraction agent. Your ONLY job is to decide whether a conversation contains a rare, meaningful, durable fact — and if so, save or update one concise memory.

            You have these tools:
            - save_memory(key, content, category?, scope?) — Save a new memory. scope must be "global" or "project"; the app will reject noisy, temporary, or wrongly-scoped memories.
            - update_memory(key, content?, newKey?, category?, scope?) — Update an existing memory.
            - delete_memory(key) — Remove an outdated or incorrect memory.

            CRITICAL RULES — BE EXTREMELY SELECTIVE:
            Most conversations have NOTHING worth saving. No tool call is the normal outcome.
            Only save a memory if ALL of these are true:
            1. It is a durable personal fact about the user as a person — something useful months from now.
            2. It was explicitly stated by the user or directly chosen by the user (not inferred, speculated, or based on assistant/tool observations).
            3. It is NOT already covered by an existing memory.
            4. It can be phrased as one stable profile fact, not a transcript summary.

            SAVE these (rare):
            - Name, birthday, family members, pet names
            - Job title or career (not specific projects or codebases)
            - Lasting personal preferences (favorite food, color, programming language, IDE, theme)
            - Important life facts (where they live, significant relationships)
            - Long-term interests, hobbies, goals, or what they want Lumi to help with
            - Durable project-only conventions or constraints, but ONLY with scope="project" and only when an active project is shown below

            NEVER SAVE any of these:
            - What task the user asked for or what they're working on
            - Current code/repo state, file paths, branches, working directories, or directory contents
            - Technical complaints, bugs, latency issues, or tool behavior
            - Temporary project descriptions, architecture facts that are not stable conventions, or codebase snapshots
            - The user's current context, active project, or working directory
            - What the assistant did or said
            - Git config, toolchain inventories, installed extensions, command history, branch/worktree state
            - Anything speculative or inferred from behavior ("seems to prefer", "likely stays up late")
            - Conversational style preferences ("prefers thoughtful responses")
            - Facts about files, documents, or folder contents the user has
            - Test markers, message IDs, session IDs, or anything the user explicitly says to ignore

            BEFORE saving, check existing memories:
            - If the fact is already captured, make NO tool calls.
            - If the user corrects or replaces a saved fact, call update_memory.
            - If the user explicitly asks Lumi to forget a saved fact, call delete_memory.
            - Never create a second memory for the same topic under a different key.

            Memory writing style:
            - Use direct facts: "Adir prefers dark theme." not "When asked, Adir selected dark theme."
            - Keep content concise and human-readable.
            - Use categories: Personal, Preferences, Work, Interests, Goals, Project.
            - Avoid "Technical" unless it is clearly a durable personal preference like favorite programming language or preferred IDE.
            - Use scope="global" for facts useful everywhere.
            - Use scope="project" only for durable project-specific rules/preferences/conventions. The app will force global scope if no active project exists.

            When in doubt, do NOT save. Silence is the correct default.

            After processing, respond with exactly: MEMORY_SYNC_DONE
            """;
    }

    internal static string BuildCheckpointPrompt(MemoryAgentCheckpoint checkpoint)
    {
        var sb = new StringBuilder();

        // Current user info
        if (!string.IsNullOrWhiteSpace(checkpoint.UserName))
        {
            sb.Append("The user's name is: ");
            sb.AppendLine(checkpoint.UserName);
            sb.AppendLine();
        }

        if (checkpoint.ProjectId.HasValue && !string.IsNullOrWhiteSpace(checkpoint.ProjectName))
        {
            sb.Append("Active project: ");
            sb.AppendLine(checkpoint.ProjectName);
            sb.AppendLine("If the user states a durable convention, preference, or constraint that should apply only inside this project, save it with scope=\"project\". Otherwise use scope=\"global\".");
            sb.AppendLine();
        }

        // Existing memories for dedup/update awareness
        sb.AppendLine("== EXISTING MEMORIES ==");
        if (checkpoint.ExistingMemories.Count == 0)
        {
            sb.AppendLine("(none saved yet)");
        }
        else
        {
            foreach (var memory in checkpoint.ExistingMemories
                         .OrderBy(m => m.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(200))
            {
                sb.Append('[');
                sb.Append(string.IsNullOrWhiteSpace(memory.Category) ? DefaultCategory : memory.Category);
                sb.Append("] [");
                sb.Append(string.IsNullOrWhiteSpace(memory.Scope) ? MemoryScopes.Global : memory.Scope);
                if (memory.ProjectId.HasValue)
                    sb.Append(':').Append(memory.ProjectId.Value.ToString("N"));
                sb.Append("] ");
                sb.Append(memory.Key);
                sb.Append(" = ");
                sb.AppendLine(TrimForPrompt(memory.Content, 400));
            }
        }

        // Conversation to analyze
        sb.AppendLine();
        sb.AppendLine("== CONVERSATION TO ANALYZE ==");
        foreach (var turn in checkpoint.RecentConversation)
        {
            var label = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Lumi" : "User";
            sb.Append(label);
            sb.Append(": ");
            sb.AppendLine(TrimForPrompt(turn.Content, 1000));
        }

        sb.AppendLine();
        sb.AppendLine("Analyze this conversation. Save or update a memory only if the user explicitly revealed a lasting personal fact about themselves (name, birthday, family, job title/career, hobby, goal, lasting preference) or a durable project-only convention/constraint for the active project. Do NOT save tasks, files, tool output, branch/repo state, installed tools, working context, or current implementation details. Most conversations have nothing worth saving — just respond MEMORY_SYNC_DONE.");

        return sb.ToString();
    }

    private async Task<string?> PickLightweightModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = await _copilotService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            var modelIds = models.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (modelIds.Count == 0)
                return _dataStore.Data.Settings.PreferredModel;

            return modelIds
                .OrderByDescending(ScoreLightweightModel)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .First();
        }
        catch
        {
            return _dataStore.Data.Settings.PreferredModel;
        }
    }

    private static int ScoreLightweightModel(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        var score = 0;

        if (id.Contains("mini")) score += 700;
        if (id.Contains("haiku")) score += 650;
        if (id.Contains("flash")) score += 600;
        if (id.Contains("fast")) score += 550;
        if (id.Contains("small")) score += 500;
        if (id.Contains("nano")) score += 450;

        if (id.Contains("sonnet")) score += 220;
        if (id.Contains("gpt-4.1")) score += 180;
        if (id.Contains("gpt-5")) score += 140;

        if (id.Contains("preview")) score -= 40;
        if (id.Contains("pro")) score -= 220;
        if (id.Contains("opus")) score -= 280;

        return score;
    }

    private SemaphoreSlim GetCheckpointGate(Guid chatId)
    {
        lock (_checkpointSync)
        {
            if (_checkpointLocks.TryGetValue(chatId, out var gate))
                return gate;

            gate = new SemaphoreSlim(1, 1);
            _checkpointLocks[chatId] = gate;
            return gate;
        }
    }

    private bool IsCheckpointAlreadyProcessed(Guid chatId, string interactionSignature)
    {
        lock (_checkpointSync)
        {
            return _lastCheckpointByChat.TryGetValue(chatId, out var last)
                   && string.Equals(last, interactionSignature, StringComparison.Ordinal);
        }
    }

    private void MarkCheckpointProcessed(Guid chatId, string interactionSignature)
    {
        lock (_checkpointSync)
            _lastCheckpointByChat[chatId] = interactionSignature;
    }

    internal static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = WhitespaceRegex.Replace(value.Trim(), " ");
        return trimmed.Length == 0 ? null : trimmed;
    }

    internal static string NormalizeCategory(string? category)
    {
        var normalized = NormalizeOrNull(category) ?? DefaultCategory;
        return normalized.ToLower(CultureInfo.InvariantCulture) switch
        {
            "personal" => "Personal",
            "preference" or "preferences" => "Preferences",
            "work" or "career" => "Work",
            "project" or "projects" => "Project",
            "interest" or "interests" or "hobby" or "hobbies" => "Interests",
            "goal" or "goals" => "Goals",
            "technical" => "Technical",
            "general" => DefaultCategory,
            _ => normalized.Length > 40 ? DefaultCategory : normalized
        };
    }

    internal static string NormalizeScope(string? scope, Guid? projectId)
    {
        var normalized = NormalizeOrNull(scope)?.ToLower(CultureInfo.InvariantCulture);
        return normalized is MemoryScopes.Project or "project-only" or "project_only"
               && projectId.HasValue
            ? MemoryScopes.Project
            : MemoryScopes.Global;
    }

    internal static string CanonicalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        // Collapse casing and punctuation differences so "Dog's name" and "dogs name" resolve together.
        var chars = key.Trim().ToLower(CultureInfo.InvariantCulture)
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static string TrimForPrompt(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;

        return trimmed[..maxLength] + "...";
    }

    internal static bool ShouldAnalyzeCheckpoint(MemoryAgentCheckpoint checkpoint)
    {
        if (!checkpoint.IsValid)
            return false;

        var userText = string.Join(
            "\n",
            checkpoint.RecentConversation
                .Where(item => item.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Content));

        if (string.IsNullOrWhiteSpace(userText))
            userText = checkpoint.UserMessage;

        var normalized = NormalizeForDecision(userText);
        if (normalized.Length == 0)
            return false;

        if (ContainsAny(normalized, MemoryCorrectionSignals))
            return true;

        var hasDurableSignal = ContainsAny(normalized, DurableValueSignals);
        var hasProjectDurableSignal = checkpoint.ProjectId.HasValue && ContainsAny(normalized, DurableProjectSignals);
        if (!hasDurableSignal && !hasProjectDurableSignal)
            return false;

        var hasTechnicalNoise = ContainsAny(normalized, TechnicalNoiseSignals) || FilePathRegex.IsMatch(userText);
        if (hasTechnicalNoise && !hasProjectDurableSignal && !LooksLikeDurableTechnicalPreference(normalized))
            return false;

        return true;
    }

    internal static MemoryCandidateEvaluation EvaluateMemoryCandidate(
        string key,
        string content,
        string? category,
        string? scope = null)
    {
        var normalizedKey = NormalizeOrNull(key);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedKey is null || normalizedContent is null)
            return MemoryCandidateEvaluation.Reject("key and content are required");

        if (normalizedContent.Length > MaxMemoryContentLength)
            return MemoryCandidateEvaluation.Reject("memory content is too long");

        var normalizedCategory = NormalizeCategory(category);
        var requestedScope = NormalizeOrNull(scope)?.ToLower(CultureInfo.InvariantCulture);
        var normalizedScope = requestedScope is MemoryScopes.Project or "project-only" or "project_only"
            ? MemoryScopes.Project
            : MemoryScopes.Global;
        var combined = NormalizeForDecision($"{normalizedKey} {normalizedCategory} {normalizedContent}");

        if (ContainsAny(combined, HardRejectSignals))
            return MemoryCandidateEvaluation.Reject("memory contains helper/test artifacts");

        if (FilePathRegex.IsMatch(normalizedContent) || FilePathRegex.IsMatch(normalizedKey))
            return MemoryCandidateEvaluation.Reject("memory looks like a file, path, or project artifact");

        var hasTechnicalNoise = ContainsAny(combined, TechnicalNoiseSignals);
        var hasEphemeralNoise = ContainsAny(combined, EphemeralSignals);
        if (hasEphemeralNoise && !LooksLikeDurableTechnicalPreference(combined))
            return MemoryCandidateEvaluation.Reject("memory looks temporary, task-specific, or technical");

        if (normalizedScope == MemoryScopes.Project)
        {
            if (HasDurableProjectSignal(combined))
                return MemoryCandidateEvaluation.Accept(88);

            return MemoryCandidateEvaluation.Reject("project memory is not a durable project convention or constraint");
        }

        if (hasTechnicalNoise && !LooksLikeDurableTechnicalPreference(combined))
            return MemoryCandidateEvaluation.Reject("memory looks temporary, task-specific, or technical");

        if (!HasDurableValueSignal(combined, normalizedCategory))
            return MemoryCandidateEvaluation.Reject("memory is not a durable user profile fact");

        return MemoryCandidateEvaluation.Accept();
    }

    private static bool HasDurableProjectSignal(string combined)
    {
        if (!ContainsAny(combined, DurableProjectSignals))
            return false;

        return !combined.Contains("branch", StringComparison.Ordinal)
               && !combined.Contains("uncommitted", StringComparison.Ordinal)
               && !combined.Contains("current task", StringComparison.Ordinal)
               && !combined.Contains("currently", StringComparison.Ordinal)
               && !combined.Contains("right now", StringComparison.Ordinal)
               && !combined.Contains("today", StringComparison.Ordinal)
               && !combined.Contains("this chat", StringComparison.Ordinal)
               && !combined.Contains("this conversation", StringComparison.Ordinal);
    }

    private static bool HasDurableValueSignal(string combined, string category)
    {
        if (ContainsAny(combined, DurableValueSignals))
            return true;

        if (combined.StartsWith(" name ", StringComparison.Ordinal)
            || combined.Contains(" name personal ", StringComparison.Ordinal)
            || combined.Contains(" key name ", StringComparison.Ordinal)
            || combined.Contains(" category personal name ", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeDurableTechnicalPreference(string combined)
    {
        var hasPreference = combined.Contains("favorite", StringComparison.Ordinal)
                            || combined.Contains("prefer", StringComparison.Ordinal)
                            || combined.Contains("preferred", StringComparison.Ordinal)
                            || combined.Contains("likes ", StringComparison.Ordinal);
        if (!hasPreference)
            return false;

        var hasAllowedTopic = combined.Contains("programming language", StringComparison.Ordinal)
                              || combined.Contains(" ide ", StringComparison.Ordinal)
                              || combined.Contains("editor", StringComparison.Ordinal)
                              || combined.Contains("theme", StringComparison.Ordinal)
                              || combined.Contains("terminal", StringComparison.Ordinal)
                              || combined.Contains("shell", StringComparison.Ordinal);
        if (!hasAllowedTopic)
            return false;

        return !combined.Contains("extension", StringComparison.Ordinal)
               && !combined.Contains("repo", StringComparison.Ordinal)
               && !combined.Contains("branch", StringComparison.Ordinal)
               && !combined.Contains("worktree", StringComparison.Ordinal)
               && !combined.Contains("currently", StringComparison.Ordinal);
    }

    internal static string? ExtractMemoryTopic(string key, string content, string category)
    {
        var combined = NormalizeForDecision($"{key} {category} {content}");

        if (combined.Contains("programming language", StringComparison.Ordinal))
            return "preference:programming-language";
        if (combined.Contains("preferred ide", StringComparison.Ordinal)
            || combined.Contains(" ide ", StringComparison.Ordinal)
            || combined.Contains("code editor", StringComparison.Ordinal)
            || combined.Contains(" editor ", StringComparison.Ordinal))
        {
            return "preference:editor";
        }
        if (combined.Contains("theme", StringComparison.Ordinal))
            return "preference:theme";
        if (combined.Contains("movie genre", StringComparison.Ordinal))
            return "preference:movie-genre";
        if (combined.Contains("favorite color", StringComparison.Ordinal))
            return "preference:color";
        if (combined.Contains("favorite food", StringComparison.Ordinal)
            || combined.Contains("likes burgers", StringComparison.Ordinal)
            || combined.Contains("likes pizza", StringComparison.Ordinal)
            || combined.Contains(" food ", StringComparison.Ordinal))
        {
            return "preference:food";
        }
        if (combined.Contains("lumi help", StringComparison.Ordinal)
            || combined.Contains("help with most", StringComparison.Ordinal))
        {
            return "goal:lumi-help";
        }
        if (combined.Contains("work motivation", StringComparison.Ordinal)
            || combined.Contains("enjoy most about work", StringComparison.Ordinal))
        {
            return "work:motivation";
        }
        if (combined.Contains("partner", StringComparison.Ordinal)
            || combined.Contains("fiance", StringComparison.Ordinal)
            || combined.Contains("fiancée", StringComparison.Ordinal))
        {
            return "personal:partner";
        }
        if (combined.Contains("mother", StringComparison.Ordinal))
            return "personal:mother";
        if (combined.Contains("father", StringComparison.Ordinal))
            return "personal:father";
        if (combined.Contains("lives in", StringComparison.Ordinal)
            || combined.Contains("location", StringComparison.Ordinal))
        {
            return "personal:location";
        }
        if (combined.Contains("job title", StringComparison.Ordinal)
            || combined.Contains("work as", StringComparison.Ordinal)
            || combined.Contains("works as", StringComparison.Ordinal)
            || combined.Contains("current job", StringComparison.Ordinal))
        {
            return "work:job";
        }

        return null;
    }

    private static string NormalizeForDecision(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lower = text.ToLower(CultureInfo.InvariantCulture)
            .Replace('’', '\'')
            .Replace('`', '\'');
        lower = Regex.Replace(lower, @"[^\p{L}\p{Nd}#+/\\.'-]+", " ");
        return WhitespaceRegex.Replace($" {lower} ", " ");
    }

    private static bool ContainsAny(string normalizedHaystack, IEnumerable<string> needles)
    {
        foreach (var needle in needles)
        {
            if (normalizedHaystack.Contains(NormalizeForDecision(needle), StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

public sealed record MemoryCandidateEvaluation(bool ShouldSave, string Reason, int Confidence)
{
    public static MemoryCandidateEvaluation Accept(int confidence = 95) => new(true, "accepted", confidence);

    public static MemoryCandidateEvaluation Reject(string reason) => new(false, reason, 0);
}

public sealed class MemoryAgentCheckpoint
{
    public Guid ChatId { get; init; }
    public string InteractionSignature { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
    public string AssistantMessage { get; init; } = string.Empty;
    public string? UserName { get; init; }
    public Guid? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public List<MemoryAgentSnapshot> ExistingMemories { get; init; } = [];
    public List<MemoryAgentConversationItem> RecentConversation { get; init; } = [];

    public bool IsValid =>
        ChatId != Guid.Empty
        && !string.IsNullOrWhiteSpace(InteractionSignature)
        && !string.IsNullOrWhiteSpace(UserMessage)
        && !string.IsNullOrWhiteSpace(AssistantMessage);
}

public sealed class MemoryAgentSnapshot
{
    public string Key { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Category { get; init; } = "General";
    public string Scope { get; init; } = MemoryScopes.Global;
    public Guid? ProjectId { get; init; }
}

public sealed class MemoryAgentConversationItem
{
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}
