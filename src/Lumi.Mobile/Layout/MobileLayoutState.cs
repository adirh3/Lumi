namespace Lumi.Mobile.Layout;

/// <summary>Material-style width buckets. Drives how many panes the shell shows.</summary>
public enum WidthSizeClass
{
    /// <summary>Bar phone portrait, or a flip phone's folded cover display.</summary>
    Compact,

    /// <summary>Large phone landscape, folded outer display of a book-style foldable, small tablet.</summary>
    Medium,

    /// <summary>Unfolded foldable or tablet — wide enough for list + detail side by side.</summary>
    Expanded
}

/// <summary>
/// Physical posture of a folding device. Avalonia has no cross-platform hinge API, so this is fed
/// by the host (an Android head can map <c>WindowLayoutInfo</c>; the desktop simulator sets it directly).
/// </summary>
public enum FoldPosture
{
    /// <summary>Flat: one continuous surface.</summary>
    Flat,

    /// <summary>Book posture — hinge runs top-to-bottom, splitting the screen left/right.</summary>
    BookVerticalHinge,

    /// <summary>Laptop/tabletop posture — hinge runs left-to-right, splitting the screen top/bottom.</summary>
    TabletopHorizontalHinge
}

/// <summary>
/// The single source of truth for adaptive layout. Pure logic with no Avalonia dependency so every
/// form factor the app claims to support can be unit tested exhaustively.
/// </summary>
public readonly record struct MobileLayoutState
{
    public const double MediumWidthThreshold = 600;
    public const double ExpandedWidthThreshold = 840;

    /// <summary>
    /// Minimum detail-pane width worth showing next to the list. Sized so an unfolded book foldable
    /// (each half ≈430 dp) still splits at its hinge; below this we stay single pane.
    /// </summary>
    public const double MinimumDetailPaneWidth = 400;

    public const double ListPaneMinWidth = 300;

    public double Width { get; init; }
    public double Height { get; init; }
    public WidthSizeClass WidthClass { get; init; }

    /// <summary>Logical size of the hinge occlusion in device-independent pixels (0 when there is none).</summary>
    public double HingeSize { get; init; }

    /// <summary>Offset of the hinge from the left edge (book posture only).</summary>
    public double HingePosition { get; init; }
    public double HorizontalHingeSize { get; init; }
    public double HorizontalHingePosition { get; init; }

    public static WidthSizeClass ClassifyWidth(double width) => width switch
    {
        < MediumWidthThreshold => WidthSizeClass.Compact,
        < ExpandedWidthThreshold => WidthSizeClass.Medium,
        _ => WidthSizeClass.Expanded
    };

    public static MobileLayoutState From(
        double width,
        double height,
        FoldPosture posture = FoldPosture.Flat,
        double hingeSize = 0,
        double hingePosition = 0)
    {
        width = double.IsFinite(width) && width > 0 ? width : 1;
        height = double.IsFinite(height) && height > 0 ? height : 1;

        var widthClass = ClassifyWidth(width);

        // Book posture folds the screen into two logical halves. Honour the hinge instead of
        // splitting arbitrarily, so no content is ever painted underneath it.
        var splitAtHinge = posture == FoldPosture.BookVerticalHinge
                           && hingePosition >= ListPaneMinWidth
                           && width - (hingePosition + Math.Max(hingeSize, 1)) >= MinimumDetailPaneWidth;
        var splitAtHorizontalHinge = posture == FoldPosture.TabletopHorizontalHinge
                                     && hingePosition >= 300
                                     && height - (hingePosition + Math.Max(hingeSize, 1)) >= 200;

        return new MobileLayoutState
        {
            Width = width,
            Height = height,
            WidthClass = widthClass,
            // Only report the hinge when the panes actually meet there. Reporting it otherwise would
            // paint an empty stripe away from the physical fold while content sat under the real one.
            HingeSize = splitAtHinge ? Math.Max(hingeSize, 1) : 0,
            HingePosition = splitAtHinge ? hingePosition : 0,
            HorizontalHingeSize = splitAtHorizontalHinge ? Math.Max(hingeSize, 1) : 0,
            HorizontalHingePosition = splitAtHorizontalHinge ? hingePosition : 0
        };
    }
}
