using StrataTheme.Controls;

namespace Lumi.Mobile.Services;

/// <summary>
/// A long file's text in sections, so its pane builds only the part on screen. Markdown is cut where its
/// text meets a table, code, quote or rule, and long text between two paragraphs, never inside code or a
/// list; text between lines; code at blank lines outside comments and strings that span lines, so each
/// section highlights as it does within the whole file. Shown one after another, spaced as each section's
/// <c>JoinsNext</c> asks, the sections read exactly as the file does.
/// </summary>
public static class FilePreviewSections
{
    /// <summary>
    /// A markdown section grows to this much before it can end where its text meets a table, code, quote or
    /// rule, so a report comes in about one block a frame: its text, then a table, then its code.
    /// </summary>
    public const int MarkdownCharacters = 400;

    /// <summary>Text with no such block in reach ends between two paragraphs once a section holds this much.</summary>
    public const int MarkdownTextCharacters = 1_200;

    /// <summary>Plain text costs less to lay out, so its sections hold more.</summary>
    public const int TextCharacters = 4_000;

    /// <summary>A code section ends at the first blank line, outside comments and strings, past this many lines.</summary>
    public const int CodeLines = 80;

    /// <summary>
    /// The file's sections, in order. <c>JoinsNext</c> marks a markdown section whose last paragraph the next
    /// one's first continues: markdown sets such paragraphs a blank line apart, where its blocks are
    /// <see cref="StrataMarkdown.BlockSpacing"/> apart.
    /// </summary>
    public static IReadOnlyList<(string Text, bool JoinsNext)> Split(FilePreviewKind kind, string text) => kind switch
    {
        FilePreviewKind.Markdown => SplitMarkdown(text),
        FilePreviewKind.Code => Cut(text, SplitCode(text)),
        _ => Cut(text, SplitText(text))
    };

    /// <summary>
    /// Cuts only where <see cref="StrataMarkdown"/> would part the text anyway, by its own rules: code opens and
    /// closes at any line that starts with three backticks; paragraphs, headings and list items that follow
    /// one another merge into one text, a blank line apart, while their writing direction agrees; tables,
    /// code, quotes and rules are blocks of their own. A section leaves out the blank lines after it, which
    /// the space below it stands for: plain text would otherwise draw them.
    /// </summary>
    private static IReadOnlyList<(string Text, bool JoinsNext)> SplitMarkdown(string text)
    {
        if (text.Length < MarkdownCharacters * 2)
            return [(text.TrimEnd(), false)];

        var cuts = new List<(int At, bool Joins)>();
        var sectionStart = 0;
        var inCode = false;
        var previousBlank = false;
        var previousJoins = false;
        // Whether the lines so far end in a plain paragraph that began with a letter, and that letter's direction.
        bool? paragraphIsRtl = null;
        foreach (var (start, line) in Lines(text))
        {
            var trimmed = line.AsSpan().TrimStart();
            var isBlank = trimmed.IsEmpty;
            var isFence = trimmed.StartsWith("```");
            var joins = !isBlank && !inCode && !isFence && JoinsText(trimmed);
            if (!inCode && previousBlank && StartsTopLevelBlock(line))
            {
                var length = start - sectionStart;
                // Where the text meets a block, the sections stack exactly as the blocks do.
                if (length >= MarkdownCharacters && !(joins && previousJoins))
                {
                    cuts.Add((start, false));
                    sectionStart = start;
                }
                // Long text with no block in reach parts between two paragraphs that begin with letters of one
                // direction: markdown keeps them in one text, and each section's text takes that direction.
                else if (length >= MarkdownTextCharacters && paragraphIsRtl is { } before && LeadingIsRtl(trimmed) == before)
                {
                    cuts.Add((start, true));
                    sectionStart = start;
                }
            }

            if (isFence)
                inCode = !inCode;
            if (!isBlank)
            {
                previousJoins = joins;
                // A plain paragraph begins at the margin after a blank line and goes on in plain lines.
                if (!joins || char.IsWhiteSpace(line[0]) || !IsPlainText(trimmed))
                    paragraphIsRtl = null;
                else if (previousBlank)
                    paragraphIsRtl = LeadingIsRtl(trimmed);
            }

            previousBlank = !inCode && isBlank;
        }

        return [.. Cut(text, cuts).Select(section => (section.Text.TrimEnd(), section.JoinsNext))];
    }

    private static List<int> SplitText(string text)
    {
        var cuts = new List<int>();
        if (text.Length < TextCharacters * 2)
            return cuts;

        var sectionStart = 0;
        foreach (var (start, _) in Lines(text))
        {
            if (start - sectionStart >= TextCharacters)
            {
                cuts.Add(start);
                sectionStart = start;
            }
        }

        return cuts;
    }

    private static List<int> SplitCode(string text)
    {
        var cuts = new List<int>();
        var linesInSection = 0;
        var previousBlank = false;
        var state = CodeState.None;
        foreach (var (start, line) in Lines(text))
        {
            // Past the length, a section ends at a blank line; long past it, at any line, but never inside
            // a comment or string that spans lines, which would highlight wrongly on its own.
            var due = linesInSection >= CodeLines * 2 || (linesInSection >= CodeLines && previousBlank);
            if (state == CodeState.None && due)
            {
                cuts.Add(start);
                linesInSection = 0;
            }

            state = Scan(line, state);
            previousBlank = string.IsNullOrWhiteSpace(line);
            linesInSection++;
        }

        return cuts;
    }

    private static IReadOnlyList<(string Text, bool JoinsNext)> Cut(string text, List<int> cuts) =>
        Cut(text, cuts.ConvertAll(cut => (cut, false)));

