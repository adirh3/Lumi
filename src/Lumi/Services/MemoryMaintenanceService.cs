using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

public sealed class MemoryMaintenanceService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly DataStore _dataStore;

    public MemoryMaintenanceService(DataStore dataStore)
    {
        _dataStore = dataStore;
    }

    public async Task<MemoryMaintenanceResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var result = new MemoryMaintenanceResult();
        var now = DateTimeOffset.Now;
        var memories = _dataStore.Data.Memories;
        var toRemove = new List<Memory>();
        var changed = false;

        foreach (var memory in memories.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Reviewed++;

            if (NormalizeMemoryFields(memory, now))
                changed = true;

            if (TryRewriteMemory(memory, now))
            {
                result.Rewritten++;
                changed = true;
            }

            var quality = MemoryAgentService.EvaluateMemoryCandidate(
                memory.Key,
                memory.Content,
                memory.Category,
                memory.Scope);

            if (!quality.ShouldSave)
            {
                if (ShouldDeleteRejectedMemory(memory))
                {
                    toRemove.Add(memory);
                    result.Deleted++;
                }
                else if (!string.Equals(memory.Status, MemoryStatuses.Archived, StringComparison.OrdinalIgnoreCase))
                {
                    memory.Status = MemoryStatuses.Archived;
                    memory.MaintenanceNote = $"Archived during cleanup: {quality.Reason}.";
                    memory.LastReviewedAt = now;
                    memory.UpdatedAt = now;
                    memory.Confidence = 0;
                    result.Archived++;
                    changed = true;
                }
                else if (memory.LastReviewedAt != now)
                {
                    memory.LastReviewedAt = now;
                    changed = true;
                }

                continue;
            }

            if (memory.Confidence != quality.Confidence)
            {
                memory.Confidence = quality.Confidence;
                changed = true;
            }

            if (memory.LastReviewedAt != now)
            {
                memory.LastReviewedAt = now;
                changed = true;
            }
        }

        foreach (var memory in toRemove)
        {
            memories.Remove(memory);
            changed = true;
        }

        result.Merged = MergeDuplicates(memories, now);
        changed |= result.Merged > 0;
        result.Active = memories.Count(m => string.Equals(m.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase));

        if (changed)
            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static bool NormalizeMemoryFields(Memory memory, DateTimeOffset now)
    {
        var changed = false;

        var normalizedKey = MemoryAgentService.NormalizeOrNull(memory.Key) ?? "";
        if (!string.Equals(memory.Key, normalizedKey, StringComparison.Ordinal))
        {
            memory.Key = normalizedKey;
            changed = true;
        }

        var normalizedContent = MemoryAgentService.NormalizeOrNull(memory.Content) ?? "";
        if (!string.Equals(memory.Content, normalizedContent, StringComparison.Ordinal))
        {
            memory.Content = normalizedContent;
            changed = true;
        }

        var normalizedCategory = MemoryAgentService.NormalizeCategory(memory.Category);
        if (!string.Equals(memory.Category, normalizedCategory, StringComparison.Ordinal))
        {
            memory.Category = normalizedCategory;
            changed = true;
        }

        var normalizedScope = MemoryAgentService.NormalizeScope(memory.Scope, memory.ProjectId);
        if (!string.Equals(memory.Scope, normalizedScope, StringComparison.Ordinal))
        {
            memory.Scope = normalizedScope;
            changed = true;
        }

        if (normalizedScope == MemoryScopes.Global && memory.ProjectId.HasValue)
        {
            memory.ProjectId = null;
            changed = true;
        }

        if (!string.Equals(memory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(memory.Status, MemoryStatuses.Archived, StringComparison.OrdinalIgnoreCase))
        {
            memory.Status = MemoryStatuses.Active;
            changed = true;
        }

        if (changed)
        {
            memory.UpdatedAt = now;
            memory.MaintenanceNote = "Normalized during memory cleanup.";
        }

        return changed;
    }

    private static bool TryRewriteMemory(Memory memory, DateTimeOffset now)
    {
        var rewritten = RewriteContent(memory.Key, memory.Content);
        if (rewritten is null || string.Equals(rewritten, memory.Content, StringComparison.Ordinal))
            return false;

        memory.Content = rewritten;
        memory.UpdatedAt = now;
        memory.MaintenanceNote = "Rewritten into a direct durable fact.";
        return true;
    }

    private static string? RewriteContent(string key, string content)
    {
        var normalized = NormalizeForMaintenance($"{key} {content}");

        if (!normalized.Contains(" when asked ", StringComparison.Ordinal)
            && !normalized.Contains(" selected ", StringComparison.Ordinal))
        {
            return null;
        }

        if (normalized.Contains(" work motivation ", StringComparison.Ordinal)
            || normalized.Contains(" enjoy most about work ", StringComparison.Ordinal))
        {
            var selected = ExtractSelectedValue(content);
            return selected is null ? null : $"Adir enjoys {LowercaseFirst(selected)} at work.";
        }

        if (normalized.Contains(" preferred lumi help ", StringComparison.Ordinal)
            || normalized.Contains(" help with most ", StringComparison.Ordinal))
        {
            var selected = ExtractSelectedValue(content);
            return selected is null ? null : $"Adir wants Lumi to help most with {LowercaseFirst(selected)}.";
        }

        if (normalized.Contains(" outside work ", StringComparison.Ordinal)
            || normalized.Contains(" interests ", StringComparison.Ordinal))
        {
            var selected = ExtractSelectedValue(content);
            return selected is null ? null : $"Adir enjoys {LowercaseFirst(selected)} outside work.";
        }

        return null;
    }

    private static int MergeDuplicates(List<Memory> memories, DateTimeOffset now)
    {
        var merged = 0;
        var duplicateGroups = memories
            .Where(static m => string.Equals(m.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase))
            .GroupBy(BuildMergeKey, StringComparer.Ordinal)
            .Where(static g => g.Key.Length > 0 && g.Count() > 1)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            var primary = group
                .OrderByDescending(static m => m.UpdatedAt)
                .ThenByDescending(static m => m.CreatedAt)
                .First();

            foreach (var duplicate in group)
            {
                if (ReferenceEquals(duplicate, primary))
                    continue;

                memories.Remove(duplicate);
                merged++;
            }

            primary.LastReviewedAt = now;
            primary.MaintenanceNote = merged > 0 ? "Merged related duplicate memories during cleanup." : primary.MaintenanceNote;
            primary.UpdatedAt = now;
        }

        return merged;
    }

    private static string BuildMergeKey(Memory memory)
    {
        var scope = MemoryAgentService.NormalizeScope(memory.Scope, memory.ProjectId);
        var scopeKey = scope == MemoryScopes.Project
            ? $"{scope}:{memory.ProjectId:N}"
            : scope;
        var topic = MemoryAgentService.ExtractMemoryTopic(memory.Key, memory.Content, memory.Category)
                    ?? MemoryAgentService.CanonicalizeKey(memory.Key);
        return string.IsNullOrWhiteSpace(topic) ? "" : $"{scopeKey}:{topic}";
    }

    private static bool ShouldDeleteRejectedMemory(Memory memory)
    {
        var normalized = NormalizeForMaintenance($"{memory.Key} {memory.Content} {memory.Category}");
        return string.IsNullOrWhiteSpace(memory.Key)
               || string.IsNullOrWhiteSpace(memory.Content)
               || normalized.Contains(" memchk_", StringComparison.Ordinal)
               || normalized.Contains(" techchk_", StringComparison.Ordinal)
               || normalized.Contains(" ignore test marker ", StringComparison.Ordinal)
               || normalized.Contains(" test marker ", StringComparison.Ordinal)
               || normalized.Contains(" memory_sync_done ", StringComparison.Ordinal);
    }

    private static string? ExtractSelectedValue(string content)
    {
        var match = Regex.Match(
            content,
            @"selected\s+['""]?(?<value>[^'"".]+)['""]?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        return MemoryAgentService.NormalizeOrNull(match.Groups["value"].Value.Trim(' ', '.', '"', '\'')) is { } value
            ? value
            : null;
    }

    private static string LowercaseFirst(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length == 1
            ? value.ToLowerInvariant()
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string NormalizeForMaintenance(string text)
    {
        var lower = text.ToLowerInvariant()
            .Replace('’', '\'')
            .Replace('`', '\'');
        lower = Regex.Replace(lower, @"[^\p{L}\p{Nd}#+/\\.'_-]+", " ");
        return WhitespaceRegex.Replace($" {lower} ", " ");
    }
}

public sealed class MemoryMaintenanceResult
{
    public int Reviewed { get; set; }
    public int Active { get; set; }
    public int Rewritten { get; set; }
    public int Merged { get; set; }
    public int Archived { get; set; }
    public int Deleted { get; set; }

    public string ToDisplayText()
    {
        if (Reviewed == 0)
            return "No memories to clean up.";

        return $"Cleaned up {Reviewed} memories: {Merged} merged, {Archived} archived, {Deleted} deleted, {Rewritten} rewritten. {Active} active memories remain.";
    }
}
