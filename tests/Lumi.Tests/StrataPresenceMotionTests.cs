using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Skia-enabled headless app builder so <see cref="WindowBase.CaptureRenderedFrame"/> returns REAL
/// composited pixels (the default headless drawing is a no-op that never applies composition-thread
/// <c>Offset</c> transforms). <see cref="HeadlessUnitTestSession.StartNew(Type, AvaloniaTestIsolationLevel)"/>
/// discovers this <c>BuildAvaloniaApp</c> by convention; the rest of the suite keeps using the
/// drawing-free <see cref="HeadlessTestApp"/>.
/// </summary>
public sealed class SkiaHeadlessTestApp : Lumi.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Tests create their own windows.
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SkiaHeadlessTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The durable form of the "reliable, display-free way to assess the presence motion" — the thing the
/// focus-travel work hinged on. MCP/OS screenshots cannot freeze the glide because it is a
/// composition-thread <c>Offset</c> transform that <see cref="RenderTargetBitmap"/> does not apply, so
/// the only objective assessor is a Skia headless window whose <see cref="WindowBase.CaptureRenderedFrame"/>
/// DOES apply composition transforms. We drive a real <see cref="StrataPresence"/> focus move and assert
/// the rendered glow centroid <i>ramps</i> through intermediate positions instead of teleporting in a
/// single frame — guarding against a regression to the old base-offset snap.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataPresenceMotionTests
{
    /// <summary>
    /// Classifies a centroid sample series (captured after a single focus change, starting from
    /// <paramref name="origin"/>) as smooth motion vs. an instant teleport. A teleport completes the whole
    /// journey inside the first captured frame (first step ≈ total travel, no intermediates); a ramp eases
    /// through the destination over several frames. Wall-clock-agnostic on purpose: it reasons about the
    /// SHAPE of the trajectory, not absolute timing (each headless capture can take ~100&#160;ms of real
    /// time for a heavy scene, so the compositor clock advances unevenly between samples).
    /// </summary>
    internal static MotionKind ClassifyMotion(IReadOnlyList<double> samples, double origin)
    {
        if (samples.Count < 2)
            return MotionKind.NoMove;

        var last = samples[^1];
        var total = last - origin;
        if (Math.Abs(total) < MinTravelPx)
            return MotionKind.NoMove;

        var firstStep = samples[0] - origin;
        var firstRatio = firstStep / total; // how much of the journey the very first frame covered

        // Count the distinct intermediate "bands" the centroid passed through, strictly between a 12%
        // and 88% slice of the journey. A real glide visits several; a teleport visits none.
        var lo = origin + total * 0.12;
        var hi = origin + total * 0.88;
        var bands = new HashSet<int>();
        foreach (var s in samples)
        {
            var inside = total > 0 ? (s > lo && s < hi) : (s < lo && s > hi);
            if (inside)
                bands.Add((int)Math.Round(s / BandPx));
        }

        // Teleport: the first frame already (essentially) arrived AND nothing meaningful sat in between.
        if (firstRatio >= TeleportFirstStepRatio && bands.Count < MinIntermediateBands)
            return MotionKind.Teleport;

        if (bands.Count >= MinIntermediateBands && firstRatio < TeleportFirstStepRatio)
            return MotionKind.Ramp;

        // Ambiguous (e.g. coarse sampling): treat a low first step as a ramp, otherwise teleport.
        return firstRatio < TeleportFirstStepRatio ? MotionKind.Ramp : MotionKind.Teleport;
    }

    internal enum MotionKind
    {
        NoMove,
        Ramp,
        Teleport,
    }

    private const double MinTravelPx = 40.0;
    private const double BandPx = 10.0;
    private const int MinIntermediateBands = 3;
    private const double TeleportFirstStepRatio = 0.82;

    // ── Deterministic classifier unit tests (always run; encode the assessment method) ─────────────

    [Fact]
    public void Classifier_FlagsAnInstantTeleport()
    {
        // Origin 100; first captured frame is already at the destination 300 and stays there.
        var teleport = new List<double> { 300, 300, 301, 300, 300, 299, 300 };
        Assert.Equal(MotionKind.Teleport, ClassifyMotion(teleport, origin: 100));
    }

    [Fact]
    public void Classifier_AcceptsASmoothCubicRamp()
    {
        // A decelerating ease from 100 -> 300 that visits many intermediate positions.
        var origin = 100.0;
        var ramp = new List<double>();
        for (int i = 1; i <= 12; i++)
        {
            var t = i / 12.0;
            var eased = 1 - Math.Pow(1 - t, 3); // cubic ease-out
            ramp.Add(origin + 200 * eased);
        }
        Assert.Equal(MotionKind.Ramp, ClassifyMotion(ramp, origin));
    }

    [Fact]
    public void Classifier_AcceptsACoarselySampledRamp()
    {
        // Even with only a few samples landing mid-journey, an early sample well short of the
        // destination must NOT be read as a teleport.
        var coarse = new List<double> { 165, 235, 285, 300, 300 };
        Assert.Equal(MotionKind.Ramp, ClassifyMotion(coarse, origin: 100));
    }

    [Fact]
    public void Classifier_IgnoresNoise()
    {
        var still = new List<double> { 100, 101, 99, 100, 100 };
        Assert.Equal(MotionKind.NoMove, ClassifyMotion(still, origin: 100));
    }

    // ── Integration assessment of the REAL control (Skia headless real-pixel capture) ──────────────

    [SkippableFact]
    public void FocusTravel_RampsSmoothly_DoesNotTeleport()
    {
        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        var samples = new List<double>();
        double origin = -1;
        var rendered = false;

        try
        {
            session!.Dispatch(() =>
            {
                // Provide the colour tokens the lobes resolve via GetResourceObservable, in case the app
                // theme doesn't surface every key, so the glow renders with measurable opacity.
                var res = Application.Current!.Resources;
                res["Color.AccentDefault"] = Color.FromRgb(120, 110, 245);
                res["Color.AccentViolet"] = Color.FromRgb(160, 100, 230);
                res["Color.AccentRose"] = Color.FromRgb(230, 110, 170);
                res["Palette.Warning400"] = Color.FromRgb(235, 175, 90);
                res["Palette.Success400"] = Color.FromRgb(90, 210, 140);
                res["Palette.Accent400"] = Color.FromRgb(110, 160, 240);
                res["Palette.Danger400"] = Color.FromRgb(235, 90, 90);

                var presence = new StrataPresence
                {
                    State = PresenceState.Streaming, // brightest ambient => strong centroid signal
                    Intensity = 4.0,                 // max luminance => opaque-ish lobes
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.5, 0.30),
                };

                var window = new Window
                {
                    Width = 460,
                    Height = 460,
                    Background = Brushes.White, // glow is darker than white => "dark" centroid tracks it
                    Content = presence,
                };
                window.Show();

                // Flush attach + InitComposition (posted at Loaded) + initial layout/focus snap.
                for (int i = 0; i < 5; i++)
                    Tick(40);

                origin = Centroid(window.CaptureRenderedFrame());
                rendered = origin >= 0;
                if (!rendered)
                    return;

                // The move under test: a large downward focus travel (0.30 -> 0.92).
                presence.FocusPoint = new Point(0.5, 0.92);
                Tick(0);

                // Sample the glide. Small real-time steps keep the compositor clock fine-grained enough
                // to catch the ramp's middle despite per-capture overhead.
                for (int i = 0; i < 16; i++)
                {
                    Tick(24);
                    samples.Add(Centroid(window.CaptureRenderedFrame()));
                }

                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            session?.Dispose();
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");

        var total = samples[^1] - origin;
        var kind = ClassifyMotion(samples, origin);

        var detail = $"origin={origin:F0} total={total:F0} samples=[{string.Join(", ", samples.Select(s => s.ToString("F0")))}]";

        Assert.True(Math.Abs(total) >= MinTravelPx, $"Focus move did not travel measurably. {detail}");
        Assert.False(kind == MotionKind.Teleport, $"Focus travel TELEPORTED instead of gliding. {detail}");
        Assert.Equal(MotionKind.Ramp, kind);
    }

    /// <summary>
    /// Permanent guard against the regression that made the field feel motionless: when the lobes were
    /// sized to the surface's LONG edge, a wide window produced lobes LARGER than the viewport, so
    /// translating the field toward a focal point was imperceptible (a blob bigger than the screen barely
    /// appears to move). The field must stay contained within the SHORT edge regardless of how wide the
    /// surface is, so its travel reads. Asserts on a deliberately wide (2:1) surface.
    /// </summary>
    [SkippableFact]
    public void Lobes_StayContainedWithinShortEdge_OnAWideSurface()
    {
        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        double maxDiameter = -1, shortEdge = 0, longEdge = 0;
        try
        {
            session!.Dispatch(() =>
            {
                var presence = new StrataPresence { State = PresenceState.Streaming };
                var window = new Window { Width = 1600, Height = 760, Content = presence };
                window.Show();
                for (int i = 0; i < 5; i++)
                    Tick(40); // attach + InitComposition (lays out the lobes) 

                shortEdge = Math.Min(presence.Bounds.Width, presence.Bounds.Height);
                longEdge = Math.Max(presence.Bounds.Width, presence.Bounds.Height);
                maxDiameter = presence.DebugMaxLobeDiameter();
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            session?.Dispose();
        }

        Skip.If(maxDiameter < 0, "Presence did not lay out in the headless session.");

        Assert.True(longEdge >= shortEdge * 1.9, $"Test surface was not wide enough ({longEdge}x{shortEdge}).");
        // The decisive check: the largest lobe must fit within the SHORT edge — i.e. sizing tracks the
        // short edge, not the long edge. The old bug yielded a diameter > the short edge (and > viewport).
        Assert.True(
            maxDiameter <= shortEdge,
            $"Largest lobe ({maxDiameter:F0}px) exceeds the short edge ({shortEdge:F0}px) — the field is " +
            $"sized to the long edge again and its travel will be imperceptible.");
    }

    private static void Tick(int realMs)
    {
        if (realMs > 0)
            Thread.Sleep(realMs);
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        catch
        {
            // Render timer not available on this platform variant; the dispatcher pump still advances.
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Vertical centroid of the rendered glow: a darkness-weighted average row (the coloured
    /// lobes are darker than the white window background), so it tracks where the field's light pools.</summary>
    private static double Centroid(WriteableBitmap? bmp)
    {
        if (bmp is null)
            return -1;

        using var fb = bmp.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
        double sy = 0, sw = 0;
        unsafe
        {
            byte* p = (byte*)fb.Address;
            for (int y = 0; y < h; y++)
            {
                byte* row = p + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + x * 4;
                    double dark = 255.0 - (px[0] + px[1] + px[2]) / 3.0;
                    if (dark > 28)
                    {
                        sy += y * dark;
                        sw += dark;
                    }
                }
            }
        }

        return sw > 0 ? sy / sw : -1;
    }
}
