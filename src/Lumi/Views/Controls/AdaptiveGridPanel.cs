using System;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.Views.Controls;

/// <summary>
/// A responsive card grid: as many equal-width columns as fit at <see cref="MinItemWidth"/>, each
/// stretched to fill the row, with every card in a row sized to the tallest one. The layout CSS
/// expresses as <c>grid-template-columns: repeat(auto-fill, minmax(MinItemWidth, 1fr))</c>, which a
/// WrapPanel cannot do without leaving a ragged right edge.
/// </summary>
public sealed class AdaptiveGridPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(MinItemWidth), 240);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, int>(nameof(MaxColumns), 0);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(ColumnSpacing), 12);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<AdaptiveGridPanel, double>(nameof(RowSpacing), 12);

    static AdaptiveGridPanel()
    {
        AffectsMeasure<AdaptiveGridPanel>(
            MinItemWidthProperty,
            MaxColumnsProperty,
            ColumnSpacingProperty,
            RowSpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Upper bound on the column count; 0 means no limit.</summary>
    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    internal static int ComputeColumns(double availableWidth, double minItemWidth, double spacing, int maxColumns, int itemCount)
    {
        if (itemCount <= 0)
            return 1;

        var min = Math.Max(1, minItemWidth);
        var columns = double.IsFinite(availableWidth)
            ? (int)Math.Floor((availableWidth + spacing) / (min + spacing))
            : itemCount;
        columns = Math.Max(1, columns);
        if (maxColumns > 0)
            columns = Math.Min(columns, maxColumns);
        return columns;
    }

    private (int Columns, double ItemWidth) Resolve(double availableWidth)
    {
        var visibleCount = 0;
        foreach (var child in Children)
        {
            if (child.IsVisible)
                visibleCount++;
        }

        var columns = ComputeColumns(availableWidth, MinItemWidth, ColumnSpacing, MaxColumns, visibleCount);
        var width = double.IsFinite(availableWidth)
            ? Math.Max(0, (availableWidth - (columns - 1) * ColumnSpacing) / columns)
            : MinItemWidth;
        return (columns, width);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Resolve(availableSize.Width);
        var childConstraint = new Size(itemWidth, double.PositiveInfinity);

        double totalHeight = 0;
        double rowHeight = 0;
        var column = 0;
        var rows = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            child.Measure(childConstraint);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            column++;
            if (column == columns)
            {
                totalHeight += rowHeight;
                rows++;
                rowHeight = 0;
                column = 0;
            }
        }

        if (column > 0)
        {
            totalHeight += rowHeight;
            rows++;
        }

        if (rows > 1)
            totalHeight += (rows - 1) * RowSpacing;

        var desiredWidth = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : columns * itemWidth + (columns - 1) * ColumnSpacing;
        return new Size(rows == 0 ? 0 : desiredWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Resolve(finalSize.Width);

        var index = 0;
        var visible = new Control[Children.Count];
        foreach (var child in Children)
        {
            if (child.IsVisible)
                visible[index++] = child;
        }

        double top = 0;
        for (var rowStart = 0; rowStart < index; rowStart += columns)
        {
            var rowEnd = Math.Min(rowStart + columns, index);
            double rowHeight = 0;
            for (var i = rowStart; i < rowEnd; i++)
                rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);

            for (var i = rowStart; i < rowEnd; i++)
            {
                var x = (i - rowStart) * (itemWidth + ColumnSpacing);
                visible[i].Arrange(new Rect(x, top, itemWidth, rowHeight));
            }

            top += rowHeight + RowSpacing;
        }

        return finalSize;
    }
}
