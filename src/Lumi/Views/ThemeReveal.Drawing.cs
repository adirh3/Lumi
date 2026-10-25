using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace Lumi.Views;

public static partial class ThemeReveal
{
    // The edge is a soft band, a little wider as the circle grows: the old look fades out across it (DIPs).
    private const double MinFeather = 20;
    private const double MaxFeather = 72;
    // A slow frame (the first one after the theme changes is) slows the reveal down instead of skipping part of it.
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(1 / 20.0);
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly Lazy<SKRuntimeEffect?> LazyEffect = new(CreateEffect);

    /// <summary>How the new look spreads over the window, from the picture of its old look.</summary>
    internal sealed record Reveal(
        // What the picture stands on where the window was see-through; transparent for a picture taken from the screen.
        SKColor Background,
        // The window, in DIPs, and its physical pixels per DIP: the picture's scale.
        Size Size,
        double Scale,
        Matrix WindowToHost,
        Point Origin,
        double EndRadius,
        TimeSpan Duration,
        bool Dark,
        // The new look's accent, which faintly lights the edge.
        SKColor Tint,
        TaskCompletionSource Done);

    /// <summary>How far the edge has spread at progress <paramref name="q"/>: off at once, settling as it reaches the far corners.</summary>
    internal static double Reach(double q) => 1 - Math.Pow(1 - Math.Clamp(q, 0, 1), 2);

    /// <summary>How brightly the edge is lit at progress <paramref name="q"/>: lit at once, fading as it leaves.</summary>
    internal static double Glow(double q)
    {
        q = Math.Clamp(q, 0, 1);
        return Smooth(q / 0.08) * (1 - Smooth((q - 0.65) / 0.35));
    }

    /// <summary>How far the edge spreads from <paramref name="origin"/>: past the window's farthest corner, soft band and all.</summary>
    internal static double EndRadius(Size size, Point origin)
    {
        var x = Math.Max(origin.X, size.Width - origin.X);
        var y = Math.Max(origin.Y, size.Height - origin.Y);
        return Math.Sqrt(x * x + y * y) + MaxFeather;
    }

    /// <summary>A bigger window takes a little longer to fill.</summary>
    internal static TimeSpan DurationFor(double endRadius) => TimeSpan.FromSeconds(Math.Clamp(0.4 + endRadius / 3000, 0.5, 0.75));

    private static double Feather(double radius) => Math.Min(MinFeather + 0.05 * radius, MaxFeather);

    internal static SKRuntimeEffect? Effect => LazyEffect.Value;

    internal static SKRuntimeEffect? CreateEffect()
    {
        var effect = SKRuntimeEffect.CreateShader(Shader, out var errors);
        if (effect is null)
            Trace.TraceWarning($"[ThemeReveal] The reveal's shader didn't compile: {errors}");
        return effect;
    }

