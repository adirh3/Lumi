using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
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

    private const string DefaultCategory = "General";

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

    public List<AIFunction> BuildMemoryTools(Func<Guid?> sourceChatIdProvider, string source = "chat")
    {
        return
        [
            AIFunctionFactory.Create(
                async ([Description("Brief label for the memory (e.g. Birthday, Dog's name, Prefers dark mode)")] string key,
                 [Description("Full memory text with details")] string content,
                 [Description("Category: Personal, Preferences, Work, etc. Default: General")] string? category) =>
                {
                    var sourceChatId = sourceChatIdProvider();
                    return await SaveMemoryAsync(key, content, category, sourceChatId, source).ConfigureAwait(false);
                },
                "save_memory",
                "Save or update a persistent memory about the user"),

            AIFunctionFactory.Create(
                async ([Description("Key of the memory to update")] string key,
                 [Description("New content text (optional)")] string? content,
                 [Description("New key if renaming (optional)")] string? newKey,
                 [Description("New category (optional)")] string? category) =>
                {
                    return await UpdateMemoryAsync(key, content, newKey, category, source).ConfigureAwait(false);
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

    public List<AIFunction> BuildMemoryTools(Guid? sourceChatId, string source = "chat")
    {
        return BuildMemoryTools(() => sourceChatId, source);
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
        var model = await PickLightweightModelAsync(cancellationToken).ConfigureAwait(false);

        var memoryTools = BuildMemoryTools(checkpoint.ChatId, source: "auto");
        var session = await _copilotService.CreateLightweightSessionAsync(
            BuildSystemPrompt(), model, memoryTools, cancellationToken).ConfigureAwait(false);

        try
        {
            var prompt = BuildCheckpointPrompt(checkpoint);
            await session.SendAndWaitAsync(
                new MessageOptions { Prompt = prompt },
                TimeSpan.FromSeconds(90),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string> SaveMemoryAsync(
        string key,
        string content,
        string? category,
        Guid? sourceChatId,
        string source = "chat",
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeOrNull(key);
        var normalizedContent = NormalizeOrNull(content);
        if (normalizedKey is null || normalizedContent is null)
            return "Ignored: key and content are required.";

        var normalizedCategory = NormalizeOrNull(category) ?? DefaultCategory;

        var existing = FindMemoryByKey(normalizedKey);

        if (existing is not null)
        {
            existing.Content = normalizedContent;
            existing.Category = normalizedCategory;
            existing.Source = source;
            if (sourceChatId.HasValue)
                existing.SourceChatId = sourceChatId.Value.ToString();
            existing.UpdatedAt = DateTimeOffset.Now;
        }
        else
        {
            _dataStore.Data.Memories.Add(new Memory
            {
                Key = normalizedKey,
                Content = normalizedContent,
                Category = normalizedCategory,
                Source = source,
                SourceChatId = sourceChatId?.ToString(),
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            });
        }

        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        return $"Memory saved: {normalizedKey}";
    }

    private async Task<string> UpdateMemoryAsync(
        string key,
        string? content,
        string? newKey,
        string? category,
        string source = "chat",
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeOrNull(key);
        if (normalizedKey is null)
            return "Ignored: key is required.";

        var memory = FindMemoryByKey(normalizedKey);

        if (memory is null)
            return $"Memory not found: {normalizedKey}";

        var normalizedContent = NormalizeOrNull(content);
        var normalizedNewKey = NormalizeOrNull(newKey);
        var normalizedCategory = NormalizeOrNull(category);

        if (normalizedContent is not null)
            memory.Content = normalizedContent;
        if (normalizedNewKey is not null)
            memory.Key = normalizedNewKey;
        if (normalizedCategory is not null)
            memory.Category = normalizedCategory;

        memory.Source = source;
        memory.UpdatedAt = DateTimeOffset.Now;

        await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
        return $"Memory updated: {memory.Key}";
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

    private Memory? FindMemoryByKey(string key)
    {
        var canonical = CanonicalizeKey(key);
        return _dataStore.Data.Memories.FirstOrDefault(m =>
            string.Equals(CanonicalizeKey(m.Key), canonical, StringComparison.Ordinal));
    }

    private static string BuildSystemPrompt()
    {
        return """
            You are a memory extraction agent. Your ONLY job is to extract memorable facts from conversations and save them using tools.

            You have these tools:
            - save_memory(key, content, category?) — Save a new memory or update an existing one by key.
            - update_memory(key, content?, newKey?, category?) — Update an existing memory.
            - delete_memory(key) — Remove an outdated or incorrect memory.

            INSTRUCTIONS:
            1. Read the conversation below.
            2. Identify any durable personal facts, preferences, or important details worth remembering for future conversations.
            3. For each fact worth saving, call save_memory with a short descriptive key, the full detail as content, and a category.
            4. If a fact contradicts or updates an existing memory, call update_memory or delete_memory first.
            5. If nothing new or memorable was discussed, make NO tool calls.
            6. Before creating a new key, check existing memory keys and reuse/update the closest matching key when possible.
            7. Never create multiple keys for the same underlying fact (for example, don't create both "Partner" and "Significant other").

            Good memory examples: user's name, birthday, pet names, job title, preferences (dark mode, favorite language), family members, hobbies, important dates.
            Bad memory examples: what task the user asked for, temporary debugging issues, code they wrote, transient conversation details.

            After processing, respond with exactly: MEMORY_SYNC_DONE
            """;
    }

    private static string BuildCheckpointPrompt(MemoryAgentCheckpoint checkpoint)
    {
        var sb = new StringBuilder();

        // Current user info
        if (!string.IsNullOrWhiteSpace(checkpoint.UserName))
        {
            sb.Append("The user's name is: ");
            sb.AppendLine(checkpoint.UserName);
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
        sb.AppendLine("Extract any memorable facts from this conversation. Reuse existing keys whenever possible and avoid creating near-duplicate keys. Call save_memory for each new fact, update_memory for corrections, or delete_memory for outdated info. If nothing is worth remembering, just respond MEMORY_SYNC_DONE.");

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

    private static string? NormalizeOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string CanonicalizeKey(string key)
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
}

public sealed class MemoryAgentCheckpoint
{
    public Guid ChatId { get; init; }
    public string InteractionSignature { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
    public string AssistantMessage { get; init; } = string.Empty;
    public string? UserName { get; init; }
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
}

public sealed class MemoryAgentConversationItem
{
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}
