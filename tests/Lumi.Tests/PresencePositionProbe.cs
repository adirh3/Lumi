using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// The reliable, display-free <b>position</b> assessor for the presence field. Where
/// <see cref="PresenceMotionCaptureHarness"/> answers "does it MOVE?", this answers "is it in the RIGHT
/// PLACE — at every window size?". For a grid of (window size × focus point) scenarios it renders the real
/// <see cref="StrataPresence"/>, measures the brightness-weighted centroid of the actually-rendered glow,
/// converts it to a normalized (0..1) viewport fraction, and compares it to where the focus point asked the
/// light to pool. It writes a <c>position-report.txt</c> table (size, focus, measured centroid, error) so
/// the placement can be read as ground truth rather than guessed from a screenshot.
///
/// Two hard checks run when enabled: (1) at a horizontally-centred focus the pool is centred at EVERY
/// window size (the "centered regardless of window size" guarantee), and (2) a lower focus pools the light
/// lower (vertical tracking). A companion-split probe confirms an island split travels HORIZONTALLY.
/// Gated behind <c>PRESENCE_CAPTURE=1</c> so it is inert in normal CI runs (CI relies on
/// <see cref="PresenceGeometryTests"/> for the always-on math guarantees).
/// </summary>
[Collection("Headless UI")]
public sealed class PresencePositionProbe
{
    private readonly ITestOutputHelper _out;

    public PresencePositionProbe(ITestOutputHelper o) => _out = o;

    private static readonly (int W, int H)[] Sizes =
    {
        (1645, 980),  // typical maximized app
        (1280, 832),  // smaller window
        (2560, 1080), // ultrawide (where long-edge bugs surface)
        (1000, 1400), // portrait-ish (short edge = width)
    };

