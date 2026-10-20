using System;
using System.Collections.Generic;
using System.Linq;

namespace Lumi.Views.Settings;

/// <summary>
/// Decides what Settings search finds. Every word of the query has to begin a word of a setting's name
/// or description; names, which are short, also forgive one typo and match a query typed without spaces.
/// Sentences only match by the start of their words: matching letters loosely inside them would find
/// nearly any query.
/// </summary>
internal static class SettingsSearch
{
    private const int MinimumTypoLength = 5;

    public static bool Matches(string? query, IEnumerable<string?> names, IEnumerable<string?>? descriptions = null)
    {
        var terms = Words(query);
        if (terms.Count == 0)
            return false;

        var nameWords = names.Select(Words).Where(static words => words.Count > 0).ToList();
        var compactQuery = string.Concat(terms);
        if (nameWords.Any(words => StartsAtAWord(words, compactQuery)))
            return true;

        var descriptionWords = (descriptions ?? []).SelectMany(Words).ToList();
        return terms.All(term =>
            nameWords.Any(words => words.Any(word => word.StartsWith(term, StringComparison.Ordinal) || IsOneTypoAway(term, word)))
            || descriptionWords.Any(word => word.StartsWith(term, StringComparison.Ordinal)));
    }

    public static bool Matches(string? query, string? name, string? description = null)
        => Matches(query, [name], [description]);

    // "fontsize" finds "Font Size", but "port" does not find "Import".
    private static bool StartsAtAWord(List<string> words, string compactQuery)
    {
        for (var start = 0; start < words.Count; start++)
        {
            if (string.Concat(words.Skip(start)).StartsWith(compactQuery, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Short words sit one letter from too many others ("mode" and "model", "chat" and "what"),
    // so only terms and word starts of five letters or more forgive a typo.
    private static bool IsOneTypoAway(string term, string word)
    {
        if (term.Length < MinimumTypoLength)
            return false;

        for (var length = Math.Max(term.Length - 1, MinimumTypoLength); length <= term.Length + 1 && length <= word.Length; length++)
        {
            if (IsWithinOneEdit(term, word.AsSpan(0, length)))
                return true;
        }
        return false;
    }

    // One substitution, insertion, deletion or swap of neighbouring letters.
    private static bool IsWithinOneEdit(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;

        var index = 0;
        while (index < a.Length && index < b.Length && a[index] == b[index])
            index++;
        if (index >= a.Length || index >= b.Length)
            return true;

        if (a.Length == b.Length)
        {
            return a[(index + 1)..].SequenceEqual(b[(index + 1)..])
                   || index + 1 < a.Length
                   && a[index] == b[index + 1]
                   && a[index + 1] == b[index]
                   && a[(index + 2)..].SequenceEqual(b[(index + 2)..]);
        }

        return a.Length > b.Length
            ? a[(index + 1)..].SequenceEqual(b[index..])
            : a[index..].SequenceEqual(b[(index + 1)..]);
    }

    internal static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text))
            return words;

        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isWordCharacter = index < text.Length && char.IsLetterOrDigit(text[index]);
            if (isWordCharacter && start < 0)
            {
                start = index;
            }
            else if (!isWordCharacter && start >= 0)
            {
                words.Add(text[start..index].ToLowerInvariant());
                start = -1;
            }
        }
        return words;
    }
}
