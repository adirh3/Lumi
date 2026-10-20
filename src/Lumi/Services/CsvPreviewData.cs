using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Lumi.Localization;
using Microsoft.VisualBasic.FileIO;

namespace Lumi.Services;

internal sealed record CsvPreviewData(char Delimiter, IReadOnlyList<string[]> Records, int ColumnCount)
{
    internal static CsvPreviewData Parse(string text, bool isTruncated, char? delimiter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lineOffset = 0;
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        if (firstLineEnd == 5 && text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)
            && text[4] is ',' or ';' or '\t' or '|')
        {
            delimiter = text[4];
            var start = firstLineEnd + 1;
            if (text[firstLineEnd] == '\r' && start < text.Length && text[start] == '\n')
                start++;
            text = text[start..];
            lineOffset = 1;
        }

        var separator = delimiter ?? DetectDelimiter(text);
        if (isTruncated)
            text = text[..FindLastCompleteRecord(text, separator)];

        using var parser = new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(separator.ToString());
        var records = new List<string[]>();
        var columnCount = 0;
        try
        {
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parser.ReadFields() is not { } fields)
                    break;
                records.Add(fields);
                columnCount = Math.Max(columnCount, fields.Length);
            }
        }
        catch (MalformedLineException ex)
        {
            throw new FormatException(Loc.Get("Csv_InvalidFormat", ex.LineNumber + lineOffset), ex);
        }

        return new(separator, records, columnCount);
    }

    private static char DetectDelimiter(string text)
    {
        ReadOnlySpan<char> candidates = [',', ';', '\t', '|'];
        Span<int> counts = stackalloc int[candidates.Length];
        counts.Clear();
        var quoted = false;
        var hasContent = false;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                    i++;
                else
                    quoted = !quoted;
            }
            else if (!quoted)
            {
                if (character is '\r' or '\n' && hasContent)
                    break;
                var candidate = candidates.IndexOf(character);
                if (candidate >= 0)
                {
                    counts[candidate]++;
                    hasContent = true;
                }
            }
            hasContent |= !char.IsWhiteSpace(character);
        }

        var best = 0;
        for (var i = 1; i < counts.Length; i++)
            if (counts[i] > counts[best])
                best = i;
        return candidates[best];
    }

    private static int FindLastCompleteRecord(string text, char delimiter)
    {
        // A preview can end inside a quoted multiline field or an otherwise valid-looking cell.
        var end = 0;
        var quoted = false;
        var fieldStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                        i++;
                    else
                        quoted = false;
                }
            }
            else if (character == '"' && fieldStart)
            {
                quoted = true;
                fieldStart = false;
            }
            else if (character == delimiter)
                fieldStart = true;
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                end = i + 1;
                fieldStart = true;
            }
            else if (!char.IsWhiteSpace(character))
                fieldStart = false;
        }
        return end;
    }
}