    [SkippableFact]
    public void Probe_FieldPositions()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("PRESENCE_CAPTURE") == "1",
            "Set PRESENCE_CAPTURE=1 to run the on-demand position probe.");

        var outDir = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_DIR")
                     ?? Path.Combine(Path.GetTempPath(), "Lumi-presence-position");
        Directory.CreateDirectory(outDir);

        var session = TryStartSession(out var skip);
        Skip.If(session is null, skip ?? "Skia headless session unavailable.");

        // Focus points to probe. The centred ones must pool centred at every size; the off-centre ones are
        // reported (their settled centroid is damped toward centre by the per-lobe follow, by design).
        var focus = new[]
        {
            new Point(0.5, 0.5),
            new Point(0.5, 0.80),
            new Point(0.5, 0.30),
            new Point(0.30, 0.62),
        };

        var report = new StringBuilder("size       focus        measuredCentroid   errX   note\n");
        var centeredErrors = new List<(string scene, double errX)>();
        var verticalTrack = new Dictionary<string, double>(); // key=size, value=centroidY at fp=(0.5,0.5)
        var verticalTrackLow = new Dictionary<string, double>();
        var rendered = false;

        try
        {
            foreach (var (w, h) in Sizes)
            {
                foreach (var fp in focus)
                {
                    double cxN = -1, cyN = -1;
                    session!.Dispatch(() =>
                    {
                        var presence = BuildPresence(PresenceState.Streaming, fp);
                        var window = ShowWindow(presence, w, h);

                        // First placement SNAPS to the focus (no travel), so a short settle is enough.
                        for (int i = 0; i < 14; i++)
                            Tick(16);

                        var bmp = window.CaptureRenderedFrame();
                        if (bmp is not null)
                        {
                            var (cx, cy) = Centroid(bmp);
                            if (cx >= 0)
                            {
                                cxN = cx / bmp.PixelSize.Width;
                                cyN = cy / bmp.PixelSize.Height;
                                rendered = true;
                            }
                            bmp.Dispose();
                        }
                        window.Close();
                    }, CancellationToken.None).GetAwaiter().GetResult();

                    var sizeKey = $"{w}x{h}";
                    var errX = cxN < 0 ? double.NaN : Math.Abs(cxN - fp.X);
                    var centred = Math.Abs(fp.X - 0.5) < 1e-9;
                    report.Append($"{sizeKey,-10} {fp.X:F2},{fp.Y:F2}    {cxN:F3},{cyN:F3}        {errX:F3}  {(centred ? "centred-X" : "off-centre")}\n");

                    if (cxN >= 0 && centred)
                        centeredErrors.Add(($"{sizeKey} fp={fp.Y:F2}", errX));
                    if (cyN >= 0 && Math.Abs(fp.Y - 0.5) < 1e-9 && Math.Abs(fp.X - 0.5) < 1e-9)
                        verticalTrack[sizeKey] = cyN;
                    if (cyN >= 0 && Math.Abs(fp.Y - 0.80) < 1e-9 && Math.Abs(fp.X - 0.5) < 1e-9)
                        verticalTrackLow[sizeKey] = cyN;
                }
            }
        }
        finally
        {
            SafeDispose(session);
        }

        File.WriteAllText(Path.Combine(outDir, "position-report.txt"), report.ToString());
        _out.WriteLine(report.ToString());
        _out.WriteLine($"artefacts in: {outDir}");

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");

        // (1) Centred focus => pool centred horizontally at EVERY window size.
        foreach (var (scene, errX) in centeredErrors)
            Assert.True(errX < 0.09, $"Field not horizontally centred for {scene}: |x-0.5|={errX:F3} (>0.09).");

        // (2) A lower focus pools the light lower (vertical tracking) at each size.
        foreach (var kv in verticalTrackLow)
            if (verticalTrack.TryGetValue(kv.Key, out var midY))
                Assert.True(kv.Value > midY + 0.05,
                    $"Lowering the focus did not pull the pool down for {kv.Key}: midY={midY:F3} lowY={kv.Value:F3}.");
    }

    [SkippableFact]
    public void Probe_CompanionSplit_IsHorizontal()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("PRESENCE_CAPTURE") == "1",
            "Set PRESENCE_CAPTURE=1 to run the on-demand companion-split probe.");

        var session = TryStartSession(out var skip);
        Skip.If(session is null, skip ?? "Skia headless session unavailable.");

        var report = new StringBuilder("size       island      companion(px)   fieldCentre(px)   dY(level)\n");
        var checks = new List<(string scene, double ox, double oy, double fy, double w, double h)>();

        try
        {
            foreach (var (w, h) in Sizes)
            {
                // The main field sits LOW (a composer-anchored chat), while a right-side island opens
                // higher up. The companion must travel RIGHT to the island but stay LEVEL with the field
                // (same vertical space) — NOT drift up to the island's own height.
                var fieldFocus = new Point(0.5, 0.78);
                var island = new Point(0.80, 0.42);
                double ox = 0, oy = 0, fy = 0;
                session!.Dispatch(() =>
                {
                    var presence = BuildPresence(PresenceState.Streaming, fieldFocus);
                    var window = ShowWindow(presence, w, h);
                    for (int i = 0; i < 8; i++) Tick(16);

                    presence.SplitToIsland(island);
                    for (int i = 0; i < 90; i++) Tick(16); // let the slow companion spring fully settle

                    (ox, oy) = presence.DebugCompanionOffset();
                    (_, fy) = presence.DebugFieldCenterOffset();
                    window.Close();
                }, CancellationToken.None).GetAwaiter().GetResult();

                report.Append($"{w}x{h,-6} {island.X:F2},{island.Y:F2}   {ox:F0},{oy:F0}        {0.0:F0},{fy:F0}          {Math.Abs(oy - fy):F0}\n");
                checks.Add(($"{w}x{h}", ox, oy, fy, w, h));
            }
        }
        finally
        {
            SafeDispose(session);
        }

        _out.WriteLine(report.ToString());

        foreach (var (scene, ox, oy, fy, w, h) in checks)
        {
            // Lands on the island horizontally: offset X must reach ~0.30·w to the right.
            var expectedX = (0.80 - 0.5) * w;
            Assert.True(ox > 0.6 * expectedX,
                $"Companion did not travel RIGHT to the island for {scene}: ox={ox:F0} expected≈{expectedX:F0}.");
            // SAME VERTICAL SPACE: the companion sits LEVEL with the field centre, not at the island's
            // height (which here is 0.36·h higher). The old island-Y placement would fail this hard.
            Assert.True(Math.Abs(oy - fy) < 0.10 * h,
                $"Companion split was not LEVEL with the field for {scene}: companionY={oy:F0} fieldY={fy:F0} (dY>{0.10 * h:F0}).");
        }
    }

    [SkippableFact]
    public void Probe_MergeRetractsHome()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("PRESENCE_CAPTURE") == "1",
            "Set PRESENCE_CAPTURE=1 to run the on-demand merge-retract probe.");

        var session = TryStartSession(out var skip);
        Skip.If(session is null, skip ?? "Skia headless session unavailable.");

        var report = new StringBuilder("size       island(px)   merged(px)   fieldCentre(px)   homeErr\n");
        var checks = new List<(string scene, double islX, double homeX, double homeY, double fx, double fy, double w, double h)>();

        try
        {
            // The merge-home math is size-independent (FieldOffset centring is already validated across 4
            // sizes by Probe_FieldPositions); one representative size keeps the heavy Skia render budget low.
            foreach (var (w, h) in new[] { (1645, 980) })
            {
                // Open an island (companion travels right), then CLOSE it. On close the companion must
                // retract all the way HOME into the live field centre (not to a stale panel centre), so
                // the un-split reads as a single pool drawing back into the field — not a blink.
                var fieldFocus = new Point(0.5, 0.78);
                var island = new Point(0.80, 0.42);
                double islX = 0, homeX = 0, homeY = 0, fx = 0, fy = 0;
                session!.Dispatch(() =>
                {
                    var presence = BuildPresence(PresenceState.Streaming, fieldFocus);
                    var window = ShowWindow(presence, w, h);
                    for (int i = 0; i < 8; i++) Tick(16);

                    presence.SplitToIsland(island);
                    for (int i = 0; i < 90; i++) Tick(16); // companion travels out to the island
                    (islX, _) = presence.DebugCompanionOffset();

                    presence.Merge();
                    for (int i = 0; i < 90; i++) Tick(16); // companion retracts home (slow spring)
                    (homeX, homeY) = presence.DebugCompanionOffset();
                    (fx, fy) = presence.DebugFieldCenterOffset();
                    window.Close();
                }, CancellationToken.None).GetAwaiter().GetResult();

                var homeErr = Math.Sqrt((homeX - fx) * (homeX - fx) + (homeY - fy) * (homeY - fy));
                report.Append($"{w}x{h,-6} {islX:F0}         {homeX:F0},{homeY:F0}      {fx:F0},{fy:F0}          {homeErr:F0}\n");
                checks.Add(($"{w}x{h}", islX, homeX, homeY, fx, fy, w, h));
            }
        }
        finally
        {
            SafeDispose(session);
        }

        _out.WriteLine(report.ToString());

        foreach (var (scene, islX, homeX, homeY, fx, fy, w, h) in checks)
        {
            // Sanity: it really was out at the island before the close.
            var expectedX = (0.80 - 0.5) * w;
            Assert.True(islX > 0.6 * expectedX,
                $"Companion did not reach the island before merge for {scene}: islX={islX:F0} expected≈{expectedX:F0}.");
            // After the close it has RETRACTED home onto the live field centre (both axes), not parked at a
            // stale offset — the polished un-split. Tolerance is a small fraction of the short edge.
            var tol = 0.06 * Math.Min(w, h);
            Assert.True(Math.Abs(homeX - fx) < tol && Math.Abs(homeY - fy) < tol,
                $"Companion did not retract HOME to the field centre for {scene}: merged=({homeX:F0},{homeY:F0}) field=({fx:F0},{fy:F0}) tol={tol:F0}.");
        }
    }

    /// <summary>
    /// Regression guard for the companion-split-on-attach race: <see cref="StrataPresence.SplitToIsland"/>
    /// must report <c>false</c> while the control is not yet composition-ready (so the controller leaves its
    /// dedup anchor clear and the settle timer retries), and <c>true</c> once ready — including a re-aim while
    /// already split. Before the fix it returned <c>void</c> and the controller cached the anchor regardless,
    /// so a persisted/open island on first attach would dedup every retry and the companion never appeared.
    /// </summary>
    [SkippableFact]
    public void Probe_SplitToIsland_DefersUntilReady()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("PRESENCE_CAPTURE") == "1",
            "Set PRESENCE_CAPTURE=1 to run the on-demand split-readiness probe.");

        var session = TryStartSession(out var skip);
        Skip.If(session is null, skip ?? "Skia headless session unavailable.");

        bool beforeReady = true, afterReady = false, reAim = false;
        try
        {
            session!.Dispatch(() =>
            {
                var presence = BuildPresence(PresenceState.Streaming, new Point(0.5, 0.78));
                var island = new Point(0.80, 0.42);

                // Not attached yet → composition not ready → the split must report "not applied".
                beforeReady = presence.SplitToIsland(island);

                var window = ShowWindow(presence, 1280, 832);
                for (int i = 0; i < 8; i++) Tick(16); // drains the posted InitComposition → _ready

                afterReady = presence.SplitToIsland(island);            // first real split → applied
                reAim = presence.SplitToIsland(new Point(0.82, 0.42));  // re-aim while active → still applied
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            SafeDispose(session);
        }

        Assert.False(beforeReady,
            "SplitToIsland must report not-applied before composition is ready, or the controller dedup blocks the retry and the companion never appears.");
        Assert.True(afterReady, "SplitToIsland must report applied once composition is ready.");
        Assert.True(reAim, "A re-aim while already split must still report applied.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────
    private static void SafeDispose(HeadlessUnitTestSession? session)
    {
        // The Skia headless session occasionally throws from its OWN Dispose() teardown under heavy machine
        // load (a known Avalonia headless race: an NRE or a "collection marked complete" on the job queue),
        // which is unrelated to the probe assertions — those have already run and recorded their result by
        // the time we dispose. Swallow only that teardown noise so these env-gated probes report their real
        // outcome instead of a spurious teardown failure.
        try { session?.Dispose(); }
        catch (NullReferenceException) { }
        catch (InvalidOperationException) { }
    }

    private static HeadlessUnitTestSession? TryStartSession(out string? skip)
    {
        skip = null;
        try
        {
            return HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        }
        catch (Exception ex)
        {
            skip = $"Skia headless session unavailable: {ex.Message}";
            return null;
        }
    }

    private static StrataPresence BuildPresence(PresenceState state, Point focus)
    {
        var res = Application.Current!.Resources;
        res["Color.AccentDefault"] = Color.FromRgb(120, 110, 245);
        res["Color.AccentViolet"] = Color.FromRgb(160, 100, 230);
        res["Color.AccentRose"] = Color.FromRgb(230, 110, 170);
        res["Palette.Warning400"] = Color.FromRgb(235, 175, 90);
        res["Palette.Success400"] = Color.FromRgb(90, 210, 140);
        res["Palette.Accent400"] = Color.FromRgb(110, 160, 240);
        res["Palette.Danger400"] = Color.FromRgb(235, 90, 90);

        return new StrataPresence
        {
            State = state,
            Intensity = 1.6,
            FocusReach = 1.0,
            FocusPoint = focus,
        };
    }

    private static Window ShowWindow(StrataPresence presence, int w, int h)
    {
        var window = new Window
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1A)),
            Content = presence,
        };
        window.Show();
        return window;
    }

    private static void Tick(int realMs)
    {
        if (realMs > 0)
            Thread.Sleep(realMs);
        try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        catch { /* render timer variant w/o manual tick */ }
        Dispatcher.UIThread.RunJobs();
    }

    private static (double X, double Y) Centroid(WriteableBitmap bmp)
    {
        using var fb = bmp.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
        double sx = 0, sy = 0, sw = 0;
        unsafe
        {
            byte* p = (byte*)fb.Address;
            for (int y = 0; y < h; y++)
            {
                byte* row = p + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + x * 4;
                    double bright = (px[0] + px[1] + px[2]) / 3.0 - 22.0;
                    if (bright > 8)
                    {
                        sx += x * bright;
                        sy += y * bright;
                        sw += bright;
                    }
                }
            }
        }
        return sw > 0 ? (sx / sw, sy / sw) : (-1, -1);
    }
}
