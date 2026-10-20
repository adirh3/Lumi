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
    /// From this width the open place's list sits beside its content, as the desktop sidebar does.
    /// Below it the list is home and content opens over it. 700 rather than Material's 840 so a
    /// tablet held upright and an unfolded book foldable at a large display size still get two panes.
    /// </summary>
    public const double TwoPaneMinWidth = 700;

    /// <summary>From this width a supporting panel (plan, changes, sources) also fits beside the chat.</summary>
    public const double ThreePaneMinWidth = 1200;

    /// <summary>Below this height a phone held sideways keeps one pane and one-line chrome.</summary>
    public const double ShortHeightThreshold = 480;

    /// <summary>
    /// Wide enough that height never takes the second pane away. A browser host shrinks the window
    /// above its keyboard, and a tablet must not drop its list every time the keyboard opens.
    /// </summary>
    public const double WideLandscapeWidth = 1000;

    /// <summary>
    /// Minimum detail-pane width worth showing next to the list. Sized so an unfolded book foldable
    /// (each half ≈430 dp) still splits at its hinge; below this we stay single pane.
    /// </summary>
    public const double MinimumDetailPaneWidth = 400;

    /// <summary>The docked list's narrowest size: room for the places pill with the open place named.</summary>
    public const double ListPaneMinWidth = 320;

    /// <summary>The docked list's widest size; it narrows towards <see cref="ListPaneMinWidth"/> on smaller windows.</summary>
    public const double ListPaneMaxWidth = 360;

    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>
    /// The height that decides panes and chrome: the window's, except that a window which only got
    /// shorter keeps the one it had (see <see cref="Following"/>).
    /// </summary>
    public double PaneHeight { get; init; }

    public WidthSizeClass WidthClass { get; init; }

    /// <summary>Logical size of the hinge occlusion in device-independent pixels (0 when there is none).</summary>
    public double HingeSize { get; init; }

    /// <summary>Offset of the hinge from the left edge (book posture only).</summary>
    public double HingePosition { get; init; }
    public double HorizontalHingeSize { get; init; }
    public double HorizontalHingePosition { get; init; }

    /// <summary>
    /// Where a drawable crease (a foldable held half-open like a book) divides the screen, or 0. The
    /// list then fills exactly the left half, but nothing is reserved when it is hidden.
    /// </summary>
    public double FoldPosition { get; init; }

    /// <summary>The open place's list can sit beside its content.</summary>
    public bool CanShowTwoPanes =>
        Width >= TwoPaneMinWidth && (PaneHeight >= ShortHeightThreshold || Width >= WideLandscapeWidth);

    /// <summary>A supporting panel also fits beside the list and the chat.</summary>
    public bool CanShowThreePanes => CanShowTwoPanes && Width >= ThreePaneMinWidth;

    /// <summary>A phone held sideways: the conversation keeps most of the height.</summary>
    public bool IsShortHeight => PaneHeight < ShortHeightThreshold && Width < WideLandscapeWidth;

    /// <summary>
    /// This layout as it follows <paramref name="previous"/>. A window that only got shorter, as a
    /// browser page does above its keyboard, keeps the panes and chrome it had; they are decided
    /// again once the width or the fold changes, or the window grows back.
    /// </summary>
    public MobileLayoutState Following(MobileLayoutState previous) =>
        Width == previous.Width
        && Height < previous.PaneHeight
        && HingeSize == previous.HingeSize
        && HingePosition == previous.HingePosition
        && FoldPosition == previous.FoldPosition
        && HorizontalHingePosition == previous.HorizontalHingePosition
            ? this with { PaneHeight = previous.PaneHeight }
            : this;

    /// <summary>The docked list's width: the hinge or fold when there is one, otherwise a share of the window.</summary>
    public double ListPaneWidth => HingePosition > 0 ? HingePosition
        : FoldPosition > 0 ? FoldPosition
        : Math.Clamp(Math.Round(Width * 0.36), ListPaneMinWidth, ListPaneMaxWidth);

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
        hingeSize = double.IsFinite(hingeSize) && hingeSize > 0 ? hingeSize : 0;
        hingePosition = double.IsFinite(hingePosition) && hingePosition > 0 ? hingePosition : 0;

        var widthClass = ClassifyWidth(width);

        // An occluding book hinge needs a reserved pane; a drawable crease must not leave
        // half the canvas empty when navigation closes. Tabletop retains its upper-pane layout.
        var bookPanesFit = posture == FoldPosture.BookVerticalHinge
                           && hingePosition >= ListPaneMinWidth
                           && width - (hingePosition + hingeSize) >= MinimumDetailPaneWidth;
        var splitAtHinge = bookPanesFit && hingeSize > 0;
        var splitAtFold = bookPanesFit && hingeSize <= 0;
        var splitAtHorizontalHinge = posture == FoldPosture.TabletopHorizontalHinge
                                     && hingePosition >= 300
                                     && height - (hingePosition + Math.Max(hingeSize, 1)) >= 200;

        return new MobileLayoutState
        {
            Width = width,
            Height = height,
            PaneHeight = height,
            WidthClass = widthClass,
            // Only report the hinge when the panes actually meet there. Reporting it otherwise would
            // paint an empty stripe away from the physical fold while content sat under the real one.
            HingeSize = splitAtHinge ? hingeSize : 0,
            HingePosition = splitAtHinge ? hingePosition : 0,
            FoldPosition = splitAtFold ? hingePosition : 0,
            HorizontalHingeSize = splitAtHorizontalHinge ? Math.Max(hingeSize, 1) : 0,
            HorizontalHingePosition = splitAtHorizontalHinge ? hingePosition : 0
        };
    }
}
