using System.Globalization;
using System.Text;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed record CsvPreviewColumn(string Header, double Width, bool IsNumeric);

public sealed record CsvPreviewCell(string Text, double Width, bool IsNumeric);

public sealed record CsvPreviewRow(int Number, IReadOnlyList<CsvPreviewCell> Cells, bool IsAlternate, double NumberWidth)
{
    public string NumberText => Number.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// A CSV or TSV file as a table a thumb can read: the first record names the columns, each column is
/// measured once from what it holds, and numbers line up on the right. Rows past the preview's bound
/// are left for another app.
/// </summary>
public sealed class CsvPreviewTable
{
    public const int MaxRows = 2_000;
    public const int MaxColumns = 60;

    private const double CharacterWidth = 7.6;
    private const double CellPadding = 28;
    private const double MinColumnWidth = 64;
    private const double MaxColumnWidth = 280;
    private const int MeasuredRows = 200;

    private CsvPreviewTable(
        IReadOnlyList<CsvPreviewColumn> columns,
        IReadOnlyList<CsvPreviewRow> rows,
        double rowNumberWidth,
        bool isLimited)
    {
        Columns = columns;
        Rows = rows;
        RowNumberWidth = rowNumberWidth;
        IsLimited = isLimited;
        Width = rowNumberWidth + columns.Sum(column => column.Width);
    }

    public IReadOnlyList<CsvPreviewColumn> Columns { get; }

    public IReadOnlyList<CsvPreviewRow> Rows { get; }

    /// <summary>The row-number gutter, wide enough for the last row's number.</summary>
    public double RowNumberWidth { get; }

    /// <summary>The whole table's width: it scrolls sideways when it is wider than the pane.</summary>
    public double Width { get; }

    /// <summary>The file holds more rows or columns than the preview shows.</summary>
    public bool IsLimited { get; }

    public bool HasRows => Rows.Count > 0;

    public string RowCountText => Strings.Get(
        Rows.Count == 1 ? "CsvRowsOne" : "CsvRowsMany", Rows.Count.ToString("N0", CultureInfo.CurrentCulture));

    public string SummaryText => RowCountText + " · " + Strings.Get(
        Columns.Count == 1 ? "CsvColumnsOne" : "CsvColumnsMany", Columns.Count.ToString("N0", CultureInfo.CurrentCulture));

    /// <param name="text">The file's text, possibly cut short at the preview's character bound.</param>
    /// <param name="isTruncated">The text was cut, so its last record may be incomplete.</param>
    /// <param name="delimiter">The separator when the format fixes it (TSV); detected otherwise.</param>
    public static CsvPreviewTable Parse(string text, bool isTruncated, char? delimiter = null)
    {
        // Excel's "sep=;" first line names the separator and is not data.
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length >= 5
            && text[4] is ',' or ';' or '\t' or '|'
            && (text.Length == 5 || text[5] is '\r' or '\n'))
        {
            delimiter = text[4];
            text = text[5..].TrimStart('\r', '\n');
        }

        var separator = delimiter ?? DetectDelimiter(text);
        var records = ReadRecords(text, separator, MaxRows + 1, isTruncated, out var hasMoreRecords);
        var limited = hasMoreRecords || records.Count > MaxRows + 1;
        if (records.Count == 0)
            return new CsvPreviewTable([], [], RowNumberWidthFor(0), limited);

        var header = records[0];
        var data = records.Skip(1).Take(MaxRows).ToList();
        limited |= records.Count - 1 > MaxRows;
        var columnCount = Math.Max(header.Length, data.Count == 0 ? 0 : data.Max(record => record.Length));
        if (columnCount > MaxColumns)
        {
            columnCount = MaxColumns;
            limited = true;
        }

