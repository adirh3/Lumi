using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lumi.Services;

/// <summary>
/// Finds what the user usually sends at moments like the current one, from the prompts they sent before.
///
/// People repeat themselves where they work: in a coding project a few requests ("commit and push to main",
/// "run code review") make up a large share of replies, while the same requests almost never follow a chat
/// elsewhere. So a chat in a project learns only from that project's prompts: the chat's own count more, recent
/// ones count more, and only requests the user has sent more than once qualify. Chats outside projects get none —
/// replaying real history (<c>SuggestionEvalHarness</c>) found the requests repeated there were mostly tests and
/// one-offs, and offering them made suggestions worse. Noise is dropped before ranking: background-job triggers,
/// trivial replies ("continue", "do it"), and text too short or too long to be a chip. Requests that differ only
/// in leading politeness ("ok", "nice work,") are one request, shown in the user's most recent wording without it.
/// </summary>
public static class SuggestionHistoryRanker
{
    private const int DefaultMinOccurrences = 2;   // only surface genuinely recurring requests
    private const int MinMeaningfulLength = 15;    // drops "continue", "ping", "ok", "hey", "test", ...
    private const int MaxCandidateLength = 160;    // suggestion chips are short; long pastes aren't candidates

    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(21);
    private const double SameChatWeight = 4;       // a prompt from this chat, against one from elsewhere in the project

    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly char[] TrimCharacters = [' ', '.', '!', '?', ':', ';', ',', '"', '\'', '-'];

    /// <summary>
    /// Leading politeness and filler, so "ok commit and push to main", "nice work, commit and push to main" and
    /// "commit and push to main" are one request. A filler must be followed by a space or punctuation (not a hyphen),
    /// so "so-called" and "nowhere" stay whole, and only praise takes "work"/"job" ("nice work,", not "let's work on").
    /// </summary>
    private static readonly Regex LeadingFillerPattern = new(
        @"^(?:(?:(?:nice|great|good|amazing|awesome|excellent)(?:\s+(?:work|job))?|ok(?:ay)?|perfect|cool|thanks|thank\s+you|looks\s+(?:good|great|nice|perfect)|lgtm|well\s+done|alright|sure|yes|yeah|yep|now|so|then|also|and|well|actually|please|hey|hi|hello|let['’]?s)(?:[\s,.!:;]+|$))+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Normalised prompts that recur but carry no reusable intent worth re-suggesting.</summary>
    private static readonly HashSet<string> TrivialPrompts = new(StringComparer.Ordinal)
    {
        "continue", "did you check today", "did you finish", "another one", "go on", "try again",
        "any update", "go ahead", "do it", "status update", "keep going", "carry on", "next one",
        "same again", "one more", "and then", "what about you", "do it now", "go for it",
    };

    /// <summary>A request the user habitually sends at moments like the current one.</summary>
    /// <param name="Text">The request in the user's most recent wording, without leading filler.</param>
    /// <param name="Weight">Its occurrences, each weighted by where and how recently it was sent.</param>
    /// <param name="Count">How many times it was sent across the scanned history.</param>
    public sealed record UsualReply(string Text, double Weight, int Count);

    /// <summary>Where suggestions are being made: the chat, its project, each chat's project, and the time.</summary>
    public sealed record Scope(Guid ChatId, Guid? ProjectId, Func<Guid, Guid?> ProjectOf, DateTimeOffset Now);

    /// <summary>
    /// The user's recurring requests in the scope's project that fit it best, most likely first, or none for a chat
    /// outside projects. Only requests sent at least <paramref name="minOccurrences"/> times qualify.
    /// </summary>
    public static IReadOnlyList<UsualReply> RankUsualReplies(
        IEnumerable<UserPromptHistoryItem> historyItems,
        Scope scope,
        int maxItems,
        int minOccurrences = DefaultMinOccurrences)
    {
        ArgumentNullException.ThrowIfNull(historyItems);
        ArgumentNullException.ThrowIfNull(scope);
        if (maxItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Item limit must be greater than zero.");
        if (scope.ProjectId is not { } project)
            return [];

        var groups = new Dictionary<string, (double Weight, int Count, UserPromptHistoryItem Latest)>(StringComparer.Ordinal);
        foreach (var item in historyItems)
        {
            var place = item.ChatId == scope.ChatId ? SameChatWeight : scope.ProjectOf(item.ChatId) == project ? 1 : 0;
            var key = place > 0 ? NormalizeForGrouping(item.Content) : null;
            if (key is null)
                continue;

            var age = scope.Now - item.Timestamp;
            var weight = Math.Pow(0.5, Math.Max(0, age.TotalDays) / HalfLife.TotalDays) * place;
            groups[key] = groups.TryGetValue(key, out var group)
                ? (group.Weight + weight, group.Count + 1, item.Timestamp > group.Latest.Timestamp ? item : group.Latest)
                : (weight, 1, item);
        }

        return groups.Values
            .Where(group => group.Count >= minOccurrences)
            .OrderByDescending(static group => group.Weight)
            .ThenByDescending(static group => group.Latest.Timestamp)
            .Take(maxItems)
            .Select(static group => new UsualReply(DisplayText(group.Latest.Content), group.Weight, group.Count))
            .ToList();
    }

    internal static bool IsCandidate(string content) => NormalizeForGrouping(content) is not null;

    /// <summary>
    /// A request reduced to what it asks for: lowercase, single-spaced, without leading filler or surrounding
    /// punctuation. Two messages with the same key ask for the same thing.
    /// </summary>
    internal static string NormalizeRequest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var collapsed = WhitespacePattern.Replace(content.Trim(), " ").ToLowerInvariant().Trim(TrimCharacters);
        var stripped = LeadingFillerPattern.Replace(collapsed, "").Trim(TrimCharacters);
        return stripped.Length > 0 ? stripped : collapsed;
    }

    /// <summary>
    /// The grouping key of a prompt, or <c>null</c> if it is noise that must never be surfaced (system triggers,
    /// trivial replies, or text that is too short or too long to be a chip).
    /// </summary>
    private static string? NormalizeForGrouping(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)
            || content.TrimStart().StartsWith("Background job triggered", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = NormalizeRequest(content);
        if (key.Length < MinMeaningfulLength || key.Length > MaxCandidateLength)
            return null;
        return TrivialPrompts.Contains(key) ? null : key;
    }

    /// <summary>The user's own wording, single-spaced, without leading filler, starting with a capital.</summary>
    private static string DisplayText(string content)
    {
        var collapsed = WhitespacePattern.Replace(content.Trim(), " ");
        var stripped = LeadingFillerPattern.Replace(collapsed, "").TrimEnd('.', ',', ';', ':', ' ');
        if (stripped.Length == 0)
            stripped = collapsed;
        return char.ToUpperInvariant(stripped[0]) + stripped[1..];
    }
}
