using Avalonia;
using Avalonia.Controls;

namespace Lumi.Mobile.Layout;

/// <summary>
/// A place's list and its open item. With room for two panes the list keeps a fixed column and the
/// item fills the rest, like the desktop's sidebar and page; with room for one, whichever is in view
/// fills the screen and the other steps aside. The first child is the list, the second the item.
/// </summary>
public sealed class ListDetailPanel : Panel
{
    public static readonly StyledProperty<bool> IsTwoPaneProperty =
        AvaloniaProperty.Register<ListDetailPanel, bool>(nameof(IsTwoPane));

    public static readonly StyledProperty<double> ListWidthProperty =
        AvaloniaProperty.Register<ListDetailPanel, double>(nameof(ListWidth), 360);

    /// <summary>One pane: the item is in view instead of the list.</summary>
    public static readonly StyledProperty<bool> IsDetailOpenProperty =
        AvaloniaProperty.Register<ListDetailPanel, bool>(nameof(IsDetailOpen));

    static ListDetailPanel()
    {
        AffectsMeasure<ListDetailPanel>(IsTwoPaneProperty, ListWidthProperty, IsDetailOpenProperty);
    }

    public bool IsTwoPane
    {
        get => GetValue(IsTwoPaneProperty);
        set => SetValue(IsTwoPaneProperty, value);
    }

    public double ListWidth
    {
        get => GetValue(ListWidthProperty);
        set => SetValue(ListWidthProperty, value);
    }

    public bool IsDetailOpen
    {
        get => GetValue(IsDetailOpenProperty);
        set => SetValue(IsDetailOpenProperty, value);
    }

    private Control? ListPane => Children.Count > 0 ? Children[0] : null;

    private Control? DetailPane => Children.Count > 1 ? Children[1] : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsTwoPaneProperty || change.Property == IsDetailOpenProperty)
            UpdatePaneVisibility();
    }

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        UpdatePaneVisibility();
    }

    private void UpdatePaneVisibility()
    {
        if (ListPane is { } list)
            list.IsVisible = IsTwoPane || !IsDetailOpen;
        if (DetailPane is { } detail)
            detail.IsVisible = IsTwoPane || IsDetailOpen;
    }

    /// <summary>The list column's width for a given panel width.</summary>
    private double ListColumn(double width) =>
        double.IsFinite(width) ? Math.Clamp(ListWidth, 0, width) : ListWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var list = IsTwoPane ? ListColumn(availableSize.Width) : availableSize.Width;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var width = !IsTwoPane || i > 1 ? availableSize.Width
                : i == 0 ? list
                : Math.Max(0, availableSize.Width - list);
            child.Measure(new Size(width, availableSize.Height));
        }

        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var list = IsTwoPane ? ListColumn(finalSize.Width) : finalSize.Width;
        for (var i = 0; i < Children.Count; i++)
        {
            var rect = !IsTwoPane || i > 1 ? new Rect(finalSize)
                : i == 0 ? new Rect(0, 0, list, finalSize.Height)
                : new Rect(list, 0, Math.Max(0, finalSize.Width - list), finalSize.Height);
            Children[i].Arrange(rect);
        }

        return finalSize;
    }
}
