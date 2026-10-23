using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// What chat search finds in a message, and how it matches. Stored messages are looked up in the
/// chat database's text index (<see cref="Storage.ChatDatabase.SearchTextSql"/>, which says in SQL what
/// <see cref="Of"/> says here); messages still only in memory are matched here the same way: a run of
/// characters anywhere, inside words too, ignoring case and accents.
/// </summary>
internal static class ChatSearchText
{
    private const string JobAuthorPrefix = "Lumi Job - ";

    /// <summary>
    /// A conversation message's text, a job wake's job name and a submitted form's receipt; an input
    /// request's title and description. Null for messages search does not look at.
    /// </summary>
    public static string? Of(ChatMessage message)
    {
        switch (message.Role)
        {
            case "user" or "assistant" or "error":
            {
                var receipt = message.InteractionReceipt;
                return Join(
                    message.Content,
                    message.Author?.StartsWith(JobAuthorPrefix, StringComparison.OrdinalIgnoreCase) == true ? message.Author : null,
                    receipt?.Title,
                    receipt?.Summary,
                    receipt?.Detail);
            }
            case "tool" when message.ToolName == "ask_input" && message.InputDefinition is { } definition:
                try
                {
                    using var document = JsonDocument.Parse(definition);
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        ? Join(StringProperty(document.RootElement, "title"), StringProperty(document.RootElement, "description"))
                        : null;
                }
                catch (JsonException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether the text index can look <paramref name="text"/> up: it is made of runs of three
    /// characters, so shorter text matches nothing.
    /// </summary>
    public static bool CanLookUp(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var characters = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            if (++characters >= 3)
                return true;
        }

        return false;
    }

    /// <summary>The text index query matching <paramref name="text"/> as it is: one quoted phrase.</summary>
    public static string Phrase(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The words of a query as typed, which is how the text index is searched: it holds text as it
    /// was written and folds only case and Latin accents, so a word keeps its marks (normalized the
    /// way titles are compared, Korean, Greek, Cyrillic or Japanese words would match nothing).
    /// </summary>
    public static string[] Words(string query)
    {
        try
        {
            query = query.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not well-formed text: searched as it is.
        }

        var words = new List<string>();
        var word = new StringBuilder();
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in query.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                word.Append(buffer[..rune.EncodeToUtf16(buffer)]);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>True when <paramref name="text"/> contains <paramref name="needle"/>, ignoring case and accents.</summary>
    public static bool Contains(string text, string needle)
        => IndexOf(text, needle) >= 0;

    /// <summary>Where <paramref name="needle"/> first occurs in <paramref name="text"/>, ignoring case and accents, or -1.</summary>
    public static int IndexOf(string text, string needle)
        => CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Join(params string?[] parts)
    {
        StringBuilder? builder = null;
        foreach (var part in parts)
        {
            if (part is null)
                continue;
            if (builder is null)
                builder = new StringBuilder(part);
            else
                builder.Append('\n').Append(part);
        }

        return builder?.ToString();
    }
}
