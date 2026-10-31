using System;
using System.Collections.Generic;
using System.Threading;
using Lumi.Localization;
using Lumi.Remote.Protocol;

namespace Lumi.Services;

/// <summary>A CSV or TSV file's records, read by the rules the phone shares (<see cref="CsvPreviewRecords"/>).</summary>
internal sealed record CsvPreviewData(char Delimiter, IReadOnlyList<string[]> Records, int ColumnCount)
{
    internal static CsvPreviewData Parse(string text, bool isTruncated, char? delimiter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = CsvPreviewRecords.Parse(text, isTruncated, delimiter, cancellationToken);
            return new(parsed.Delimiter, parsed.Records, parsed.ColumnCount);
        }
        catch (CsvPreviewFormatException ex)
        {
            throw new FormatException(Loc.Get("Csv_InvalidFormat", ex.LineNumber), ex);
        }
    }
}
