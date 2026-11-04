using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

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

    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        Border.BoxShadowProperty.AddOwner<Island>();

    // Its shadow falls on the canvas beyond its bounds; its content is clipped to the curve inside it.
    static Island()
    {
        ClipToBoundsProperty.OverrideDefaultValue<Island>(false);
        AffectsRender<Island>(BoxShadowProperty, CornerRadiusProperty);
    }

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

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || BoxShadow == default)
            return;

        var rect = new Rect(Bounds.Size);
        if (BoxShadow.HasInsetShadows)
        {
            context.DrawRectangle(null, null, new RoundedRect(rect, CornerRadius), BoxShadow);
            return;
        }

        // A mask blur lets Skia reuse its rounded-shadow mask instead of filtering a pane-sized image.
        context.Custom(new ShadowOperation(rect, CornerRadius, BoxShadow));
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

    private sealed class ShadowOperation : ICustomDrawOperation
    {
        private readonly Rect _rect;
        private readonly CornerRadius _radius;
        private readonly BoxShadows _shadows;
        private readonly SKMaskFilter?[] _filters;
        private bool _reportedUnsupportedRenderer;

        public ShadowOperation(Rect rect, CornerRadius radius, BoxShadows shadows)
        {
            _rect = rect;
            _radius = radius;
            _shadows = shadows;
            Bounds = shadows.TransformBounds(rect);
            _filters = new SKMaskFilter?[shadows.Count];
            for (var index = 0; index < shadows.Count; index++)
            {
                var blur = shadows[index].Blur;
                if (blur > 0)
                {
                    // The same radius-to-sigma conversion as Avalonia's BoxShadow renderer.
                    var sigma = 0.288675f * (float)blur + 0.5f;
                    _filters[index] = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma, respectCTM: true);
                }
            }
        }

        public Rect Bounds { get; }

        public bool HitTest(Point point) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is ShadowOperation shadow
            && _rect == shadow._rect && _radius == shadow._radius && _shadows == shadow._shadows;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                if (!_reportedUnsupportedRenderer)
                {
                    _reportedUnsupportedRenderer = true;
                    Trace.TraceWarning("[Island] The island shadow requires the Skia renderer.");
                }
                return;
            }

            using var lease = feature.Lease();
            if (lease.CurrentOpacity <= 0)
                return;

            var canvas = lease.SkCanvas;
            using var outline = new SKRoundRect();
            outline.SetRectRadii(
                new SKRect((float)_rect.Left, (float)_rect.Top, (float)_rect.Right, (float)_rect.Bottom),
                [
                    new SKPoint((float)_radius.TopLeft, (float)_radius.TopLeft),
                    new SKPoint((float)_radius.TopRight, (float)_radius.TopRight),
                    new SKPoint((float)_radius.BottomRight, (float)_radius.BottomRight),
                    new SKPoint((float)_radius.BottomLeft, (float)_radius.BottomLeft)
                ]);
            for (var index = 0; index < _shadows.Count; index++)
            {
                var shadow = _shadows[index];
                if (shadow == default)
                    continue;

                using var shape = new SKRoundRect();
                shape.SetRectRadii(outline.Rect, outline.Radii);
                shape.Inflate((float)shadow.Spread, (float)shadow.Spread);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B,
                        (byte)(shadow.Color.A * lease.CurrentOpacity)),
                    MaskFilter = _filters[index]
                };
                canvas.Save();
                canvas.ClipRoundRect(outline, SKClipOperation.Difference, true);
                canvas.Translate((float)shadow.OffsetX, (float)shadow.OffsetY);
                canvas.DrawRoundRect(shape, paint);
                canvas.Restore();
            }
        }

        public void Dispose()
        {
            foreach (var filter in _filters)
                filter?.Dispose();
        }
    }
}
