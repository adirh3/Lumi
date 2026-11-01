using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumi.Services;

namespace Lumi.Tests;

/// <summary>
/// A frozen copy of the follow-up suggestion pipeline as it shipped before the history-based evaluation
/// (prompt, question, frequent-request list and parsing), kept so <see cref="SuggestionEvalHarness"/> can
/// always compare new engines with it on the same decision points. Do not update it to follow production.
/// </summary>
internal static class LegacySuggestionPrompt
{
    /// <summary>The history it read: the newest 1,000 user-role prompts, including those jobs and managers sent.</summary>
    public const int HistoryScanLimit = 1000;

    public const string SystemPrompt =
        "You generate follow-up suggestions for a chat assistant. Propose exactly 3 short messages the user would naturally send next, grounded in the CURRENT conversation. " +
        "QUALITY RULES (most important): " +
        "1. Ground every suggestion in the specifics of THIS conversation — reference the actual topics, names, entities, files, or details discussed. Avoid vague filler like \"What should I do next?\", \"Run the app\", \"Show a summary\", \"Check for errors\", or \"Tell me more\". " +
        "2. Advance the conversation — never re-ask something the assistant already answered; build on it instead. " +
        "3. Follow the user's actual intent and task, not just the broad theme (e.g. in a lesson about learning kanji, suggest a sharper way to study kanji — not an unrelated ramen recipe that merely shares the 'Japanese' theme). " +
        "4. Make the 3 meaningfully different from each other (e.g. go deeper, take the next concrete action, explore a related angle) — not three rephrasings of one idea. " +
        "5. Each must be concise (under 60 characters), specific, and something the user would actually click. " +
        "FREQUENT-REQUESTS LIST: you may also receive a list of the user's frequently-typed requests. Treat it as low-priority reference, NOT a menu: include one only if it is a genuinely natural next step for THIS conversation. Usually none fit — then ignore the list entirely. Never suggest something merely because it is frequent, and ignore any list item that looks like a test, debug, setup, or system command, or is unrelated to the current topic. " +
        "Output ONLY a JSON array of exactly 3 strings, nothing else. Example: [\"Compare the LG G4 and Sony A95L\", \"Which handles glare better?\", \"Show cheaper alternatives under $1500\"]";

    public static string BuildQuestion(string assistantMessage, string? userMessage, string? userHistorySummary)
    {
        var contextBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            contextBuilder.AppendLine("Latest user message:");
            contextBuilder.AppendLine(Truncate(userMessage.Trim(), 800));
            contextBuilder.AppendLine();
        }

        contextBuilder.AppendLine("Latest assistant message:");
        contextBuilder.AppendLine(Truncate(assistantMessage.Trim(), 1400));

        if (!string.IsNullOrWhiteSpace(userHistorySummary))
        {
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("The user's frequently-typed requests from other chats (reference only — include one ONLY if it is a natural next step for THIS conversation; otherwise ignore this list entirely):");
            contextBuilder.AppendLine(Truncate(userHistorySummary.Trim(), 1400));
        }

        var context = Truncate(contextBuilder.ToString().Trim(), 3600);
        return $"{SystemPrompt}\n\n{context}";
    }

    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        raw = raw.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline > 0)
                raw = raw[(firstNewline + 1)..];
            if (raw.EndsWith("```", StringComparison.Ordinal))
                raw = raw[..^3];
            raw = raw.Trim();
        }

        var suggestions = TryDeserialize(raw);
        if (suggestions is null)
        {
            var arrayStart = raw.IndexOf('[');
            var arrayEnd = raw.LastIndexOf(']');
            if (arrayStart >= 0 && arrayEnd > arrayStart)
                suggestions = TryDeserialize(raw[arrayStart..(arrayEnd + 1)]);
        }

        return suggestions?
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList() ?? [];
    }

    private static List<string>? TryDeserialize(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        var end = maxLength;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end];
    }

    // ── The global frequent-request list ────────────────────────────────────

    private const int MaxItems = 8;
    private const int MinOccurrences = 2;
    private const int MinMeaningfulLength = 15;
    private const int MaxCandidateLength = 160;

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingTokenPattern = new(@"^([\p{L}']+)[\s,]+", RegexOptions.Compiled);

    private static readonly HashSet<string> LeadingFillers = new(StringComparer.Ordinal)
    {
        "ok", "okay", "nice", "now", "please", "also", "so", "then", "hey", "thanks", "thank",
        "great", "cool", "perfect", "alright", "yes", "yeah", "and", "well", "actually", "lets",
        "let's", "hi", "hello", "good", "sure",
    };

    private static readonly HashSet<string> TrivialPrompts = new(StringComparer.Ordinal)
    {
        "continue", "did you check today", "did you finish", "another one", "go on", "try again",
        "any update", "go ahead", "do it", "status update", "keep going", "carry on", "next one",
        "same again", "one more", "and then", "what about you", "do it now", "go for it",
    };

    public static string? BuildFrequentRequestsBlock(IEnumerable<UserPromptHistoryItem> historyItems)
    {
        var ranked = historyItems
            .Select(item => new { item.Content, Key = NormalizeForGrouping(item.Content), item.Timestamp })
            .Where(static x => x.Key is not null)
            .GroupBy(static x => x.Key!, StringComparer.Ordinal)
            .Select(static group =>
            {
                var latest = group.OrderByDescending(static i => i.Timestamp).First();
                return (Content: TrimForDisplay(latest.Content), Count: group.Count(), LastUsedAt: latest.Timestamp);
            })
            .Where(static r => r.Count >= MinOccurrences)
            .OrderByDescending(static r => r.Count)
            .ThenByDescending(static r => r.LastUsedAt)
            .Take(MaxItems)
            .ToList();
        if (ranked.Count == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var request in ranked)
            builder.Append("- ").AppendLine(request.Content);
        return builder.ToString().TrimEnd();
    }

    private static string? NormalizeForGrouping(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var collapsed = WhitespacePattern.Replace(content.Trim(), " ");
        if (collapsed.StartsWith("Background job triggered", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = StripLeadingFillers(collapsed.ToLowerInvariant().Trim(' ', '.', '!', '?', ':', ';', '"', '\''));
        if (key.Length < MinMeaningfulLength || key.Length > MaxCandidateLength)
            return null;
        return TrivialPrompts.Contains(key) ? null : key;
    }

    private static string StripLeadingFillers(string text)
    {
        var current = text;
        while (true)
        {
            var match = LeadingTokenPattern.Match(current);
            if (!match.Success || !LeadingFillers.Contains(match.Groups[1].Value))
                return current;

            var stripped = current[match.Length..].Trim();
            if (stripped.Length == 0)
                return current;
            current = stripped;
        }
    }

    private static string TrimForDisplay(string content)
    {
        var single = WhitespacePattern.Replace(content.Trim(), " ");
        return single.Length <= MaxCandidateLength ? single : single[..(MaxCandidateLength - 1)].TrimEnd() + "…";
    }
}
