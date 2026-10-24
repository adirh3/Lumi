using Lumi.Mobile.Layout;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Desktop;

/// <summary>
/// A device the simulator can emulate. Sizes are device-independent pixels — exactly what the
/// adaptive layout reasons about, so what renders here is what a real phone renders.
/// </summary>
public sealed record SimulatedDevice(
    string Name,
    double Width,
    double Height,
    FoldPosture Posture = FoldPosture.Flat,
    double HingeSize = 0,
    double HingePosition = 0)
{
    // Name remains the stable CLI selector; only the simulator's presentation is translated.
    public string DisplayName => Name switch
    {
        "Flip cover screen" => Strings.Get("SimFlipCover"),
        "Compact phone" => Strings.Get("SimCompactPhone"),
        "Fold — folded" => Strings.Get("SimFoldFolded"),
        "Galaxy Z Fold7 — cover" => Strings.Get("SimGalaxyCover"),
        "Galaxy Z Fold7 — cover landscape" => Strings.Get("SimGalaxyCoverLandscape"),
        "Phone landscape" => Strings.Get("SimPhoneLandscape"),
        "Galaxy Z Fold7 — open, large display size" => Strings.Get("SimGalaxyOpenLarge"),
        "Fold — unfolded" => Strings.Get("SimFoldUnfolded"),
        "Galaxy Z Fold7 — open" => Strings.Get("SimGalaxyOpen"),
        "Galaxy Z Fold7 — book" => Strings.Get("SimGalaxyBook"),
        "Galaxy Z Fold7 — tabletop" => Strings.Get("SimGalaxyTabletop"),
        "Fold — tabletop" => Strings.Get("SimFoldTabletop"),
        "Tablet" => Strings.Get("SimTablet"),
        "Tablet landscape" => Strings.Get("SimTabletLandscape"),
        "Large tablet landscape" => Strings.Get("SimLargeTabletLandscape"),
        _ => Name
    };

    public override string ToString() => $"{DisplayName}  ({Width:0}×{Height:0})";

    /// <summary>The catalog the simulator offers, ordered smallest surface first.</summary>
    public static IReadOnlyList<SimulatedDevice> All { get; } =
    [
        new("Flip cover screen", 360, 374),
        new("Compact phone", 360, 780),
        new("iPhone 15 Pro", 393, 852),
        new("Pixel 8 Pro", 412, 892),
        new("Fold — folded", 344, 882),
        // Galaxy Z Fold7 at its default display size: the cover is a 2.625x phone, the main screen 2x.
        new("Galaxy Z Fold7 — cover", 412, 960),
        new("Galaxy Z Fold7 — cover landscape", 960, 412),
        new("Phone landscape", 852, 393),
        new("Galaxy Z Fold7 — open, large display size", 750, 832),
        new("Fold — unfolded", 884, 908, FoldPosture.BookVerticalHinge, 24, 430),
        new("Galaxy Z Fold7 — open", 984, 1092),
        new("Galaxy Z Fold7 — book", 984, 1092, FoldPosture.BookVerticalHinge, 0, 492),
        new("Galaxy Z Fold7 — tabletop", 1092, 984, FoldPosture.TabletopHorizontalHinge, 0, 492),
        new("Fold — tabletop", 884, 908, FoldPosture.TabletopHorizontalHinge, 24, 454),
        new("Tablet", 834, 1112),
        new("Tablet landscape", 1112, 834),
        new("Large tablet landscape", 1280, 800)
    ];
}