    /// <summary>The text between the cuts, each section ending before the line break the next one follows.</summary>
    private static IReadOnlyList<(string Text, bool JoinsNext)> Cut(string text, List<(int At, bool Joins)> cuts)
    {
        if (cuts.Count == 0)
            return [(text, false)];

        var sections = new List<(string Text, bool JoinsNext)>(cuts.Count + 1);
        var start = 0;
        foreach (var (at, joins) in cuts)
        {
            var end = at - 1;
            if (end > start && text[end - 1] == '\r')
                end--;
            sections.Add((text[start..end], joins));
            start = at;
        }

        sections.Add((text[start..], false));
        return sections;
    }

    private static IEnumerable<(int Start, string Line)> Lines(string text)
    {
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOf('\n', start);
            var next = end < 0 ? text.Length : end + 1;
            var length = (end < 0 ? text.Length : end) - start;
            if (length > 0 && text[start + length - 1] == '\r')
                length--;
            yield return (start, text.Substring(start, length));
            start = next;
        }
    }

    private static bool StartsTopLevelBlock(ReadOnlySpan<char> line) =>
        line.Length > 0 && !char.IsWhiteSpace(line[0]) && !IsListItem(line);

    /// <summary>A line of a paragraph: text that is no heading, list item or image, nor a block of its own.</summary>
    private static bool IsPlainText(ReadOnlySpan<char> trimmed) =>
        JoinsText(trimmed) && trimmed[0] != '#' && !trimmed.StartsWith("![") && !IsListItem(trimmed);

    /// <summary>
    /// Whether a paragraph that begins with this line reads right to left. Markdown takes a text's direction
    /// from its first strong letter, so a paragraph that begins with a letter has that letter's; one that
    /// begins otherwise (a link, code, a number) is left uncut, since its direction is decided further in.
    /// </summary>
    private static bool? LeadingIsRtl(ReadOnlySpan<char> trimmed) =>
        char.IsLetter(trimmed[0]) ? StrataTextDirectionDetector.IsStrongRtl(trimmed[0]) : null;

    /// <summary>
    /// Whether a line belongs to the text that paragraphs, headings and list items share, rather than to a
    /// table, quote or rule, which markdown lays out as blocks of their own.
    /// </summary>
    private static bool JoinsText(ReadOnlySpan<char> trimmed)
    {
        if (trimmed[0] == '>')
            return false;
        if (trimmed[0] == '|' && trimmed.Length > 1 && trimmed[1..].Contains('|'))
            return false;
        return !IsRule(trimmed);
    }

    private static bool IsRule(ReadOnlySpan<char> trimmed)
    {
        var marker = trimmed[0];
        if (marker is not ('-' or '*' or '_'))
            return false;

        var count = 0;
        foreach (var character in trimmed)
        {
            if (character == marker)
                count++;
            else if (!char.IsWhiteSpace(character))
                return false;
        }

        return count >= 3;
    }

    /// <summary>A bullet or numbered item, which may belong to a list that continues past a blank line.</summary>
    private static bool IsListItem(ReadOnlySpan<char> line)
    {
        if (line[0] is '-' or '*' or '+')
            return line.Length == 1 || line[1] is ' ' or '\t';

        var digits = 0;
        while (digits < line.Length && digits < 10 && char.IsAsciiDigit(line[digits]))
            digits++;
        return digits is > 0 and < 10 && digits < line.Length && line[digits] is '.' or ')'
               && (digits + 1 == line.Length || line[digits + 1] is ' ' or '\t');
    }

    private enum CodeState { None, BlockComment, MarkupComment, TripleDouble, TripleSingle, Backtick, Verbatim }

    /// <summary>Where a line leaves the comments and strings that can span lines in common languages.</summary>
    private static CodeState Scan(ReadOnlySpan<char> line, CodeState state)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var rest = line[i..];
            switch (state)
            {
                case CodeState.BlockComment:
                    if (rest.StartsWith("*/")) { state = CodeState.None; i++; }
                    continue;
                case CodeState.MarkupComment:
                    if (rest.StartsWith("-->")) { state = CodeState.None; i += 2; }
                    continue;
                case CodeState.TripleDouble:
                    if (rest.StartsWith("\"\"\"")) { state = CodeState.None; i += 2; }
                    continue;
                case CodeState.TripleSingle:
                    if (rest.StartsWith("'''")) { state = CodeState.None; i += 2; }
                    continue;
                case CodeState.Backtick:
                    if (line[i] == '`') state = CodeState.None;
                    continue;
                case CodeState.Verbatim:
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') i++;
                        else state = CodeState.None;
                    }
                    continue;
            }

            if (rest.StartsWith("//") || line[i] == '#')
                return state;
            if (rest.StartsWith("/*")) { state = CodeState.BlockComment; i++; }
            else if (rest.StartsWith("<!--")) { state = CodeState.MarkupComment; i += 3; }
            else if (rest.StartsWith("\"\"\"")) { state = CodeState.TripleDouble; i += 2; }
            else if (rest.StartsWith("'''")) { state = CodeState.TripleSingle; i += 2; }
            else if (line[i] == '`') state = CodeState.Backtick;
            else if (rest.StartsWith("@\"") || rest.StartsWith("$@\"") || rest.StartsWith("@$\""))
            {
                i += rest[0] == '@' ? 1 : 2;
                state = CodeState.Verbatim;
            }
            else if (line[i] is '"' or '\'')
            {
                // A string or character on this line; one left open ends with the line.
                var quote = line[i];
                for (i++; i < line.Length && line[i] != quote; i++)
                {
                    if (line[i] == '\\')
                        i++;
                }
            }
        }

        return state;
    }
}
