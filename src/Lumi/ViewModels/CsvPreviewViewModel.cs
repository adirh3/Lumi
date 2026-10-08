using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;

namespace Lumi.ViewModels;

public sealed record CsvPreviewRow(int Number, string[] Cells);

public sealed record CsvPreviewColumn(string Header, bool IsNumeric);

public sealed partial class CsvPreviewViewModel : ObservableObject
{
    private readonly CsvPreviewData _data;
    private readonly bool _isTruncated;
    private CsvPreviewRow[] _allRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderActionLabel))]
    private bool _hasHeaderRow = true;
    [ObservableProperty] private IReadOnlyList<CsvPreviewColumn> _columns = [];
    [ObservableProperty] private DataGridCollectionView _rows = new(Array.Empty<CsvPreviewRow>());

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasRows => Rows.Count > 0;
    public string RowCountLabel => HasSearch
        ? Loc.Get(_allRows.Length == 1 ? "Csv_FilteredRowsSingular" : "Csv_FilteredRows", Rows.Count, _allRows.Length)
        : _allRows.Length == 1 ? Loc.Csv_RowCountSingular : Loc.Get("Csv_RowCount", _allRows.Length);
    public string ColumnCountLabel => Columns.Count == 1
        ? Loc.Csv_ColumnCountSingular : Loc.Get("Csv_ColumnCount", Columns.Count);
    public string HeaderActionLabel => HasHeaderRow ? Loc.Csv_UseFirstRowAsData : Loc.Csv_UseFirstRowAsHeaders;
    public string DelimiterLabel => _data.Delimiter switch
    {
        ';' => Loc.Csv_Semicolon,
        '\t' => Loc.Csv_Tab,
        '|' => Loc.Csv_Pipe,
        _ => Loc.Csv_Comma
    };
    public string InteractionHint => Loc.AdaptKeyboardHint(Loc.Csv_InteractionHint);
    public string EmptyTitle => HasSearch ? Loc.Csv_NoMatches
        : _data.Records.Count > 0 ? Loc.Csv_NoDataRows
        : _isTruncated ? Loc.Csv_NoCompleteRows : Loc.Preview_Empty;
    public string EmptyDetail => HasSearch ? Loc.Csv_NoMatchesHint
        : _data.Records.Count > 0 ? Loc.Csv_HeaderRowHint : "";

    internal CsvPreviewViewModel(CsvPreviewData data, bool isTruncated)
    {
        _data = data;
        _isTruncated = isTruncated;
        RebuildRows();
    }

    partial void OnHasHeaderRowChanged(bool value) => RebuildRows();

    partial void OnSearchTextChanged(string value)
    {
        Rows.Refresh();
        NotifySummary();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void ToggleHeaderRow() => HasHeaderRow = !HasHeaderRow;

    private void RebuildRows()
    {
        var start = HasHeaderRow && _data.Records.Count > 0 ? 1 : 0;
        _allRows = _data.Records.Skip(start).Select((record, index) =>
            new CsvPreviewRow(index + start + 1, Enumerable.Range(0, _data.ColumnCount)
                .Select(column => column < record.Length ? record[column] : "").ToArray())).ToArray();
        Columns = Enumerable.Range(0, _data.ColumnCount).Select(column =>
        {
            var header = HasHeaderRow && _data.Records.Count > 0 && column < _data.Records[0].Length
                ? _data.Records[0][column].Trim() : "";
            var values = _allRows.Select(row => row.Cells[column])
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            return new CsvPreviewColumn(
                header.Length > 0 ? header : Loc.Get("Csv_Column", column + 1),
                values.Length > 0 && values.All(value => decimal.TryParse(value,
                    CsvPreviewRowComparer.NumericStyles, CultureInfo.InvariantCulture, out _)));
        }).ToArray();
        Rows = new DataGridCollectionView(_allRows) { Filter = MatchesSearch };
        NotifySummary();
    }

    private bool MatchesSearch(object item)
        => item is CsvPreviewRow row && (!HasSearch || row.Cells.Any(cell =>
            cell.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)));

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(RowCountLabel));
        OnPropertyChanged(nameof(ColumnCountLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
    }
}

internal sealed class CsvPreviewRowComparer(int column, bool isNumeric) : IComparer
{
    internal const NumberStyles NumericStyles = NumberStyles.Float | NumberStyles.AllowThousands;

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        if (x is not CsvPreviewRow left || y is not CsvPreviewRow right)
            throw new ArgumentException("CSV sorting requires CSV rows.");

        var leftValue = left.Cells[column];
        var rightValue = right.Cells[column];
        int result;
        if (isNumeric)
        {
            var leftEmpty = string.IsNullOrWhiteSpace(leftValue);
            var rightEmpty = string.IsNullOrWhiteSpace(rightValue);
            result = leftEmpty || rightEmpty
                ? rightEmpty.CompareTo(leftEmpty)
                : decimal.Parse(leftValue, NumericStyles, CultureInfo.InvariantCulture)
                    .CompareTo(decimal.Parse(rightValue, NumericStyles, CultureInfo.InvariantCulture));
        }
        else
            result = StringComparer.CurrentCultureIgnoreCase.Compare(leftValue, rightValue);
        return result != 0 ? result : left.Number.CompareTo(right.Number);
    }
}
