using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.Views.Controls;

/// <summary>
/// Lays children out from its lower right corner: right to left along its bottom line, then line by line upward, so in a
/// shelf that unfolds above a row's end its first child sits nearest that end. Lines wrap at the width given.
/// </summary>
public sealed class ShelfPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ShelfPanel, double>(nameof(Spacing), 8);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<ShelfPanel, double>(nameof(LineSpacing), 8);

    static ShelfPanel() => AffectsMeasure<ShelfPanel>(SpacingProperty, LineSpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>The children each line holds, bottom line first, as the last measure found them.</summary>
    private readonly List<(int Start, int Count, double Width, double Height)> _lines = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        BuildLines(availableSize.Width);
        double width = 0, height = 0;
        foreach (var line in _lines)
        {
            width = Math.Max(width, line.Width);
            height += line.Height;
        }

        if (_lines.Count > 1)
            height += LineSpacing * (_lines.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bottom = finalSize.Height;
        foreach (var (start, count, _, height) in _lines)
        {
            var right = finalSize.Width;
            for (var index = start; index < start + count; index++)
            {
                var child = Children[index];
                if (!child.IsVisible)
                    continue;

                var size = child.DesiredSize;
                child.Arrange(new Rect(right - size.Width, bottom - height + (height - size.Height) / 2, size.Width, size.Height));
                right -= size.Width + Spacing;
            }

            bottom -= height + LineSpacing;
        }

        return finalSize;
    }

    private void BuildLines(double available)
    {
        _lines.Clear();
        int start = 0, count = 0;
        double width = 0, height = 0;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            if (!child.IsVisible)
            {
                count++;
                continue;
            }

            var size = child.DesiredSize;
            var next = width == 0 ? size.Width : width + Spacing + size.Width;
            if (width > 0 && next > available)
            {
                _lines.Add((start, count, width, height));
                start = index;
                count = 0;
                width = 0;
                height = 0;
                next = size.Width;
            }

            width = next;
            height = Math.Max(height, size.Height);
            count++;
        }

        if (count > 0)
            _lines.Add((start, count, width, height));
    }
}
