using System;
using System.Globalization;
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
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// Objective shape/placement assessor for the resting (Idle) aura, using the same Skia headless
/// real-pixel capture the motion tests rely on (composition <c>Offset</c>/<c>Scale</c> ARE applied,
/// unlike MCP/OS screenshots). Measures WHERE the field pools and HOW WIDE vs tall it sits when aimed
/// at the composer — the two qualities behind "the idle aura should spread along the composer".
/// </summary>
[Collection("Headless UI")]
public sealed class PresenceAuraShapeTests
{
    private readonly ITestOutputHelper _out;

    public PresenceAuraShapeTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public void IdleAura_PoolsLowAndSpreadsHorizontally()
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

        Box full = default, core = default;
        var rendered = false;
        int width = 0, height = 0;

        try
        {
            session!.Dispatch(() =>
            {
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
                    State = PresenceState.Idle, // existing-chat resting state (Calm profile => WidthBias)
                    Intensity = 3.0,            // lift the (dim) idle ambient so the centroid has signal
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.5, 0.86), // the composer anchor the controller uses when idle
                };

                // A realistic chat-canvas aspect (taller-than-square, like the live 1347x931 workspace).
                var window = new Window
                {
                    Width = 1200,
                    Height = 840,
                    Background = Brushes.White,
                    Content = presence,
                };
                window.Show();

                for (int i = 0; i < 8; i++)
                    Tick(40); // attach + InitComposition + initial focus snap

                // Let the spring settle at the composer anchor.
                for (int i = 0; i < 40; i++)
                    Tick(24);

                var frame = window.CaptureRenderedFrame();
                rendered = frame is not null;
                if (!rendered)
                    return;

                width = frame!.PixelSize.Width;
                height = frame.PixelSize.Height;
                full = Measure(frame, darkThreshold: 24);
                core = Measure(frame, darkThreshold: 70);
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");
        Skip.If(full.Weight <= 0, "No aura pixels measured.");

        string Pct(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture);
        _out.WriteLine($"canvas={width}x{height}");
        _out.WriteLine($"FULL  centroid=({Pct(full.Cx / width)}%, {Pct(full.Cy / height)}%) " +
                       $"bbox W={Pct(full.W / width)}% H={Pct(full.H / height)}% aspect={(full.W / full.H):F2}");
        _out.WriteLine($"CORE  centroid=({Pct(core.Cx / width)}%, {Pct(core.Cy / height)}%) " +
                       $"bbox W={Pct(core.W / width)}% H={Pct(core.H / height)}% aspect={(core.W / core.H):F2}");

        // Assertions encode Adir's two asks for the idle aura:
        // 1) it pools LOW (toward the composer) rather than mid-canvas, and
        // 2) it reads WIDER than tall (light spreading ALONG the composer).
        Assert.True(core.Cy / height >= 0.55,
            $"Idle aura core pools too high ({Pct(core.Cy / height)}% of canvas) — it should settle low toward the composer.");
        Assert.True(full.W / full.H >= 1.6,
            $"Idle aura is not horizontally spread (aspect {(full.W / full.H):F2}) — it should read as a broad wash wider than tall.");
    }

    [SkippableFact]
    public void EmitLean_IsGentle_AndSettlesWithoutBouncingBack()
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

        var samples = new System.Collections.Generic.List<(double Ms, double Cx)>();
        var rendered = false;
        int width = 0;

        try
        {
            session!.Dispatch(() =>
            {
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
                    State = PresenceState.Idle,
                    Intensity = 3.0,
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.5, 0.74),
                };
                var window = new Window { Width = 1200, Height = 840, Background = Brushes.White, Content = presence };
                window.Show();

                for (int i = 0; i < 8; i++)
                    Tick(40);
                for (int i = 0; i < 24; i++)
                    Tick(24); // settle at rest

                var rest = Measure(window.CaptureRenderedFrame(), 24);
                width = rest.Cx > 0 ? 1200 : 0;
                rendered = rest.Weight > 0;
                if (!rendered)
                    return;
                samples.Add((0, rest.Cx));

                presence.Emit(PresenceEdge.Right); // the island-open lean

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 22; i++) // ~1.4s of the 1.28s Emit, sampled by the (slow) capture itself
                {
                    Tick(8);
                    var b = Measure(window.CaptureRenderedFrame(), 24);
                    if (b.Weight > 0)
                        samples.Add((sw.Elapsed.TotalMilliseconds, b.Cx));
                }
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");
        Skip.If(samples.Count < 6, "Too few Emit samples captured.");