        var columns = new CsvPreviewColumn[columnCount];
        for (var column = 0; column < columnCount; column++)
        {
            var name = column < header.Length && !string.IsNullOrWhiteSpace(header[column])
                ? header[column].Trim()
                : Strings.Get("CsvColumnNamed", column + 1);
            var longest = name.Length;
            var numeric = true;
            var hasValue = false;
            for (var row = 0; row < data.Count; row++)
            {
                var value = column < data[row].Length ? data[row][column] : "";
                if (row < MeasuredRows)
                    longest = Math.Max(longest, LongestLine(value));
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                hasValue = true;
                numeric &= decimal.TryParse(value.Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out _);
            }

            columns[column] = new CsvPreviewColumn(
                name,
                Math.Clamp(Math.Ceiling(longest * CharacterWidth + CellPadding), MinColumnWidth, MaxColumnWidth),
                numeric && hasValue);
        }

        var numberWidth = RowNumberWidthFor(data.Count);
        var rows = new CsvPreviewRow[data.Count];
        for (var row = 0; row < data.Count; row++)
        {
            var record = data[row];
            var cells = new CsvPreviewCell[columnCount];
            for (var column = 0; column < columnCount; column++)
            {
                var value = column < record.Length ? record[column] : "";
                cells[column] = new CsvPreviewCell(
                    Flatten(value), columns[column].Width, columns[column].IsNumeric);
            }

            rows[row] = new CsvPreviewRow(row + 1, cells, row % 2 == 1, numberWidth);
        }

        return new CsvPreviewTable(columns, rows, numberWidth, limited);
    }

    private static double RowNumberWidthFor(int rows) =>
        Math.Max(40, rows.ToString(CultureInfo.InvariantCulture).Length * 8.5 + 24);

    /// <summary>A cell is one line in the table; its line breaks read as spaces.</summary>
    private static string Flatten(string value) =>
        value.Contains('\n') || value.Contains('\r')
            ? value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ')
            : value;

    private static int LongestLine(string value)
    {
        var longest = 0;
        foreach (var line in value.Split('\n'))
            longest = Math.Max(longest, line.TrimEnd('\r').Length);
        return longest;
    }

    private static char DetectDelimiter(string text)
    {
        ReadOnlySpan<char> candidates = [',', ';', '\t', '|'];
        Span<int> counts = stackalloc int[candidates.Length];
        counts.Clear();
        var quoted = false;
        foreach (var character in text)
        {
            if (character == '"')
                quoted = !quoted;
            else if (!quoted && character is '\r' or '\n')
                break;
            else if (!quoted && candidates.IndexOf(character) is var index and >= 0)
                counts[index]++;
        }

        var best = 0;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > counts[best])
                best = i;
        }

        return candidates[best];
    }

    /// <summary>
    /// RFC 4180 records: quoted fields may hold separators, doubled quotes and line breaks. Blank lines
    /// are skipped, and a record the text was cut inside is dropped rather than shown half-read.
    /// </summary>
    private static List<string[]> ReadRecords(
        string text, char delimiter, int maxRecords, bool isTruncated, out bool hasMore)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var atFieldStart = true;
        hasMore = false;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (quoted)
            {
                if (character != '"')
                    field.Append(character);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                    quoted = false;
                continue;
            }

            if (character == '"' && atFieldStart)
            {
                quoted = true;
                atFieldStart = false;
            }
            else if (character == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                if (fields.Count > 1 || fields[0].Length > 0)
                    records.Add([.. fields]);
                fields.Clear();
                if (records.Count >= maxRecords)
                {
                    hasMore = text.AsSpan(i + 1).Trim().Length > 0 || isTruncated;
                    return records;
                }
            }
            else
            {
                field.Append(character);
                atFieldStart = false;
            }
        }

        // The last record has no line break after it. If the text was cut, it may be cut short too.
        if (!isTruncated && !quoted && (fields.Count > 0 || field.Length > 0))
        {
            fields.Add(field.ToString());
            records.Add([.. fields]);
        }

        hasMore = isTruncated;
        return records;
    }
}
