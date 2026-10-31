using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

/// <summary>
/// One row of a CSV preview, drawn as its cells' text in their columns rather than built from a control
/// per cell: a screenful of rows then takes a few lines of layout each, not dozens of styled controls.
/// Its row is its data context; numbers line up on the right, and text too long for its column ends in
/// an ellipsis, as the table's header does.
/// </summary>
public sealed class CsvRowView : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CsvRowView>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<CsvRowView>();

    /// <summary>The row number's colour, quieter than the cells'.</summary>
    public static readonly StyledProperty<IBrush?> NumberForegroundProperty =
        AvaloniaProperty.Register<CsvRowView, IBrush?>(nameof(NumberForeground));

    public static readonly StyledProperty<double> NumberFontSizeProperty =
        AvaloniaProperty.Register<CsvRowView, double>(nameof(NumberFontSize), 12);

    /// <summary>Every other row is shaded, so a row is easy to follow across a wide table.</summary>
    public static readonly StyledProperty<IBrush?> AlternateBackgroundProperty =
        AvaloniaProperty.Register<CsvRowView, IBrush?>(nameof(AlternateBackground));

    /// <summary>The space between a cell's text and its column's edges, as the header's cells keep.</summary>
    internal const double CellPadding = 12;

    private CsvPreviewRow? _row;
    private TextLayout? _number;
    private TextLayout?[] _cells = [];

    static CsvRowView()
    {
        AffectsRender<CsvRowView>(ForegroundProperty, FontSizeProperty, NumberForegroundProperty,
            NumberFontSizeProperty, AlternateBackgroundProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? NumberForeground
    {
        get => GetValue(NumberForegroundProperty);
        set => SetValue(NumberForegroundProperty, value);
    }

    public double NumberFontSize
    {
        get => GetValue(NumberFontSizeProperty);
        set => SetValue(NumberFontSizeProperty, value);
    }

    public IBrush? AlternateBackground
    {
        get => GetValue(AlternateBackgroundProperty);
        set => SetValue(AlternateBackgroundProperty, value);
    }

    /// <summary>The text of the row's cells as laid out, for tests and accessibility.</summary>
    internal IReadOnlyList<string> CellTexts => _row?.Cells.Select(cell => cell.Text).ToArray() ?? [];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _row = DataContext as CsvPreviewRow;
        ReleaseText();
        AutomationProperties.SetName(this, _row is null
            ? string.Empty
            : $"{_row.NumberText}: {string.Join(", ", _row.Cells.Select(cell => cell.Text))}");
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty || change.Property == FontSizeProperty
            || change.Property == NumberForegroundProperty || change.Property == NumberFontSizeProperty
            || change.Property == TextElement.FontFamilyProperty)
        {
            ReleaseText();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _row is null ? default : new Size(_row.NumberWidth + _row.Cells.Sum(cell => cell.Width), 0);

    public override void Render(DrawingContext context)
    {
        if (_row is not { } row)
            return;

        var height = Bounds.Height;
        if (row.IsAlternate && AlternateBackground is { } shade)
            context.FillRectangle(shade, new Rect(Bounds.Size));

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        _number ??= Layout(row.NumberText, typeface, NumberFontSize, NumberForeground, TextAlignment.Right,
            row.NumberWidth - CellPadding);
        _number.Draw(context, new Point(0, (height - _number.Height) / 2));

        if (_cells.Length != row.Cells.Count)
            _cells = new TextLayout?[row.Cells.Count];
        var x = row.NumberWidth;
        for (var index = 0; index < row.Cells.Count; index++)
        {
            var cell = row.Cells[index];
            if (cell.Text.Length > 0)
            {
                var layout = _cells[index] ??= Layout(cell.Text, typeface, FontSize, Foreground,
                    cell.IsNumeric ? TextAlignment.Right : TextAlignment.Left, cell.Width - CellPadding * 2);
                layout.Draw(context, new Point(x + CellPadding, (height - layout.Height) / 2));
            }

            x += cell.Width;
        }
    }

    private static TextLayout Layout(
        string text, Typeface typeface, double fontSize, IBrush? foreground, TextAlignment alignment, double width) =>
        new(text, typeface, fontSize, foreground, alignment, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis,
            maxWidth: Math.Max(1, width), maxLines: 1);

    private void ReleaseText()
    {
        _number?.Dispose();
        _number = null;
        foreach (var cell in _cells)
            cell?.Dispose();
        _cells = [];
        InvalidateVisual();
    }
}