        double rest0 = samples[0].Cx;
        double peak = double.MinValue;
        int peakIdx = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Cx > peak)
            {
                peak = samples[i].Cx;
                peakIdx = i;
            }
        }
        double peakShift = peak - rest0;
        // Lowest position reached AFTER the peak — a bounce would swing left, past rest.
        double postPeakMin = double.MaxValue;
        for (int i = peakIdx; i < samples.Count; i++)
            postPeakMin = Math.Min(postPeakMin, samples[i].Cx);
        double overshoot = rest0 - postPeakMin; // >0 means it bounced left of rest
        double endShift = samples[^1].Cx - rest0;

        foreach (var (ms, cx) in samples)
            _out.WriteLine($"t={ms,7:F0}ms  Cx={cx:F1}  d={cx - rest0,7:F1}");
        _out.WriteLine($"rest={rest0:F1}  peakShift={peakShift:F1}px  overshoot(left of rest)={overshoot:F1}px  endShift={endShift:F1}px");

        // (1) The field DID lean toward the seam (the gesture is visible).
        Assert.True(peakShift > 3, $"Emit produced no visible lean (peakShift {peakShift:F1}px).");
        // (2) It SETTLES back home — does not bounce past rest to the opposite side. This is the polish fix:
        //     a decaying lean+settle, not an out-and-back swing that overshoots.
        //     The floor is a pixel-centroid SAMPLING-NOISE budget, not a behaviour tolerance: this test
        //     pixel-samples a compositor keyframe animation (Emit animates _selfVisual.Offset, whose live
        //     value is not readable from any property), and the centroid of a soft blurred glow jitters by
        //     ~10px frame-to-frame — even a known-good settle measures overshoot ~9px. peakShift is likewise
        //     under-sampled under machine load (the slow CaptureRenderedFrame advances the wall-clock-driven
        //     animation by a load-dependent amount per sample), which collapses the proportional term. A real
        //     bounce regression (the old 0.20·span out-and-back swing) overshoots by tens of px — far above
        //     this floor — so a noise-safe floor keeps the regression-catching power without flaking on load.
        Assert.True(overshoot <= Math.Max(16.0, 0.20 * peakShift),
            $"Emit bounced back past rest by {overshoot:F1}px (peakShift {peakShift:F1}px) — it should settle, not swing.");
        // (3) By the end it has returned close to rest (no residual displacement) — same centroid-noise floor.
        Assert.True(Math.Abs(endShift) <= Math.Max(16.0, 0.30 * peakShift),
            $"Emit did not settle back to rest (endShift {endShift:F1}px).");
    }

    [SkippableFact]
    public void Resize_RePlacesFieldToTrackNormalizedFocus()
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

        var ran = false;
        double narrowNorm = 0, wideNorm = 0, narrowTx = 0, wideTx = 0;

        try
        {
            session!.Dispatch(() =>
            {
                var res = Application.Current!.Resources;
                res["Color.AccentDefault"] = Color.FromRgb(120, 110, 245);
                res["Color.AccentViolet"] = Color.FromRgb(160, 100, 230);
                res["Color.AccentRose"] = Color.FromRgb(230, 110, 170);
                res["Palette.Warning400"] = Color.FromRgb(235, 175, 90);
                res["Palette.Success400"] = Color.FromRgb(90, 210, 140);
                res["Palette.Accent400"] = Color.FromRgb(110, 160, 240);
                res["Palette.Danger400"] = Color.FromRgb(235, 90, 90);

                // Deliberately OFF-CENTRE focus X: a centred focus stays at ~0.5 under any width, so an
                // off-centre X is what makes this sensitive to "did the field actually re-place for the new
                // width?". The Core lobe IS the field's bright centre, so reading its travel-from-home (via
                // the same debug hook the motion tests use) measures placement directly — no pixel-blend
                // ambiguity from the differently-anchored ambient lobes.
                var presence = new StrataPresence
                {
                    State = PresenceState.Idle,
                    Intensity = 3.0,
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.32, 0.80),
                };
                var window = new Window { Width = 900, Height = 700, Background = Brushes.White, Content = presence };
                window.Show();

                for (int i = 0; i < 8; i++)
                    Tick(40);
                SettleCoreX(presence, 900, out narrowTx);
                narrowNorm = 0.5 + narrowTx / 900.0;

                // Widen the window WITHOUT touching FocusPoint (a maximize): the same normalized focus must
                // now resolve to a NEW absolute placement that holds the SAME normalized position.
                window.Width = 1400;
                SettleCoreX(presence, 1400, out wideTx);
                wideNorm = 0.5 + wideTx / 1400.0;

                window.Close();
                ran = true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Skip.IfNot(ran, "Headless run did not complete.");

        _out.WriteLine($"narrow: coreTravelX={narrowTx:F1}/900  norm={narrowNorm:F3}");
        _out.WriteLine($"wide:   coreTravelX={wideTx:F1}/1400 norm={wideNorm:F3}");
        _out.WriteLine($"absTravelShift={wideTx - narrowTx:F1}px  normDrift={Math.Abs(wideNorm - narrowNorm):F3}");

        // (1) The field PHYSICALLY re-placed: the Core's travel-from-centre grew with the window (holding a
        //     fixed normalized offset means a larger pixel offset on a wider field) — a field that ignored
        //     resize would keep ~the same pixel travel.
        Assert.True(Math.Abs(wideTx) - Math.Abs(narrowTx) > 30,
            $"Field did not re-scale its placement on resize (|travel| {Math.Abs(narrowTx):F0} -> {Math.Abs(wideTx):F0}px).");
        // (2) ...holding the SAME normalized focus position (it tracked the focus, not drifted toward centre).
        Assert.True(Math.Abs(wideNorm - narrowNorm) <= 0.03,
            $"Field's normalized position drifted on resize ({narrowNorm:F3} -> {wideNorm:F3}) — it should hold the focus.");
    }

    [SkippableFact]
    public void Resize_BurstHoldsFocus_NeverCollapsesToCentre()
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

        var ran = false;
        double baseNorm = 0, worstDrift = 0, worstWidth = 0, worstNorm = 0;

        try
        {
            session!.Dispatch(() =>
            {
                var res = Application.Current!.Resources;
                res["Color.AccentDefault"] = Color.FromRgb(120, 110, 245);
                res["Color.AccentViolet"] = Color.FromRgb(160, 100, 230);
                res["Color.AccentRose"] = Color.FromRgb(230, 110, 170);
                res["Palette.Warning400"] = Color.FromRgb(235, 175, 90);
                res["Palette.Success400"] = Color.FromRgb(90, 210, 140);
                res["Palette.Accent400"] = Color.FromRgb(110, 160, 240);
                res["Palette.Danger400"] = Color.FromRgb(235, 90, 90);

                // Off-centre focus (held fixed): sensitive to any drift toward the centred home.
                var presence = new StrataPresence
                {
                    State = PresenceState.Idle,
                    Intensity = 3.0,
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.32, 0.80),
                };
                var window = new Window { Width = 900, Height = 700, Background = Brushes.White, Content = presence };
                window.Show();
                for (int i = 0; i < 8; i++)
                    Tick(40);

                // The field's OWN held normalized position at the start width (damped by the Core follow,
                // so ~0.34, not the raw 0.32) — the burst must hold THIS, whatever it is.
                SettleCoreX(presence, 900, out var baseTx);
                baseNorm = 0.5 + baseTx / 900.0;

                // Simulate a CONTINUOUS drag-resize: many incremental width changes back-to-back — the exact
                // burst that previously left the field jittering or snapped to centre (its deferred spring
                // completion landing on the resize-clobbered centred base). After EACH step the field must
                // still hold its off-centre focus — never collapse toward 0.5.
                for (double w = 950; w <= 1600; w += 50)
                {
                    window.Width = w;
                    SettleCoreX(presence, w, out var tx);
                    var norm = 0.5 + tx / w;
                    var drift = Math.Abs(norm - baseNorm);
                    if (drift > worstDrift)
                    {
                        worstDrift = drift;
                        worstWidth = w;
                        worstNorm = norm;
                    }
                }

                window.Close();
                ran = true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Skip.IfNot(ran, "Headless run did not complete.");

        _out.WriteLine($"baseNorm={baseNorm:F3}  worst over burst: width={worstWidth:F0} norm={worstNorm:F3} drift={worstDrift:F3}");

        // Across the WHOLE drag the field holds its off-centre focus at every step — in particular it never
        // collapses to centre (which from an off-centre baseline reads as a large drift). Snap re-pinning each
        // step is what holds it; the old spring path could revert to the resize-clobbered centred base.
        Assert.True(worstDrift <= 0.04,
            $"Field drifted from focus during a resize burst (worst norm {worstNorm:F3} at width {worstWidth:F0}, drift {worstDrift:F3} from baseline {baseNorm:F3}).");
    }

    [SkippableFact]
    public void Resize_HoldsFocus_WithoutBackgroundDrain_StructuralArrange()
    {
        // The two resize tests above settle with Tick() -> RunJobs(), which drains EVERY dispatcher
        // priority INCLUDING Background. That masks the real live bug: during a continuous OS resize-drag
        // the event flood STARVES Background, so a Background-deferred focus re-pin never runs and the
        // field collapses to centre / jitters. This test settles with a RENDER-ONLY drain (Render and
        // above, never Background), so it passes ONLY if placement is structural — i.e. ArrangeOverride
        // arranges each lobe host AT its focal target, so the layout-pass Offset sync writes the focal
        // Offset with no Background help. With the old Background-deferred snap this would collapse to
        // centre (norm ~0.5); with arrange-at-focal it holds the off-centre focus.
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

        var ran = false;
        double baseNorm = 0, wideNorm = 0;

        try
        {
            session!.Dispatch(() =>
            {
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
                    State = PresenceState.Idle,
                    Intensity = 3.0,
                    FocusReach = 1.0,
                    FocusPoint = new Point(0.30, 0.80),
                };
                var window = new Window { Width = 900, Height = 700, Background = Brushes.White, Content = presence };
                window.Show();
                for (int i = 0; i < 8; i++)
                    Tick(40); // full drain for the INITIAL placement is fine (no drag in progress yet)

                baseNorm = 0.5 + presence.DebugFieldCenterOffset().X / 900.0;

                // Now resize and settle with a RENDER-ONLY drain — Background is intentionally never run,
                // emulating the starvation of a real continuous drag.
                window.Width = 1500;
                for (int i = 0; i < 30; i++)
                    TickRenderOnly(16);

                wideNorm = 0.5 + presence.DebugFieldCenterOffset().X / 1500.0;

                window.Close();
                ran = true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Skip.IfNot(ran, "Headless run did not complete.");

        _out.WriteLine($"baseNorm={baseNorm:F3}  wideNorm(render-only)={wideNorm:F3}");

        // (1) It did NOT collapse to centre under a Background-starved resize.
        Assert.True(Math.Abs(wideNorm - 0.5) > 0.10,
            $"Field collapsed toward centre on a Background-starved resize (norm {wideNorm:F3}) — placement is not structural.");
        // (2) It held the SAME off-centre normalized focus it had before the resize.
        Assert.True(Math.Abs(wideNorm - baseNorm) <= 0.03,
            $"Field's normalized position drifted on a Background-starved resize ({baseNorm:F3} -> {wideNorm:F3}).");
    }

    /// <summary>Ticks until the Core lobe's horizontal travel-from-home stabilizes (the resize/placement
    /// spring has settled), then returns it. Polls so the test is robust to spring duration rather than a
    /// fixed, guessed tick budget.</summary>
    private static void SettleCoreX(StrataPresence presence, double expectWidth, out double travelX)
    {
        travelX = presence.DebugFieldCenterOffset().X;
        var stable = 0;
        for (int i = 0; i < 160 && stable < 6; i++)
        {
            Tick(16);
            var tx = presence.DebugFieldCenterOffset().X;
            stable = Math.Abs(tx - travelX) < 0.5 ? stable + 1 : 0;
            travelX = tx;
        }
    }

    private readonly record struct Box(double Cx, double Cy, double W, double H, double Weight);

    /// <summary>Darkness-weighted centroid + bounding box of the glow (coloured lobes are darker than the
    /// white background), at a given darkness threshold. Higher threshold isolates the bright core.</summary>
    private static unsafe Box Measure(Bitmap bmp, double darkThreshold)
    {
        if (bmp is not WriteableBitmap wb)
        {
            // CaptureRenderedFrame returns a WriteableBitmap; guard defensively.
            return default;
        }

        using var fb = wb.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
        double sx = 0, sy = 0, sw = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        byte* p = (byte*)fb.Address;
        for (int y = 0; y < h; y++)
        {
            byte* row = p + y * stride;
            for (int x = 0; x < w; x++)
            {
                byte* px = row + x * 4;
                double dark = 255.0 - (px[0] + px[1] + px[2]) / 3.0;
                if (dark > darkThreshold)
                {
                    sx += x * dark;
                    sy += y * dark;
                    sw += dark;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (sw <= 0)
            return default;
        return new Box(sx / sw, sy / sw, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY), sw);
    }

    /// <summary>Disposes the Skia headless session, swallowing the known load-induced teardown race
    /// (an NRE / "collection marked complete" thrown from <see cref="HeadlessUnitTestSession.Dispose"/>'s
    /// own teardown under heavy machine load). The measurements/assertions have already run by then, so
    /// this only suppresses spurious teardown noise — never a real assertion failure.</summary>
    private static void SafeDispose(HeadlessUnitTestSession? session)
    {
        try { session?.Dispose(); }
        catch (NullReferenceException) { }
        catch (InvalidOperationException) { }
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

    /// <summary>A render-priority tick that deliberately does NOT drain Background dispatcher jobs (it runs
    /// the render timer — which drives layout/arrange + the composition sync — then drains only Render and
    /// above). Used to prove resize placement is structural (arrange-at-focal) and does not depend on a
    /// Background-deferred re-pin that a real continuous drag would starve.</summary>
    private static void TickRenderOnly(int realMs)
    {
        if (realMs > 0)
            Thread.Sleep(realMs);
        try
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        catch
        {
            // Render timer not available on this platform variant.
        }
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
    }
}
