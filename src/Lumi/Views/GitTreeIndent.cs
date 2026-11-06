using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Lumi.Views;

/// <summary>
/// Leading space of a Git changes tree row: one step per level, with a hairline guide under each
/// ancestor's chevron so long folders still read as belonging to their parent.
/// </summary>
public sealed class GitTreeIndent : Control
{
    public const double Step = 14;

    public static readonly StyledProperty<int> DepthProperty =
        AvaloniaProperty.Register<GitTreeIndent, int>(nameof(Depth));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<GitTreeIndent, IBrush?>(nameof(Stroke));

    static GitTreeIndent()
    {
        AffectsMeasure<GitTreeIndent>(DepthProperty);
        AffectsRender<GitTreeIndent>(StrokeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<GitTreeIndent>(false);
    }

    public int Depth
    {
        get => GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Depth * Step, 0);

    public override void Render(DrawingContext context)
    {
        if (Depth <= 0 || Stroke is not { } stroke)
            return;

        var height = Bounds.Height;
        for (var level = 0; level < Depth; level++)
        {
            var x = level * Step + Step / 2;
            context.FillRectangle(stroke, new Rect(x, 0, 1, height));
        }
    }
}
