using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Services;

/// <summary>
/// Produces the "frequently-typed requests" block offered to the follow-up suggestion model.
///
/// Design intent: this class does <b>cheap, deterministic data preparation only</b>. It deduplicates
/// the user's recurring prompts, strips obvious noise (system/background-job triggers, trivial one-off
/// replies, near-duplicate phrasings), requires genuine recurrence, and keeps the few most frequent.
///
/// It deliberately does NOT decide which requests are <i>relevant</i> to the current conversation.
/// Judging relevance from lexical keyword overlap was tried and failed badly on real data: generic
/// words ("tool", "run", "check", "use") match almost everything, so frequent prompts such as
/// "commit and push to main" leaked into unrelated chats. Relevance — and filtering out test/debug
/// artifacts — is a semantic judgement, so it is left to the suggestion model, which understands
/// meaning. This keeps the deterministic surface small, language-agnostic, and easy to reason about.
/// </summary>
public static class SuggestionHistoryRanker
{
    private const int DefaultMinOccurrences = 2;   // only surface genuinely recurring requests
    private const int MinMeaningfulLength = 15;    // drops "continue", "ping", "ok", "hey", "test", ...
    private const int MaxCandidateLength = 160;    // suggestion chips are short; long pastes aren't candidates
    private const int DisplayMaxLength = 160;

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingTokenPattern = new(@"^([\p{L}']+)[\s,]+", RegexOptions.Compiled);

    /// <summary>
    /// Leading conversational filler stripped before grouping, so "ok commit and push to main",
    /// "nice commit and push to main" and "commit and push to main" collapse into one request.
    /// </summary>
    private static readonly HashSet<string> LeadingFillers = new(StringComparer.Ordinal)
    {
        "ok", "okay", "nice", "now", "please", "also", "so", "then", "hey", "thanks", "thank",
        "great", "cool", "perfect", "alright", "yes", "yeah", "and", "well", "actually", "lets",
        "let's", "hi", "hello", "good", "sure",
    };

    /// <summary>Normalised prompts that recur but carry no reusable intent worth re-suggesting.</summary>
    private static readonly HashSet<string> TrivialPrompts = new(StringComparer.Ordinal)
    {
        "continue", "did you check today", "did you finish", "another one", "go on", "try again",
        "any update", "go ahead", "do it", "status update", "keep going", "carry on", "next one",
        "same again", "one more", "and then", "what about you", "do it now", "go for it",
    };

    /// <summary>A distinct recurring request the user has typed across chats.</summary>
    public sealed record FrequentRequest(string Content, int Count, DateTimeOffset LastUsedAt);

    /// <summary>
    /// Ranks the user's recurring requests by frequency (then recency) after cleaning out noise and
    /// near-duplicates. Returns at most <paramref name="maxItems"/> distinct requests, most frequent
    /// first. Relevance to the current conversation is intentionally NOT considered — that, and the
    /// rejection of test/debug artifacts, is the suggestion model's job.
    /// </summary>
    public static IReadOnlyList<FrequentRequest> RankFrequentRequests(
        IEnumerable<UserPromptHistoryItem> historyItems,
        int maxItems,
        int minOccurrences = DefaultMinOccurrences)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        if (maxItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Item limit must be greater than zero.");

        return historyItems
            .Select(item => new { item.Content, Key = NormalizeForGrouping(item.Content), item.Timestamp })
            .Where(x => x.Key is not null)
            .GroupBy(static x => x.Key!, StringComparer.Ordinal)
            .Select(group =>
            {
                var latest = group.OrderByDescending(static i => i.Timestamp).First();
                return new FrequentRequest(TrimForDisplay(latest.Content), group.Count(), latest.Timestamp);
            })
            .Where(r => r.Count >= minOccurrences)
            .OrderByDescending(static r => r.Count)
            .ThenByDescending(static r => r.LastUsedAt)
            .Take(maxItems)
            .ToList();
    }

    /// <summary>
    /// Renders ranked requests into the prompt block, or <c>null</c> when there are none. Counts are
    /// intentionally omitted so the model weighs each item on conversational fit, not popularity.
    /// </summary>
    public static string? RenderFrequentRequests(IReadOnlyList<FrequentRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var request in requests)
            sb.Append("- ").AppendLine(request.Content);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Convenience: rank and render in a single call. Returns <c>null</c> when nothing qualifies.</summary>
    public static string? BuildFrequentRequestsBlock(
        IEnumerable<UserPromptHistoryItem> historyItems,
        int maxItems,
        int minOccurrences = DefaultMinOccurrences)
        => RenderFrequentRequests(RankFrequentRequests(historyItems, maxItems, minOccurrences));

    /// <summary>
    /// Normalises a prompt for grouping/dedup, or returns <c>null</c> if it is noise that must never be
    /// surfaced (system triggers, trivial replies, or text that is too short or too long to be a chip).
    /// </summary>
    private static string? NormalizeForGrouping(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var collapsed = WhitespacePattern.Replace(content.Trim(), " ");

        // Background-job triggers are system-injected user-role messages, never real user requests.
        if (collapsed.StartsWith("Background job triggered", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = StripLeadingFillers(
            collapsed.ToLowerInvariant().Trim(' ', '.', '!', '?', ':', ';', '"', '\''));

        if (key.Length < MinMeaningfulLength || key.Length > MaxCandidateLength)
            return null;
        if (TrivialPrompts.Contains(key))
            return null;

        return key;
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
                return current; // never strip the prompt away entirely
            current = stripped;
        }
    }

    private static string TrimForDisplay(string content)
    {
        var single = WhitespacePattern.Replace(content.Trim(), " ");
        return single.Length <= DisplayMaxLength
            ? single
            : single[..(DisplayMaxLength - 1)].TrimEnd() + "…";
    }
}
