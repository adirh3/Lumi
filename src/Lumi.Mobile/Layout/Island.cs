using Avalonia;
using Avalonia.Controls;

namespace Lumi.Mobile.Layout;

/// <summary>
/// What is open (the chat, a place's page, a file), as the desktop shows it: a rounded island resting on
/// the canvas while something sits beside it, and the whole pane, in the island's colour, while it is
/// alone. <see cref="Frame"/> moves between the two (1 an island, 0 the pane), so the island can settle
/// into the screen on the same frames as the list beside it slides away.
/// </summary>
public class Island : ContentControl
{
    public static readonly StyledProperty<double> FrameProperty =
        AvaloniaProperty.Register<Island, double>(nameof(Frame), 1d);

    /// <summary>The curve of a whole island; the corners straighten with <see cref="Frame"/>.</summary>
    public static readonly StyledProperty<CornerRadius> IslandRadiusProperty =
        AvaloniaProperty.Register<Island, CornerRadius>(nameof(IslandRadius));

    // Its shadow falls on the canvas beyond its bounds; its content is clipped to the curve inside it.
    static Island() => ClipToBoundsProperty.OverrideDefaultValue<Island>(false);

    public double Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public CornerRadius IslandRadius
    {
        get => GetValue(IslandRadiusProperty);
        set => SetValue(IslandRadiusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != FrameProperty && change.Property != IslandRadiusProperty)
            return;

        var frame = Math.Clamp(Frame, 0, 1);
        var radius = IslandRadius;
        SetCurrentValue(CornerRadiusProperty, new CornerRadius(
            radius.TopLeft * frame, radius.TopRight * frame, radius.BottomRight * frame, radius.BottomLeft * frame));
        PseudoClasses.Set(":flat", frame <= 0);
    }
}
