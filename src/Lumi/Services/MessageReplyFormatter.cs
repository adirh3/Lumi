using System;
using System.Text;
using System.Text.RegularExpressions;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Shapes reply quotes: normalizes the captured text, derives the one-line previews shown in the
/// composer and the transcript, and frames a reply for the model so it knows exactly which part of
/// its earlier answer the user is asking about.
/// </summary>
public static partial class MessageReplyFormatter
{
    /// <summary>Longest selected excerpt kept; a selection is precisely what the user is asking about.</summary>
    public const int MaxSelectionQuoteLength = 4000;

    /// <summary>
    /// Longest opening kept for a whole-message reply. The model already holds the full message, so
    /// the quote only has to identify which answer the user means.
    /// </summary>
    public const int MaxMessageQuoteLength = 600;

    private const int PreviewLength = 280;
    private const int TranscriptPreviewLength = 160;
    private const int WordBoundaryLookback = 40;

    /// <summary>Builds a reply to <paramref name="messageId"/>, or null when there is nothing to quote.</summary>
    public static MessageReply? Create(Guid messageId, string? text, bool isSelection, string? author)
    {
        var quote = NormalizeQuote(text, isSelection ? MaxSelectionQuoteLength : MaxMessageQuoteLength);
        if (quote.Length == 0)
            return null;

        return new MessageReply
        {
            MessageId = messageId,
            Quote = quote,
            IsSelection = isSelection,
            Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim()
        };
    }

    /// <summary>
    /// Cleans captured text: drops rendering artifacts, then trims to <paramref name="maxLength"/> on
    /// a word boundary. Everything else is kept verbatim, so the quote can still be found again in
    /// the rendered message (see <see cref="IsRenderingArtifact"/>).
    /// </summary>
    public static string NormalizeQuote(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!IsRenderingArtifact(ch))
                builder.Append(ch);
        }

        return Truncate(builder.ToString().Trim(), maxLength);
    }

    /// <summary>
    /// Characters that exist only in rendered text, not in what the user read: U+2005 pads inline
    /// code, U+FFFC stands in for embedded controls, and CR comes from rendered line breaks.
    /// </summary>
    public static bool IsRenderingArtifact(char ch) => ch is '\u2005' or '\uFFFC' or '\r';

    /// <summary>One-line preview of a reply's quote for the composer and the transcript.</summary>
    public static string BuildPreview(MessageReply? reply)
        => reply is null ? "" : BuildPreview(reply.Quote, reply.IsSelection, PreviewLength);

    /// <summary>
    /// Frames <paramref name="prompt"/> as a reply for the model: a short lead-in saying what is being
    /// replied to, the quote as a Markdown block quote, then the user's own words.
    /// </summary>
    public static string ComposePrompt(string prompt, MessageReply? reply)
    {
        if (reply is null || string.IsNullOrWhiteSpace(reply.Quote))
            return prompt;

        var builder = new StringBuilder(reply.Quote.Length + prompt.Length + 96);
        builder.Append(reply.IsSelection
                ? "[Replying to this part of your earlier response]"
                : "[Replying to your earlier response]")
            .Append('\n');

        foreach (var line in reply.Quote.Split('\n'))
            builder.Append(line.Length == 0 ? ">" : "> ").Append(line).Append('\n');

        builder.Append('\n').Append(prompt);
        return builder.ToString();
    }

    /// <summary>
    /// Inline marker for transcripts flattened to text (session-recovery replays, read_chat), e.g.
    /// <c>(replying to: "…") </c>; empty when the message is not a reply.
    /// </summary>
    public static string DescribeForTranscript(MessageReply? reply)
        => reply is null || string.IsNullOrWhiteSpace(reply.Quote)
            ? ""
            : $"(replying to: \"{BuildPreview(reply.Quote, reply.IsSelection, TranscriptPreviewLength)}\") ";

    private static string BuildPreview(string quote, bool isSelection, int maxLength)
    {
        // A selection is already rendered text; a whole-message quote is raw markdown.
        var text = isSelection ? quote : StripMarkdown(quote);
        return Truncate(Whitespace().Replace(text, " ").Trim(), maxLength);
    }

    private static string StripMarkdown(string markdown)
    {
        var text = CodeFence().Replace(markdown, "");
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = LinePrefix().Replace(text, "");
        return EmphasisMarker().Replace(text, "");
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        var limit = Math.Max(1, maxLength - 1);
        var lookback = Math.Min(WordBoundaryLookback, limit);
        var boundary = text.LastIndexOfAny([' ', '\n', '\t'], limit - 1, lookback);
        var cut = boundary > 0 ? boundary : limit;
        return text[..cut].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[ \t]*(```|~~~).*$", RegexOptions.Multiline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^[ \t]*(#{1,6}[ \t]+|>[ \t]?|[-*+][ \t]+(\[[ xX]\][ \t]+)?|\d+[.)][ \t]+)", RegexOptions.Multiline)]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"\*\*|__|~~|`")]
    private static partial Regex EmphasisMarker();
}
