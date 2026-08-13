using System.Text;

namespace Lumi.Remote.Protocol;

public readonly record struct RemoteMarkdownImageReference(
    int Index,
    int TargetStart,
    int TargetLength,
    string Target,
    int MarkupStart,
    int MarkupLength,
    string AltText);

public static class RemoteMarkdownImages
{
    public static IReadOnlyList<RemoteMarkdownImageReference> Find(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        var excluded = BuildCodeExclusions(markdown);
        var references = new List<RemoteMarkdownImageReference>();
        var position = 0;
        while (position + 4 < markdown.Length)
        {
            var imageStart = markdown.IndexOf("![", position, StringComparison.Ordinal);
            if (imageStart < 0)
                break;
            if (excluded[imageStart] || IsEscaped(markdown, imageStart))
            {
                position = imageStart + 2;
                continue;
            }

            var bracketClose = FindClosing(
                markdown,
                excluded,
                imageStart + 2,
                '[',
                ']');
            if (bracketClose < 0
                || bracketClose + 1 >= markdown.Length
                || markdown[bracketClose + 1] != '(')
            {
                position = imageStart + 2;
                continue;
            }

            var targetStart = bracketClose + 2;
            var parenClose = FindClosing(
                markdown,
                excluded,
                targetStart,
                '(',
                ')');
            if (parenClose < 0)
                break;

            var contentStart = targetStart;
            var contentEnd = parenClose;
            while (contentStart < contentEnd && char.IsWhiteSpace(markdown[contentStart]))
                contentStart++;
            while (contentEnd > contentStart && char.IsWhiteSpace(markdown[contentEnd - 1]))
                contentEnd--;

            var target = markdown[contentStart..contentEnd];
            if (target.Length >= 2 && target[0] == '<' && target[^1] == '>')
                target = target[1..^1].Trim();

            references.Add(new RemoteMarkdownImageReference(
                references.Count,
                contentStart,
                contentEnd - contentStart,
                target,
                imageStart,
                parenClose - imageStart + 1,
                markdown[(imageStart + 2)..bracketClose]));
            position = parenClose + 1;
        }

        return references;
    }

    public static string RewriteTargets(
        string markdown,
        IReadOnlyDictionary<int, string> replacements)
    {
        if (string.IsNullOrEmpty(markdown) || replacements.Count == 0)
            return markdown;

        var references = Find(markdown);
        var builder = new StringBuilder(markdown);
        foreach (var reference in references.Reverse())
        {
            if (!replacements.TryGetValue(reference.Index, out var replacement)
                || string.IsNullOrWhiteSpace(replacement))
            {
                continue;
            }

            builder.Remove(reference.TargetStart, reference.TargetLength);
            builder.Insert(reference.TargetStart, replacement);
        }

        return builder.ToString();
    }

    public static string ToSelectionText(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return "";

        var references = Find(markdown);
        if (references.Count == 0)
            return markdown;

        var builder = new StringBuilder(markdown);
        foreach (var reference in references.Reverse())
        {
            builder.Remove(reference.MarkupStart, reference.MarkupLength);
            if (!string.IsNullOrWhiteSpace(reference.AltText))
                builder.Insert(reference.MarkupStart, reference.AltText.Trim());
        }

        return builder.ToString();
    }

    private static bool[] BuildCodeExclusions(string markdown)
    {
        var excluded = new bool[markdown.Length];
        MarkFencedCode(markdown, excluded);
        MarkInlineCode(markdown, excluded);
        return excluded;
    }

    private static void MarkFencedCode(string markdown, bool[] excluded)
    {
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var lineStart = 0;
        while (lineStart < markdown.Length)
        {
            var newline = markdown.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? markdown.Length : newline + 1;
            var contentEnd = newline < 0 ? markdown.Length : newline;
            if (contentEnd > lineStart && markdown[contentEnd - 1] == '\r')
                contentEnd--;

            var markerStart = lineStart;
            while (markerStart < contentEnd
                   && markdown[markerStart] is ' ' or '\t')
            {
                markerStart++;
            }

            var markerCharacter = markerStart < contentEnd
                ? markdown[markerStart]
                : '\0';
            var markerLength = 0;
            if (markerCharacter is '`' or '~')
            {
                while (markerStart + markerLength < contentEnd
                       && markdown[markerStart + markerLength] == markerCharacter)
                {
                    markerLength++;
                }
            }

            if (!inFence && markerLength >= 3)
            {
                inFence = true;
                fenceCharacter = markerCharacter;
                fenceLength = markerLength;
                Mark(excluded, lineStart, lineEnd);
            }
            else if (inFence)
            {
                Mark(excluded, lineStart, lineEnd);
                if (markerCharacter == fenceCharacter
                    && markerLength >= fenceLength
                    && IsOnlyWhitespace(
                        markdown,
                        markerStart + markerLength,
                        contentEnd))
                {
                    inFence = false;
                }
            }

            lineStart = lineEnd;
        }
    }

    private static void MarkInlineCode(string markdown, bool[] excluded)
    {
        for (var position = 0; position < markdown.Length;)
        {
            if (excluded[position]
                || markdown[position] != '`'
                || IsEscaped(markdown, position))
            {
                position++;
                continue;
            }

            var runLength = CountRun(markdown, position, '`');
            var closing = position + runLength;
            while (closing < markdown.Length)
            {
                if (excluded[closing])
                {
                    closing++;
                    continue;
                }
                if (markdown[closing] != '`')
                {
                    closing++;
                    continue;
                }

                var closingLength = CountRun(markdown, closing, '`');
                if (closingLength == runLength)
                {
                    Mark(excluded, position, closing + closingLength);
                    position = closing + closingLength;
                    break;
                }

                closing += closingLength;
            }

            if (closing >= markdown.Length)
                position += runLength;
        }
    }

    private static int FindClosing(
        string text,
        IReadOnlyList<bool> excluded,
        int start,
        char open,
        char close)
    {
        var depth = 0;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            if (excluded[index])
                return -1;

            var value = text[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (value == '\\')
            {
                escaped = true;
                continue;
            }
            if (value == open)
            {
                depth++;
                continue;
            }
            if (value != close)
                continue;
            if (depth == 0)
                return index;
            depth--;
        }

        return -1;
    }

    private static int CountRun(string text, int start, char value)
    {
        var length = 0;
        while (start + length < text.Length && text[start + length] == value)
            length++;
        return length;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var position = index - 1;
             position >= 0 && text[position] == '\\';
             position--)
        {
            slashCount++;
        }
        return slashCount % 2 != 0;
    }

    private static bool IsOnlyWhitespace(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
                return false;
        }
        return true;
    }

    private static void Mark(bool[] excluded, int start, int end)
    {
        for (var index = start; index < end; index++)
            excluded[index] = true;
    }
}
