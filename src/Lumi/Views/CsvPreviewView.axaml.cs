using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Lumi.Localization;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class CsvPreviewView : UserControl
{
    private CsvPreviewViewModel? _viewModel;

    public CsvPreviewView()
    {
        InitializeComponent();
        CsvTable.CopyingRowClipboardContent += OnCopyingRowClipboardContent;
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as CsvPreviewViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildColumns();
        base.OnDataContextChanged(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CsvPreviewViewModel.Columns))
            RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (CsvTable is null)
            return;
        CsvTable.Columns.Clear();
        if (_viewModel is null)
            return;

        CsvTable.Columns.Add(new DataGridTemplateColumn
        {
            Header = "#", Width = new DataGridLength(64),
            CanUserSort = false, CanUserResize = false, CanUserReorder = false,
            CellTemplate = new FuncDataTemplate<CsvPreviewRow>((row, _) =>
                CreateCell(row?.Number.ToString(Loc.Culture) ?? "", "csv-row-number", true))
        });
        for (var index = 0; index < _viewModel.Columns.Count; index++)
        {
            var columnIndex = index;
            var column = _viewModel.Columns[index];
            CsvTable.Columns.Add(new DataGridTemplateColumn
            {
                Header = column.Header,
                HeaderTemplate = new FuncDataTemplate<string>((header, _) => CreateCell(header ?? "", "csv-header")),
                Width = DataGridLength.Auto, MinWidth = 120, MaxWidth = 360,
                CanUserSort = true, Tag = index,
                CustomSortComparer = new CsvPreviewRowComparer(index, column.IsNumeric),
                CellTemplate = new FuncDataTemplate<CsvPreviewRow>((row, _) => CreateCell(
                    row?.Cells[columnIndex] ?? "", column.IsNumeric ? "csv-numeric" : null, column.IsNumeric))
            });
        }
    }

    private void OnCopyingRowClipboardContent(object? sender, DataGridRowClipboardEventArgs e)
    {
        e.ClipboardRowContent.Clear();
        foreach (var column in CsvTable.Columns.OrderBy(column => column.DisplayIndex))
        {
            if (column.Tag is not int index)
                continue;
            object content = column.Header;
            if (!e.IsColumnHeadersRow)
            {
                if (e.Item is not CsvPreviewRow row)
                    throw new System.InvalidOperationException("CSV clipboard content requires a CSV row.");
                content = row.Cells[index];
            }
            e.ClipboardRowContent.Add(new DataGridClipboardCellContent(e.Item, column, content));
        }
    }

    private static TextBlock CreateCell(string value, string? styleClass = null, bool alignRight = false)
    {
        var text = new TextBlock
        {
            Text = value.Replace("\r\n", "  ").Replace('\r', ' ').Replace('\n', ' '),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left
        };
        if (styleClass is not null)
            text.Classes.Add(styleClass);
        ToolTip.SetTip(text, value);
        return text;
    }
}
