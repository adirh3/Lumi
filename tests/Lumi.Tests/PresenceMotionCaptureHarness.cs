using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using Xunit.Abstractions;

namespace Lumi.Tests;

/// <summary>
/// On-demand, human-inspectable pixel-level capture of the REAL <see cref="StrataPresence"/> focus
/// glide. Renders the control on a dark, app-like background (so the bright glow's path is directly
/// visible) and drives a real focus transition while capturing composited frames via
/// <see cref="WindowBase.CaptureRenderedFrame"/> (which DOES apply composition-thread <c>Offset</c>
/// transforms, unlike OS/MCP screenshots).
///
/// It writes three artefacts to disk:
/// <list type="bullet">
/// <item>per-frame PNGs (<c>frameNN.png</c>),</item>
/// <item>a single <c>trail.png</c> composite = per-pixel MAX brightness across every frame. A smooth
/// glide leaves ONE continuous streak; a teleport leaves TWO separate blobs; no motion leaves one
/// blob. One image tells the whole story.</item>
/// <item>a <c>trajectory.csv</c> of the brightness-weighted centroid (x,y) per frame, so the travel
/// amplitude and per-frame step sizes are measurable, not guessed.</item>
/// </list>
///
/// This is the counterpart to <see cref="PresenceSpringTests"/>: the spring tests prove the motion
/// MODEL is C¹-smooth; this proves the rendered PIXELS actually move and by how much (perceptibility).
/// Gated behind <c>PRESENCE_CAPTURE=1</c> so it is inert in normal CI runs.
/// </summary>
[Collection("Headless UI")]
[Trait(HeadlessTestSession.HostTrait, HeadlessTestSession.OwnHost)]
public sealed class PresenceMotionCaptureHarness
{
    private readonly ITestOutputHelper _out;

    public PresenceMotionCaptureHarness(ITestOutputHelper o) => _out = o;

