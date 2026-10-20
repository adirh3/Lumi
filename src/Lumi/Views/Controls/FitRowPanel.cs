using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.Views.Controls;

/// <summary>
/// A single row that never wraps. Children keep their natural width while the row has room; when it
/// does not, the narrower ones keep theirs and the wider ones share what is left equally, so every
/// child stays in view and only the longest give way. The CSS row of <c>flex: 0 1 auto; min-width: 0</c>
/// items, shared fairly rather than in proportion to each item's width.
/// </summary>
public sealed class FitRowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FitRowPanel, double>(nameof(Spacing), 8);

    /// <summary>
    /// Set by the row on each child: true while the child is given less than its natural width, so
    /// it can offer what it had to cut (a trimmed label's full text in a tip, say).
    /// </summary>
    public static readonly AttachedProperty<bool> IsShrunkProperty =
        AvaloniaProperty.RegisterAttached<FitRowPanel, Control, bool>("IsShrunk");

    private readonly List<Control> _visible = [];
    private double[] _naturalWidths = [];

    static FitRowPanel() => AffectsMeasure<FitRowPanel>(SpacingProperty);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetIsShrunk(Control control) => control.GetValue(IsShrunkProperty);

    private static void SetIsShrunk(Control control, bool value) => control.SetValue(IsShrunkProperty, value);

    /// <summary>
    /// Each item's width in a row <paramref name="available"/> wide: the natural widths when they fit;
    /// otherwise, from the narrowest up, each item takes its natural width or an equal share of what
    /// is left, whichever is smaller. Shares are whole pixels, so layout rounding can never push the
    /// last item past the end of the row.
    /// </summary>
    internal static double[] ComputeWidths(IReadOnlyList<double> naturalWidths, double available, double spacing)
    {
        var count = naturalWidths.Count;
        var widths = new double[count];
        var total = 0d;
        for (var i = 0; i < count; i++)
        {
            widths[i] = naturalWidths[i];
            total += naturalWidths[i];
        }

        if (count == 0 || !double.IsFinite(available))
            return widths;

        var remaining = Math.Max(0, available - spacing * (count - 1));
        if (total <= remaining)
            return widths;

        var order = new int[count];
        for (var i = 0; i < count; i++)
            order[i] = i;
        Array.Sort(order, (a, b) => naturalWidths[a].CompareTo(naturalWidths[b]));

        var left = count;
        foreach (var index in order)
        {
            var width = Math.Min(naturalWidths[index], Math.Floor(remaining / left));
            widths[index] = width;
            remaining -= width;
            left--;
        }

        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        CollectVisibleChildren();
        _naturalWidths = new double[_visible.Count];
        for (var i = 0; i < _visible.Count; i++)
        {
            _visible[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _naturalWidths[i] = _visible[i].DesiredSize.Width;
        }

        var widths = ComputeWidths(_naturalWidths, availableSize.Width, Spacing);
        double width = 0;
        double height = 0;
        for (var i = 0; i < _visible.Count; i++)
        {
            // Measure again within the share so the child lays out (and trims) at the width it gets.
            if (widths[i] < _naturalWidths[i])
                _visible[i].Measure(new Size(widths[i], availableSize.Height));
            width += widths[i];
            height = Math.Max(height, _visible[i].DesiredSize.Height);
        }

        if (_visible.Count > 1)
            width += Spacing * (_visible.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = ComputeWidths(_naturalWidths, finalSize.Width, Spacing);
        double x = 0;
        for (var i = 0; i < _visible.Count; i++)
        {
            var child = _visible[i];
            child.Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            SetIsShrunk(child, widths[i] < _naturalWidths[i]);
            x += widths[i] + Spacing;
        }

        return finalSize;
    }

    private void CollectVisibleChildren()
    {
        _visible.Clear();
        foreach (var child in Children)
        {
            if (child.IsVisible)
                _visible.Add(child);
        }
    }
}
