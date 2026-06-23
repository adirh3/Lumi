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
        double x0 = EnvD("PRESENCE_CAPTURE_X0", 0.5), y0 = EnvD("PRESENCE_CAPTURE_Y0", 0.42);
        double x1 = EnvD("PRESENCE_CAPTURE_X1", 0.5), y1 = EnvD("PRESENCE_CAPTURE_Y1", 0.78);
        int width = (int)EnvD("PRESENCE_CAPTURE_W", 760);
        int height = (int)EnvD("PRESENCE_CAPTURE_H", 820);
        int frames = (int)EnvD("PRESENCE_CAPTURE_FRAMES", 40);
        int stepMs = (int)EnvD("PRESENCE_CAPTURE_STEPMS", 28);
        bool overlay = Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_OVERLAY") == "1";

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

        var centroidsX = new List<double>();
        var centroidsY = new List<double>();
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

                // Begin the move under test.
                presence.FocusPoint = new Point(x1, y1);
                // Optionally fire the send "lift-off" kick on top of the focus glide (request: sending in
                // an existing chat lifts the field up off the composer), so the trail shows the full move.
                if (Environment.GetEnvironmentVariable("PRESENCE_CAPTURE_LIFT") == "1")
                    presence.Lift();
                Tick(0);

                for (int i = 0; i < frames; i++)
                {
                    Tick(stepMs);
                    var bmp = window.CaptureRenderedFrame();
                    if (bmp is null)
                        continue;
                    rendered = true;

                    var (cx, cy) = Centroid(bmp, ref trail, ref tw, ref th);
                    centroidsX.Add(cx);
                    centroidsY.Add(cy);
                    bmp.Save(Path.Combine(outDir, $"frame{i:D2}.png"));
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

    /// <summary>Brightness-weighted centroid (the glow is BRIGHT on the dark background), and folds the
    /// frame into the running per-pixel max-brightness trail buffer.</summary>
    private static (double X, double Y) Centroid(WriteableBitmap bmp, ref float[]? trail, ref int tw, ref int th)
    {
        using var fb = bmp.Lock();
        int w = fb.Size.Width, h = fb.Size.Height, stride = fb.RowBytes;
        if (trail is null) { trail = new float[w * h]; tw = w; th = h; }
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
                    // Brightness above the dark base (~0x16). Glow lobes read brighter than the surface.
                    double bright = (px[0] + px[1] + px[2]) / 3.0 - 22.0;
                    if (bright > 8)
                    {
                        sx += x * bright;
                        sy += y * bright;
                        sw += bright;
                        int idx = y * w + x;
                        if (idx < trail.Length && bright > trail[idx])
                            trail[idx] = (float)bright;
                    }
                }
            }
        }
        return sw > 0 ? (sx / sw, sy / sw) : (-1, -1);
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