    [SkippableFact]
    public void Capture_FocusGlide()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("PRESENCE_CAPTURE") == "1",
            "Set PRESENCE_CAPTURE=1 to run the on-demand capture harness.");

        var outDir = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_DIR")
                     ?? Path.Combine(Path.GetTempPath(), "Lumi-presence-capture");
        Directory.CreateDirectory(outDir);

        // Scenario knobs (env-overridable so I can sweep states / endpoints without recompiling).
        var stateName = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_STATE") ?? "Idle";
        var state = Enum.TryParse<PresenceState>(stateName, true, out var st) ? st : PresenceState.Idle;
        // Optional END state, so the capture can replicate a state transition (e.g. welcome Dormant ->
        // existing-chat Idle) where the ambient field brightens as the focus glides. Defaults to start.
        var state1Name = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_STATE1");
        var state1 = state1Name is not null && Enum.TryParse<PresenceState>(state1Name, true, out var st1) ? st1 : state;
        // Welcome-mark luminance knobs: start with the Halo lit (new-chat hero glow) and/or extinguish it
        // ON the move, so the capture mirrors the REAL new-chat -> existing-chat handoff (the bright Halo
        // must visibly ride DOWN with the focus, not just cross-fade out in place).
        bool haloStart = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_HALO") == "1";
        bool haloOff = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_HALO_OFF") == "1";
        double x0 = EnvD("PRESENCE_CAPTURE_X0", 0.5), y0 = EnvD("PRESENCE_CAPTURE_Y0", 0.42);
        double x1 = EnvD("PRESENCE_CAPTURE_X1", 0.5), y1 = EnvD("PRESENCE_CAPTURE_Y1", 0.78);
        int width = (int)EnvD("PRESENCE_CAPTURE_W", 760);
        int height = (int)EnvD("PRESENCE_CAPTURE_H", 820);
        int frames = (int)EnvD("PRESENCE_CAPTURE_FRAMES", 40);
        int stepMs = (int)EnvD("PRESENCE_CAPTURE_STEPMS", 28);
        // Capture this many frames of the RESTING start state BEFORE triggering the move, so the trajectory
        // measures the full rest -> rest travel (and the trail shows the true origin), not a mid-move start.
        int preFrames = (int)EnvD("PRESENCE_CAPTURE_PREFRAMES", 0);
        // Simulate the chat-load UI-thread STALL that makes welcome->existing unique: a heavy transcript
        // rebuild hogs the UI thread, so the render-glide commit is delayed while the wall-clock the spring
        // re-aims read keeps advancing. STALL_MS = how long to block (no render commit) right after arming
        // the move; then a follow-timer-style re-aim (to REAIM_Y) reads that stale clock == the desync that
        // teleports the bright Halo. STALL_BEFORE=1 instead drains the stall BEFORE arming (the deferred
        // hand-off fix), so the descent is armed in the clear.
        int stallMs = (int)EnvD("PRESENCE_CAPTURE_STALL_MS", 0);
        bool stallBefore = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_STALL_BEFORE") == "1";
        double reaimY = EnvD("PRESENCE_CAPTURE_REAIM_Y", y1 + 0.02);
        bool overlay = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_OVERLAY") == "1";

        HeadlessUnitTestSession? session = null;
        string? skipReason = null;
        try
        {
            session = HeadlessTestSession.StartOwn(typeof(SkiaHeadlessTestApp));
        }
        catch (Exception ex)
        {
            skipReason = $"Skia headless session unavailable: {ex.Message}";
        }

        Skip.If(session is null, skipReason ?? "Skia headless session unavailable.");

        var centroidsX = new List<double>();
        var centroidsY = new List<double>();
        var coresY = new List<double>();
        // Real wall-clock elapsed (ms) since the move was triggered, per frame. The headless compositor
        // advances by wall-clock, so this is the TRUE time axis of the glide — without it I can't tell a
        // slow followable descent from a fast snap (the whole point Adir keeps pushing on).
        var times = new List<double>();
        var moveClock = new System.Diagnostics.Stopwatch();
        float[]? trail = null;
        int tw = 0, th = 0;
        var rendered = false;

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
                    State = state,
                    Intensity = EnvD("PRESENCE_CAPTURE_INTENSITY", 1.6),
                    FocusReach = 1.0,
                    FocusPoint = new Point(x0, y0),
                };
                // Light the welcome Halo BEFORE warmup so its slow opacity breath has ramped to full by the
                // time the move starts (mirrors sitting on the new-chat screen before clicking a chat).
                if (haloStart)
                    presence.Halo = true;

                // A panel grid that mimics the live "show-through-translucent-glass" path so the capture
                // reflects what the user actually sees (the presence sits BEHIND a translucent surface).
                Control content = presence;
                if (overlay)
                {
                    content = new Grid
                    {
                        Children =
                        {
                            presence,
                            new Border
                            {
                                Background = new SolidColorBrush(Color.FromArgb(150, 22, 22, 28)),
                            },
                        },
                    };
                }

                var window = new Window
                {
                    Width = width,
                    Height = height,
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1A)), // app dark surface
                    Content = content,
                };
                window.Show();

                for (int i = 0; i < 6; i++)
                    Tick(40); // attach + InitComposition (posted at Loaded) + initial snap to (x0,y0)

                var snapAtY0 = presence.DebugFocusSnapshot();

                int frameIdx = 0;
                // Hold on the resting start state so the trajectory begins at the true origin (the welcome
                // luminance at rest), making the subsequent travel measurable rest -> rest.
                for (int i = 0; i < preFrames; i++)
                {
                    Tick(stepMs);
                    var pbmp = window.CaptureRenderedFrame();
                    if (pbmp is null)
                        continue;
                    rendered = true;
                    var (pcx, pcy, pcore) = Centroid(pbmp, ref trail, ref tw, ref th);
                    centroidsX.Add(pcx);
                    centroidsY.Add(pcy);
                    coresY.Add(pcore);
                    times.Add(double.NaN); // resting pre-move frames have no move-relative time
                    pbmp.Save(Path.Combine(outDir, $"frame{frameIdx:D2}.png"));
                    frameIdx++;
                    pbmp.Dispose();
                }

                // Begin the move under test. Apply the state transition + Halo extinguish FIRST (the
                // ambient field brightens as the welcome luminance lets go), then retarget the focus and
                // fire the felt kick — mirroring the controller's welcome -> existing-chat handoff.
                // DEFERRED hand-off fix: drain the simulated chat-load stall BEFORE arming, so the spring
                // is armed in the clear (its wall-clock baseline matches the render commit → no desync).
                if (stallBefore && stallMs > 0)
                    Thread.Sleep(stallMs);

                if (state1 != state)
                    presence.State = state1;
                if (haloOff)
                    presence.Halo = false;
                presence.FocusPoint = new Point(x1, y1);
                // Optionally fire the send "lift-off" kick on top of the focus glide (request: sending in
                // an existing chat lifts the field up off the composer), so the trail shows the full move.
                if (Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_LIFT") == "1")
                    presence.Lift();
                // Or the mirror "descend" kick (request: new-chat → existing-chat pours the field down).
                if (Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_DESCEND") == "1")
                    presence.Descend();

                // BUGGY path: arm, then STALL the UI thread (wall-clock advances, NO render commit), then a
                // follow-timer-style re-aim reads the now-stale clock → AimHostSpring evaluates the glide as
                // already settled and seeds a teleport. This reproduces the welcome→existing snap.
                if (!stallBefore && stallMs > 0)
                {
                    Thread.Sleep(stallMs);
                    presence.FocusPoint = new Point(x1, reaimY);
                }

                Tick(0);
                moveClock.Restart();

                for (int i = 0; i < frames; i++)
                {
                    Tick(stepMs);
                    var bmp = window.CaptureRenderedFrame();
                    if (bmp is null)
                        continue;
                    rendered = true;

                    var (cx, cy, core) = Centroid(bmp, ref trail, ref tw, ref th);
                    centroidsX.Add(cx);
                    centroidsY.Add(cy);
                    coresY.Add(core);
                    times.Add(moveClock.Elapsed.TotalMilliseconds);
                    bmp.Save(Path.Combine(outDir, $"frame{frameIdx:D2}.png"));
                    frameIdx++;
                    bmp.Dispose();
                }

                var snapAtY1 = presence.DebugFocusSnapshot();
                File.WriteAllText(Path.Combine(outDir, "focus-snapshot.txt"),
                    "=== after warmup (y0) ===\n" + snapAtY0 + "\n=== after move settles (y1) ===\n" + snapAtY1);

                // Build the trail composite here, inside the session dispatch, where the Avalonia render
                // platform is alive (constructing a WriteableBitmap after Dispose throws).
                if (trail is not null)
                    SaveTrail(trail, tw, th, Path.Combine(outDir, "trail.png"));

                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            session?.Dispose();
        }

        Skip.IfNot(rendered, "Skia headless capture returned no rendered glow (drawing-free platform).");

        var csv = new System.Text.StringBuilder("frame,x,y\n");
        for (int i = 0; i < centroidsY.Count; i++)
            csv.Append(i).Append(',').Append(centroidsX[i].ToString("F1", CultureInfo.InvariantCulture))
               .Append(',').Append(centroidsY[i].ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
        File.WriteAllText(Path.Combine(outDir, "trajectory.csv"), csv.ToString());

        var travelY = centroidsY[^1] - centroidsY[0];
        var travelX = centroidsX[^1] - centroidsX[0];
        _out.WriteLine($"state={state} overlay={overlay} size={width}x{height}");
        _out.WriteLine($"focus {x0:F2},{y0:F2} -> {x1:F2},{y1:F2}");
        _out.WriteLine($"frames={centroidsY.Count} travelX={travelX:F0}px travelY={travelY:F0}px");
        _out.WriteLine($"Y trajectory: [{string.Join(", ", centroidsY.Select(v => v.ToString("F0")))}]");
        _out.WriteLine($"X trajectory: [{string.Join(", ", centroidsX.Select(v => v.ToString("F0")))}]");

        // The TRUE time axis of the descent (move-relative ms per post-move frame) + a (t,y) view, so a
        // slow followable glide is distinguishable from a fast snap.
        var tline = times.Select(t => double.IsNaN(t) ? "rest" : t.ToString("F0")).ToList();
        _out.WriteLine($"t(ms): [{string.Join(", ", tline)}]");
        var ty = new List<string>();
        for (int i = 0; i < times.Count; i++)
            if (!double.IsNaN(times[i]))
                ty.Add($"{times[i]:F0}ms:{centroidsY[i]:F0}");
        _out.WriteLine($"(t,Y) move: [{string.Join("  ", ty)}]");

        // BRIGHT-CORE trajectory == where the actual *body* of light is. If this glides smoothly start->end
        // the light is MOVING; if it sits at the origin then JUMPS to the destination it is a cross-fade.
        var coreTravel = coresY[^1] - coresY[0];
        _out.WriteLine($"core Y trajectory: [{string.Join(", ", coresY.Select(v => v.ToString("F0")))}]");
        var tc = new List<string>();
        for (int i = 0; i < times.Count; i++)
            if (!double.IsNaN(times[i]))
                tc.Add($"{times[i]:F0}ms:{coresY[i]:F0}");
        _out.WriteLine($"(t,core) move: [{string.Join("  ", tc)}]");
        _out.WriteLine($"core travelY={coreTravel:F0}px  (full-centroid travelY={travelY:F0}px)");
        // Monotonicity of the bright core == smoothness. Count backward steps (a body easing down never
        // reverses; a cross-fade or a floor-bounce does), and the largest single-frame jump.
        int coreBacksteps = 0; double coreMaxStep = 0;
        for (int i = 1; i < coresY.Count; i++)
        {
            var d = coresY[i] - coresY[i - 1];
            if (coreTravel >= 0 && d < -1.5) coreBacksteps++;
            if (coreTravel < 0 && d > 1.5) coreBacksteps++;
            if (Math.Abs(d) > coreMaxStep) coreMaxStep = Math.Abs(d);
        }
        _out.WriteLine($"core backsteps={coreBacksteps} maxStep={coreMaxStep:F0}px (low backsteps + moderate step = a smooth travelling body)");

        // How long the bright centroid takes to cover 50% / 90% of its total travel == "is it followable?"
        var y0c = centroidsY[0];
        double Frac(double f)
        {
            var targetVal = y0c + travelY * f;
            for (int i = 0; i < times.Count; i++)
                if (!double.IsNaN(times[i]) &&
                    ((travelY >= 0 && centroidsY[i] >= targetVal) || (travelY < 0 && centroidsY[i] <= targetVal)))
                    return times[i];
            return double.NaN;
        }
        _out.WriteLine($"reach 50% travel @ {Frac(0.5):F0}ms | 90% @ {Frac(0.9):F0}ms (longer = more followable)");
        _out.WriteLine($"artefacts in: {outDir}");
    }

    private static double EnvD(string key, double fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return v is not null && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static void Tick(int realMs)
    {
        if (realMs > 0)
            Thread.Sleep(realMs);
        try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        catch { /* render timer variant w/o manual tick */ }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Brightness-weighted centroid (the glow is BRIGHT on the dark background), the centroid of
    /// just the BRIGHT CORE (top band of this frame's brightness — tracks the actual *body* of light, so a
    /// real travelling body glides while a cross-fade jumps), and folds the frame into the running
    /// per-pixel max-brightness trail buffer.</summary>
    private static (double X, double Y, double CoreY) Centroid(WriteableBitmap bmp, ref float[]? trail, ref int tw, ref int th)
    {
        using var fb = bmp.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
        if (trail is null) { trail = new float[w * h]; tw = w; th = h; }
        double sx = 0, sy = 0, sw = 0, maxB = 0;
        double coreY;
        unsafe
        {
            byte* p = (byte*)fb.Address;
            for (int y = 0; y < h; y++)
            {
                byte* row = p + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + x * 4;
                    // Brightness above the dark base (~0x16). Glow lobes read brighter than the surface.
                    double bright = (px[0] + px[1] + px[2]) / 3.0 - 22.0;
                    if (bright > 8)
                    {
                        sx += x * bright;
                        sy += y * bright;
                        sw += bright;
                        if (bright > maxB) maxB = bright;
                        int idx = y * w + x;
                        if (idx < trail.Length && bright > trail[idx])
                            trail[idx] = (float)bright;
                    }
                }
            }

            // Second pass: centroid of only the BRIGHT CORE (>= 72% of this frame's peak). This follows the
            // bright body of light, ignoring the dim ambient wash. A body that physically TRAVELS moves this
            // smoothly from start to end; a cross-fade (welcome luminance dims in place while a separate
            // ambient pool brightens low) makes it JUMP between the two cores — the litmus for "is it moving".
            double cy2 = 0, cw2 = 0, thr = maxB * 0.72;
            for (int y = 0; y < h; y++)
            {
                byte* row = p + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + x * 4;
                    double bright = (px[0] + px[1] + px[2]) / 3.0 - 22.0;
                    if (bright >= thr && bright > 8)
                    {
                        cy2 += y * bright;
                        cw2 += bright;
                    }
                }
            }
            coreY = cw2 > 0 ? cy2 / cw2 : -1;
        }
        return sw > 0 ? (sx / sw, sy / sw, coreY) : (-1, -1, coreY);
    }

    /// <summary>Writes the accumulated max-brightness trail as a viewable PNG (cool-white glow on black).</summary>
    private static void SaveTrail(float[] trail, int w, int h, string path)
    {
        float max = 1f;
        foreach (var v in trail) if (v > max) max = v;

        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            unsafe
            {
                byte* p = (byte*)fb.Address;
                int stride = fb.RowBytes;
                for (int y = 0; y < h; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        float n = trail[y * w + x] / max;            // 0..1
                        if (n < 0) n = 0; if (n > 1) n = 1;
                        byte b = (byte)(255 * Math.Min(1f, n * 1.15f));
                        byte g = (byte)(225 * n);
                        byte r = (byte)(200 * n);
                        byte* px = row + x * 4;
                        px[0] = b; px[1] = g; px[2] = r; px[3] = 255;  // BGRA premul, opaque
                    }
                }
            }
        }
        bmp.Save(path);
    }
}
