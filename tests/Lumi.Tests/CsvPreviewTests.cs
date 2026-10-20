using Avalonia.Collections;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class CsvPreviewTests
{
    [Fact]
    public void QuotedFieldsEscapedQuotesEmptyCellsAndMultilineRecordsAreParsed()
    {
        var data = CsvPreviewData.Parse(
            "Name,Notes,Empty,Trailing\r\n\"Adir, H\",\"He said \"\"hello\"\"\r\nNext line\",,\r\n", false);

        Assert.Equal(',', data.Delimiter);
        Assert.Equal(4, data.ColumnCount);
        Assert.Equal(2, data.Records.Count);
        Assert.Equal("Adir, H", data.Records[1][0]);
        Assert.Equal("He said \"hello\"\r\nNext line", data.Records[1][1]);
        Assert.Equal("", data.Records[1][2]);
        Assert.Equal("", data.Records[1][3]);
    }

    [Theory]
    [InlineData("Name,Price\nA,10", ',')]
    [InlineData("Name;Price\nA;10", ';')]
    [InlineData("Name\tPrice\nA\t10", '\t')]
    [InlineData("Name|Price\nA|10", '|')]
    [InlineData("\r\n\"Name, with commas\";\"Price\"\nA;10", ';')]
    [InlineData("\"Name; with semicolons\",Price\nA,10", ',')]
    [InlineData("sep=;\r\nName;Price\r\nA;10", ';')]
    public void CommonSeparatorsAndExcelDirectiveAreDetected(string text, char delimiter)
    {
        var data = CsvPreviewData.Parse(text, false);
        Assert.Equal(delimiter, data.Delimiter);
        Assert.Equal(2, data.ColumnCount);
        Assert.Equal(2, data.Records.Count);
        Assert.Equal(["A", "10"], data.Records[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\r\n")]
    public void EmptyFilesHaveNoRecords(string text)
    {
        var data = CsvPreviewData.Parse(text, false);
        Assert.Empty(data.Records);
        Assert.Equal(0, data.ColumnCount);
    }

    [Fact]
    public void EmptyQuotedFieldsAndWhitespaceAreNotLost()
    {
        var data = CsvPreviewData.Parse("Value,Other\n\"\",  padded  \n", false);
        Assert.Equal(["", "  padded  "], data.Records[1]);
    }

    [Theory]
    [InlineData("Name,Notes\nComplete,OK\nPartial,unfinished")]
    [InlineData("Name,Notes\nComplete,OK\nPartial,\"unfinished\nquoted")]
    [InlineData("Name,Notes\nComplete,OK\nPartial,\"escaped \"\"quote")]
    public void TruncatedPreviewNeverIncludesAPartialRecord(string text)
    {
        var data = CsvPreviewData.Parse(text, true);
        Assert.Equal(2, data.Records.Count);
        Assert.Equal(["Complete", "OK"], data.Records[1]);
    }

    [Fact]
    public void TruncationPreservesCompleteQuotedMultilineRecords()
    {
        var data = CsvPreviewData.Parse("Name,Notes\nA,\"line 1\nline 2\"\nB,\"unfinished\n", true);
        Assert.Equal(2, data.Records.Count);
        Assert.Equal("line 1\nline 2", data.Records[1][1]);
        Assert.Empty(CsvPreviewData.Parse("\"A very long first record", true).Records);
    }

    [Fact]
    public void InvalidCompleteRecordsAndCancellationAreExplicitFailures()
    {
        Assert.Throws<FormatException>(() => CsvPreviewData.Parse("Name,Notes\nA,\"unfinished", false));
        Assert.Throws<FormatException>(() => CsvPreviewData.Parse("Name,Notes\nA,\"closed\"unexpected\n", true));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CsvPreviewData.Parse("", false, cancellationToken: cts.Token));
    }

    [Fact]
    public void HeaderToggleKeepsAllCellsAndPadsRaggedRecords()
    {
        var vm = CreateViewModel("Name,,Name\nA,2,extra,last\nB\n");
        Assert.Equal(4, vm.Columns.Count);
        Assert.Equal("Name", vm.Columns[0].Header);
        Assert.Equal(Loc.Get("Csv_Column", 2), vm.Columns[1].Header);
        Assert.Equal("Name", vm.Columns[2].Header);
        Assert.Equal(Loc.Get("Csv_Column", 4), vm.Columns[3].Header);
        Assert.Equal(["B", "", "", ""], Rows(vm)[1].Cells);
        Assert.Equal([2, 3], Rows(vm).Select(row => row.Number));

        vm.HasHeaderRow = false;
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(["Name", "", "Name", ""], Rows(vm)[0].Cells);
        Assert.Equal(Loc.Get("Csv_Column", 1), vm.Columns[0].Header);
        Assert.Equal([1, 2, 3], Rows(vm).Select(row => row.Number));
    }

    [Fact]
    public void HeaderActionTogglesWithoutLosingTheFirstRecordAndFormatsSingularStats()
    {
        var vm = CreateViewModel("Value\nOne\n");
        Assert.Equal(Loc.Csv_RowCountSingular, vm.RowCountLabel);
        Assert.Equal(Loc.Csv_ColumnCountSingular, vm.ColumnCountLabel);
        Assert.Equal(Loc.Csv_UseFirstRowAsData, vm.HeaderActionLabel);
        vm.SearchText = "One";
        Assert.Equal(Loc.Get("Csv_FilteredRowsSingular", 1, 1), vm.RowCountLabel);
        vm.ClearSearchCommand.Execute(null);
        vm.ToggleHeaderRowCommand.Execute(null);
        Assert.Equal(["Value", "One"], Rows(vm).Select(row => row.Cells[0]));
        Assert.Equal(Loc.Csv_UseFirstRowAsHeaders, vm.HeaderActionLabel);
        vm.ToggleHeaderRowCommand.Execute(null);
        Assert.Equal("One", Assert.Single(Rows(vm)).Cells[0]);
    }

    [Fact]
    public void SearchMatchesEveryCellAndPreservesSortOrderAndOriginalRowNumbers()
    {
        var vm = CreateViewModel("Name,Price,Notes\nTen,10,group\nTwo,2,GROUP\nOther,-1,elsewhere\n");
        Assert.True(vm.Columns[1].IsNumeric);
        vm.Rows.SortDescriptions.Add(DataGridSortDescription.FromComparer(new CsvPreviewRowComparer(1, true)));
        Assert.Equal(["-1", "2", "10"], Rows(vm).Select(row => row.Cells[1]));

        vm.SearchText = " group ";
        Assert.Equal(["Two", "Ten"], Rows(vm).Select(row => row.Cells[0]));
        Assert.Equal([3, 2], Rows(vm).Select(row => row.Number));
        Assert.Equal(Loc.Get("Csv_FilteredRows", 2, 3), vm.RowCountLabel);
        vm.SearchText = "does not exist";
        Assert.False(vm.HasRows);
        Assert.Equal(Loc.Csv_NoMatches, vm.EmptyTitle);

        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(["-1", "2", "10"], Rows(vm).Select(row => row.Cells[1]));
        Assert.False(vm.HasSearch);
    }

    [Fact]
    public void NumericSortingHandlesBlanksDuplicatesNegativesAndThousands()
    {
        var vm = CreateViewModel("Value\n10\n\"1,200\"\n-2\n\" \"\n2\n2\n");
        Assert.True(vm.Columns[0].IsNumeric);
        vm.Rows.SortDescriptions.Add(DataGridSortDescription.FromComparer(new CsvPreviewRowComparer(0, true)));
        Assert.Equal([" ", "-2", "2", "2", "10", "1,200"], Rows(vm).Select(row => row.Cells[0]));
        Assert.Equal([6, 7], Rows(vm).Where(row => row.Cells[0] == "2").Select(row => row.Number));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Name,Price\n", false)]
    [InlineData("\"incomplete", true)]
    public void EmptyStatesDistinguishEmptyHeaderOnlyAndTruncatedFiles(string text, bool isTruncated)
    {
        var vm = new CsvPreviewViewModel(CsvPreviewData.Parse(text, isTruncated), isTruncated);
        Assert.False(vm.HasRows);
        Assert.Equal(isTruncated ? Loc.Csv_NoCompleteRows
            : text.Length == 0 ? Loc.Preview_Empty : Loc.Csv_NoDataRows, vm.EmptyTitle);
        if (text.Length > 0 && !isTruncated)
        {
            vm.HasHeaderRow = false;
            Assert.True(vm.HasRows);
        }
    }

    private static CsvPreviewViewModel CreateViewModel(string text)
        => new(CsvPreviewData.Parse(text, false), false);

    private static CsvPreviewRow[] Rows(CsvPreviewViewModel vm) => vm.Rows.Cast<CsvPreviewRow>().ToArray();
}