    /// <summary>
    /// Draws the reveal at progress <paramref name="q"/>, in the window's own space, over the new look:
    /// <paramref name="picture"/> of the old look where the edge hasn't reached. Without <paramref name="effects"/>
    /// (no GPU to draw them with), the edge is a plain circle.
    /// </summary>
    internal static void DrawFrame(SKCanvas canvas, Reveal reveal, SKImage picture, double q, bool effects)
    {
        var radius = (float)(reveal.EndRadius * Reach(q));
        var area = SKRect.Create((float)reveal.Size.Width, (float)reveal.Size.Height);
        var origin = new SKPoint((float)reveal.Origin.X, (float)reveal.Origin.Y);
        var toWindow = SKMatrix.CreateScale((float)(1 / reveal.Scale), (float)(1 / reveal.Scale));

        if (effects && Effect is { } effect)
        {
            // Not SKRuntimeShaderBuilder: disposing that disposes the effect it was given, which every frame shares.
            using var old = picture.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, toWindow);
            using var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["uCenter"] = origin,
                ["uRadius"] = radius,
                ["uSoft"] = (float)Feather(radius),
                // Light shows more on a dark look than on a light one, where it would only haze it.
                ["uGlow"] = (float)(Glow(q) * (reveal.Dark ? 0.1 : 0.07)),
                ["uBase"] = Premultiplied(reveal.Background),
                ["uTint"] = Premultiplied(reveal.Tint),
            };
            using var children = new SKRuntimeEffectChildren(effect) { ["uOld"] = old };
            using var shader = effect.ToShader(uniforms, children);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(area, paint);
            return;
        }

        using (new SKAutoCanvasRestore(canvas))
        {
            if (radius > 0)
            {
                using var circle = new SKPath();
                circle.AddCircle(origin.X, origin.Y, radius);
                canvas.ClipPath(circle, SKClipOperation.Difference, antialias: true);
            }
            if (reveal.Background.Alpha > 0)
            {
                using var background = new SKPaint { Color = reveal.Background };
                canvas.DrawRect(area, background);
            }
            canvas.DrawImage(picture, area, Sampling, null);
        }
    }

    private static SKColorF Premultiplied(SKColor color)
    {
        var alpha = color.Alpha / 255f;
        return new SKColorF(color.Red / 255f * alpha, color.Green / 255f * alpha, color.Blue / 255f * alpha, alpha);
    }

    private static SKMatrix ToSkia(Matrix matrix) => new(
        (float)matrix.M11, (float)matrix.M21, (float)matrix.M31,
        (float)matrix.M12, (float)matrix.M22, (float)matrix.M32,
        0, 0, 1);

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>
    /// Reads back what this frame drew beneath the host, all of the window, as a picture in physical pixels; null with
    /// <paramref name="partial"/> set when this frame drew only part of the window.
    /// </summary>
    private static SKImage? TakePicture(ISkiaSharpApiLease lease, Size size, out bool partial)
    {
        partial = false;
        if (lease.SkSurface is not { } surface)
            return null;
        var canvas = lease.SkCanvas;
        var window = SKRectI.Round(canvas.TotalMatrix.MapRect(SKRect.Create((float)size.Width, (float)size.Height)));
        if (!canvas.DeviceClipBounds.Contains(window))
        {
            partial = true;
            return null;
        }
        // A picture on the GPU is read back now, while this frame's context is current, so it can go from any
        // thread; one drawn in memory already is that, and reading it back would hand back itself.
        var drawn = surface.Snapshot(window);
        if (drawn is null || !drawn.IsTextureBacked)
            return drawn;
        using (drawn)
            return drawn.ToRasterImage(ensurePixelData: true);
    }

    /// <summary>Asks the reveal's next frame to cover the window with the picture of its old look, or to take one.</summary>
    private sealed record CoverRequest(Reveal Reveal, SKImage? Picture, TaskCompletionSource<bool> Covered);

    /// <summary>
    /// Draws the reveal on the render thread: the cover, from the first frame after it is asked for, and then,
    /// once started, the reveal one frame at a time, until it is over or let go.
    /// </summary>
    private sealed class RevealHandler : CompositionCustomVisualHandler
    {
        public static readonly object Start = new();
        public static readonly object Release = new();
        // A picture needs a frame that draws the whole window; the first one after the request should.
        private const int PictureTries = 4;
        private Reveal? _reveal;
        private SKImage? _picture;
        private TaskCompletionSource<bool>? _covered;
        private int _tries;
        private bool _started;
        private TimeSpan _shown;
        private long _lastFrame;

        public override void OnMessage(object message)
        {
            if (message is CoverRequest request)
            {
                End();
                _reveal = request.Reveal;
                _picture = request.Picture;
                _covered = request.Covered;
                Invalidate();
            }
            else if (ReferenceEquals(message, Start) && _reveal is not null && _covered is null)
            {
                _started = true;
                RegisterForNextAnimationFrameUpdate();
                Invalidate();
            }
            else if (ReferenceEquals(message, Release))
            {
                End();
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            if (!_started || _reveal is not { } reveal)
                return;
            var now = Stopwatch.GetTimestamp();
            if (_lastFrame != 0)
            {
                var step = Stopwatch.GetElapsedTime(_lastFrame, now);
                _shown += step < MaxStep ? step : MaxStep;
            }
            _lastFrame = now;
            if (_shown >= reveal.Duration)
            {
                End();
                return;
            }
            Invalidate();
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_reveal is not { } reveal)
                return;
            try
            {
                if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                {
                    End();
                    return;
                }
                using var lease = feature.Lease();
                if (_picture is null)
                {
                    _picture = TakePicture(lease, reveal.Size, out var partial);
                    if (_picture is null)
                    {
                        if (partial && ++_tries < PictureTries)
                            Invalidate();
                        else
                            End();
                        return;
                    }
                }

                var canvas = lease.SkCanvas;
                var saved = canvas.Save();
                try
                {
                    var windowToHost = ToSkia(reveal.WindowToHost);
                    canvas.Concat(in windowToHost);
                    var q = _started ? _shown / reveal.Duration : 0;
                    DrawFrame(canvas, reveal, _picture, q, effects: lease.GrContext is not null);
                }
                finally
                {
                    canvas.RestoreToCount(saved);
                }
                // The cover is drawn: the theme can change beneath it.
                _covered?.TrySetResult(true);
                _covered = null;
            }
            catch (Exception ex)
            {
                // The reveal is decoration: the new look simply shows.
                Trace.TraceWarning($"[ThemeReveal] The reveal stopped: {ex.Message}");
                End();
            }
        }

        public override Rect GetRenderBounds() => _reveal is { } reveal ? new Rect(reveal.Size) : default;

        private void End()
        {
            if (_reveal is not { } reveal)
                return;
            _reveal = null;
            _started = false;
            _shown = TimeSpan.Zero;
            _lastFrame = 0;
            _tries = 0;
            _covered?.TrySetResult(false);
            _covered = null;
            _picture?.Dispose();
            _picture = null;
            reveal.Done.TrySetResult();
            Invalidate();
        }
    }

    // The old look fades out across a soft band just inside the edge, where the accent faintly lights both looks;
    // the light fades as the edge leaves the window. Positions are in DIPs.
    private const string Shader = """
        uniform shader uOld;
        uniform float2 uCenter;
        uniform float uRadius;
        uniform float uSoft;
        uniform float uGlow;
        uniform float4 uBase;
        uniform float4 uTint;

        half4 main(float2 p) {
            float d = length(p - uCenter);
            // No circle at all before it has grown.
            float old = uRadius > 0.0 ? smoothstep(uRadius - uSoft, uRadius, d) : 1.0;
            float4 c = uOld.eval(p);
            float4 colour = (c + uBase * (1.0 - c.a)) * old;
            float x = (d - (uRadius - 0.5 * uSoft)) / (0.7 * uSoft);
            return half4(mix(colour, float4(uTint.rgb, 1.0), exp(-x * x) * uGlow));
        }
        """;
}
