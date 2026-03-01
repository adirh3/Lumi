using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.Benchmark;

/// <summary>
/// Monitors rendering performance by tracking two distinct rates:
/// <list type="bullet">
///   <item><b>UPS (Updates Per Second)</b> — how often the dispatcher processes
///   render-priority callbacks. This is the UI thread throughput.</item>
///   <item><b>Render FPS</b> — actual compositor frame count measured via
///   <see cref="TopLevel.RequestAnimationFrame"/>, which fires exactly once
///   per vsync / display refresh. This is what the user actually sees.</item>
/// </list>
/// </summary>
internal sealed class FrameMonitor : IDisposable
{
    private readonly Stopwatch _stopwatch = new();
    private readonly List<double> _updateDeltas = new(10000);
    private readonly List<double> _renderFrameDeltas = new(10000);
    private readonly List<double> _scrollDeltas = new(10000);
    private readonly List<double> _scrollTimeDeltas = new(10000);
    private long _updateCount;
    private double _lastTickMs;
    private bool _isRunning;
    private DispatcherTimer? _updateTimer;
    private double _lastScrollOffset;
    private Func<double>? _getScrollOffset;
    private TopLevel? _topLevel;
    private int _renderFrameCount;
    private TimeSpan _lastRenderElapsed;

    /// <summary>Total elapsed time since monitoring started.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Wire up a function that returns the current scroll offset each frame.</summary>
    public void SetScrollOffsetProvider(Func<double> provider) => _getScrollOffset = provider;

    /// <summary>
    /// Sets the TopLevel used to subscribe to animation frame callbacks
    /// for accurate render FPS counting. Must be called before <see cref="Start"/>.
    /// </summary>
    public void SetTopLevel(TopLevel topLevel) => _topLevel = topLevel;

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _updateDeltas.Clear();
        _renderFrameDeltas.Clear();
        _scrollDeltas.Clear();
        _scrollTimeDeltas.Clear();
        _updateCount = 0;
        _lastTickMs = 0;
        _lastScrollOffset = _getScrollOffset?.Invoke() ?? 0;
        _renderFrameCount = 0;
        _lastRenderElapsed = TimeSpan.Zero;

        _stopwatch.Restart();

        // Request the first animation frame — each callback re-registers
        // so we get called exactly once per compositor/vsync frame.
        _topLevel?.RequestAnimationFrame(OnAnimationFrame);

        // DispatcherTimer at Render priority measures UI-thread update throughput (UPS).
        _updateTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1)
        };
        _updateTimer.Tick += OnUpdateTick;
        _updateTimer.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _stopwatch.Stop();
        _updateTimer?.Stop();
        _updateTimer = null;
        // Animation frame loop stops on its own because _isRunning is false.
    }

    private void OnAnimationFrame(TimeSpan elapsed)
    {
        if (!_isRunning) return;

        // Track the compositor-reported delta between consecutive frames.
        // This gives the true frame pacing as measured by the compositor,
        // independent of any UI-thread dispatch delays.
        if (_renderFrameCount > 0 && _lastRenderElapsed > TimeSpan.Zero)
        {
            var deltaMs = (elapsed - _lastRenderElapsed).TotalMilliseconds;
            if (deltaMs > 0.5)
                _renderFrameDeltas.Add(deltaMs);
        }
        _lastRenderElapsed = elapsed;
        _renderFrameCount++;

        _topLevel?.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnUpdateTick(object? sender, EventArgs e)
    {
        var nowMs = _stopwatch.Elapsed.TotalMilliseconds;
        var delta = nowMs - _lastTickMs;
        _lastTickMs = nowMs;

        if (_updateCount > 0 && delta > 0.5)
        {
            _updateDeltas.Add(delta);

            if (_getScrollOffset is not null)
            {
                var offset = _getScrollOffset();
                var scrollDelta = Math.Abs(offset - _lastScrollOffset);
                // Store (scrollDelta, updateDelta) so we can normalize velocity later.
                _scrollDeltas.Add(scrollDelta);
                _scrollTimeDeltas.Add(delta);
                _lastScrollOffset = offset;
            }
        }
        else if (_updateCount == 0 && _getScrollOffset is not null)
        {
            _lastScrollOffset = _getScrollOffset();
        }

        _updateCount++;
    }

    /// <summary>Trim duration in ms to discard from the start and end of raw data.</summary>
    private const double EdgeTrimMs = 250;

    /// <summary>Window size in ms for computing rolling min/max FPS and UPS.</summary>
    private const double WindowMs = 500;

    public FrameStatistics GetStatistics(string scenarioName)
    {
        var durationMs = _stopwatch.Elapsed.TotalMilliseconds;

        // --- Edge trimming ---
        // Discard the first and last 250ms of samples to remove startup/teardown artifacts.
        var updateDeltas = TrimEdges(_updateDeltas, EdgeTrimMs);
        var renderDeltas = TrimEdges(_renderFrameDeltas, EdgeTrimMs);
        var scrollPairs = TrimScrollEdges(_scrollDeltas, _scrollTimeDeltas, EdgeTrimMs);

        var times = updateDeltas.Where(t => t > 0.5).OrderBy(t => t).ToList();
        if (times.Count == 0)
            return new FrameStatistics { ScenarioName = scenarioName };

        var avgUpdateTime = times.Average();

        double Percentile(List<double> sorted, double p)
        {
            var idx = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
            return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
        }

        // --- Render FPS from compositor frame deltas ---
        var sortedRenderDeltas = renderDeltas.Where(d => d > 0.5).OrderBy(d => d).ToList();
        double avgRenderFps = 0, avgRenderDelta = 0;
        double minWindowRenderFps = 0, maxWindowRenderFps = 0;
        int droppedFrames = 0;
        if (sortedRenderDeltas.Count > 0)
        {
            avgRenderDelta = sortedRenderDeltas.Average();
            avgRenderFps = 1000.0 / avgRenderDelta;

            // Windowed min/max: compute rolling FPS over WindowMs windows.
            // This smooths timestamp jitter so min/max reflect sustained
            // performance dips, not single-sample noise.
            (minWindowRenderFps, maxWindowRenderFps) = ComputeWindowedMinMaxFps(renderDeltas, WindowMs);

            // Dropped frames: render deltas exceeding 1.5× the median frame time.
            // The median is robust against outliers, so this threshold adapts to
            // the actual display refresh rate without hardcoding.
            var medianRenderDelta = Percentile(sortedRenderDeltas, 50);
            var dropThreshold = medianRenderDelta * 1.5;
            droppedFrames = sortedRenderDeltas.Count(d => d > dropThreshold);
        }

        // --- Windowed UPS ---
        (var minWindowUps, var maxWindowUps) = ComputeWindowedMinMaxFps(updateDeltas, WindowMs);

        // --- Jank: render frames exceeding 2× median render frame time ---
        // Using render deltas (not update deltas) so jank is measured on what
        // the user actually sees, and using median (not mean) for robustness.
        int jankFrames = 0;
        double jankThresholdMs = 33.3;
        if (sortedRenderDeltas.Count > 0)
        {
            var medianRender = Percentile(sortedRenderDeltas, 50);
            jankThresholdMs = Math.Max(medianRender * 2, 33.3);
            jankFrames = sortedRenderDeltas.Count(d => d > jankThresholdMs);
        }

        // --- Scroll velocity in px/s (not px/update) ---
        // Normalizing by time makes velocity independent of update frequency.
        // AvgScrollVelocity includes zero-scroll ticks to give the true overall
        // scroll rate (total distance / total time). MaxScrollVelocity uses P99
        // of non-zero ticks to capture peak burst speed.
        double avgScrollVelPxPerSec = 0, maxScrollVelPxPerSec = 0;
        if (scrollPairs.Count > 0)
        {
            // All velocities including zeros — for true average rate
            var allVelocities = scrollPairs
                .Where(p => p.timeMs > 0.5)
                .Select(p => p.scrollPx / (p.timeMs / 1000.0))
                .ToList();
            if (allVelocities.Count > 0)
                avgScrollVelPxPerSec = allVelocities.Average();

            // Non-zero velocities only — for peak burst speed
            var burstVelocities = allVelocities.Where(v => v > 0).OrderBy(v => v).ToList();
            if (burstVelocities.Count > 0)
                maxScrollVelPxPerSec = Percentile(burstVelocities, 99);
        }

        return new FrameStatistics
        {
            ScenarioName = scenarioName,
            DurationMs = durationMs,

            // UPS metrics (dispatcher throughput)
            TotalUpdates = times.Count,
            AvgUps = 1000.0 / avgUpdateTime,
            MinUps = minWindowUps,
            MaxUps = maxWindowUps,
            AvgUpdateTimeMs = avgUpdateTime,
            P50UpdateTimeMs = Percentile(times, 50),
            P90UpdateTimeMs = Percentile(times, 90),
            P95UpdateTimeMs = Percentile(times, 95),
            P99UpdateTimeMs = Percentile(times, 99),
            UpdateTimeStdDev = StdDev(times, avgUpdateTime),

            // Render FPS metrics (actual displayed frames)
            // RenderFrames = trimmed delta count (each delta = one rendered frame interval).
            RenderFrames = sortedRenderDeltas.Count,
            AvgRenderFps = avgRenderFps,
            MinRenderFps = minWindowRenderFps,
            MaxRenderFps = maxWindowRenderFps,
            AvgRenderFrameTimeMs = avgRenderDelta,
            P50RenderFrameTimeMs = sortedRenderDeltas.Count > 0 ? Percentile(sortedRenderDeltas, 50) : 0,
            P90RenderFrameTimeMs = sortedRenderDeltas.Count > 0 ? Percentile(sortedRenderDeltas, 90) : 0,
            P99RenderFrameTimeMs = sortedRenderDeltas.Count > 0 ? Percentile(sortedRenderDeltas, 99) : 0,
            DroppedFrames = droppedFrames,

            // Jank (based on render frame deltas, not update deltas)
            JankFrames = jankFrames,
            JankPercentage = sortedRenderDeltas.Count > 0 ? (double)jankFrames / sortedRenderDeltas.Count * 100 : 0,
            JankThresholdMs = jankThresholdMs,

            // Scroll velocity (px/s, normalized by time)
            AvgScrollVelocityPxPerSec = avgScrollVelPxPerSec,
            MaxScrollVelocityPxPerSec = maxScrollVelPxPerSec,
        };
    }

    public IReadOnlyList<double> GetRawUpdateDeltas() =>
        _updateDeltas.Where(t => t > 0.5).ToList();

    /// <summary>
    /// Computes min and max FPS from rolling windows of the given size.
    /// Each window sums deltas until they exceed <paramref name="windowMs"/>,
    /// then computes FPS = frameCount / (summedDelta / 1000). Min/max are
    /// taken across all complete windows.
    /// </summary>
    private static (double min, double max) ComputeWindowedMinMaxFps(List<double> deltas, double windowMs)
    {
        if (deltas.Count < 2) return (0, 0);

        double minFps = double.MaxValue, maxFps = double.MinValue;
        double windowSum = 0;
        int windowFrames = 0;

        for (int i = 0; i < deltas.Count; i++)
        {
            if (deltas[i] <= 0.5) continue;
            windowSum += deltas[i];
            windowFrames++;

            if (windowSum >= windowMs)
            {
                var fps = windowFrames / (windowSum / 1000.0);
                minFps = Math.Min(minFps, fps);
                maxFps = Math.Max(maxFps, fps);
                windowSum = 0;
                windowFrames = 0;
            }
        }

        if (minFps == double.MaxValue) return (0, 0);
        return (minFps, maxFps);
    }

    /// <summary>
    /// Trims the first and last <paramref name="trimMs"/> worth of samples
    /// from a list of deltas. Returns a new list with the trimmed data.
    /// </summary>
    private static List<double> TrimEdges(List<double> deltas, double trimMs)
    {
        if (deltas.Count < 3 || trimMs <= 0) return deltas;

        // Find start index: skip samples until trimMs is consumed
        double cumulative = 0;
        int startIdx = 0;
        for (int i = 0; i < deltas.Count; i++)
        {
            cumulative += deltas[i];
            if (cumulative >= trimMs) { startIdx = i + 1; break; }
        }

        // Find end index: skip samples from the end until trimMs is consumed
        cumulative = 0;
        int endIdx = deltas.Count;
        for (int i = deltas.Count - 1; i >= startIdx; i--)
        {
            cumulative += deltas[i];
            if (cumulative >= trimMs) { endIdx = i; break; }
        }

        if (endIdx <= startIdx) return deltas; // Not enough data to trim
        return deltas.GetRange(startIdx, endIdx - startIdx);
    }

    /// <summary>
    /// Trims scroll data pairs (scrollDelta, timeDelta) using the same
    /// edge-trimming approach, based on cumulative time.
    /// </summary>
    private static List<(double scrollPx, double timeMs)> TrimScrollEdges(
        List<double> scrollDeltas, List<double> timeDeltas, double trimMs)
    {
        if (scrollDeltas.Count == 0 || scrollDeltas.Count != timeDeltas.Count)
            return [];

        double cumulative = 0;
        int startIdx = 0;
        for (int i = 0; i < timeDeltas.Count; i++)
        {
            cumulative += timeDeltas[i];
            if (cumulative >= trimMs) { startIdx = i + 1; break; }
        }

        cumulative = 0;
        int endIdx = timeDeltas.Count;
        for (int i = timeDeltas.Count - 1; i >= startIdx; i--)
        {
            cumulative += timeDeltas[i];
            if (cumulative >= trimMs) { endIdx = i; break; }
        }

        if (endIdx <= startIdx) return [];
        var result = new List<(double, double)>(endIdx - startIdx);
        for (int i = startIdx; i < endIdx; i++)
            result.Add((scrollDeltas[i], timeDeltas[i]));
        return result;
    }

    private static double StdDev(List<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        var sumSqDiff = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSqDiff / (values.Count - 1));
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Aggregated statistics for a single benchmark scenario run.
/// Separates UPS (UI thread update rate) from Render FPS (actual displayed frames).
/// </summary>
internal sealed class FrameStatistics
{
    public string ScenarioName { get; init; } = "";
    public double DurationMs { get; init; }

    // UPS — dispatcher throughput (may exceed display refresh rate)
    // Min/Max computed from rolling 500ms windows for noise resistance.
    public int TotalUpdates { get; init; }
    public double AvgUps { get; init; }
    public double MinUps { get; init; }
    public double MaxUps { get; init; }
    public double AvgUpdateTimeMs { get; init; }
    public double P50UpdateTimeMs { get; init; }
    public double P90UpdateTimeMs { get; init; }
    public double P95UpdateTimeMs { get; init; }
    public double P99UpdateTimeMs { get; init; }
    public double UpdateTimeStdDev { get; init; }

    // Render FPS — actual compositor frame rate (display-bound)
    // Min/Max computed from rolling 500ms windows for noise resistance.
    public int RenderFrames { get; init; }
    public double AvgRenderFps { get; init; }
    public double MinRenderFps { get; init; }
    public double MaxRenderFps { get; init; }
    public double AvgRenderFrameTimeMs { get; init; }
    public double P50RenderFrameTimeMs { get; init; }
    public double P90RenderFrameTimeMs { get; init; }
    public double P99RenderFrameTimeMs { get; init; }
    public int DroppedFrames { get; init; }

    // Jank (based on render frame deltas, not update deltas)
    public int JankFrames { get; init; }
    public double JankPercentage { get; init; }
    public double JankThresholdMs { get; init; }

    // Scroll velocity (px/s, time-normalized)
    public double AvgScrollVelocityPxPerSec { get; init; }
    public double MaxScrollVelocityPxPerSec { get; init; }

    public void WriteTo(BenchmarkOutput output, bool verbose = false, IReadOnlyList<double>? rawUpdateDeltas = null)
    {
        output.WriteLine();
        output.WriteLine($"  Scenario: {ScenarioName}");
        output.WriteLine($"  ─────────────────────────────────────────");
        output.WriteLine($"  Duration:     {DurationMs / 1000:F1}s");
        output.WriteLine($"  Render FPS:   avg {AvgRenderFps:F1}  min {MinRenderFps:F1}  max {MaxRenderFps:F1}  ({RenderFrames} frames)");
        output.WriteLine($"  Render time:  avg {AvgRenderFrameTimeMs:F2}ms  P50={P50RenderFrameTimeMs:F2}ms  P90={P90RenderFrameTimeMs:F2}ms  P99={P99RenderFrameTimeMs:F2}ms");
        output.WriteLine($"  Dropped:      {DroppedFrames} frames (>{P50RenderFrameTimeMs * 1.5:F1}ms threshold)");
        output.WriteLine($"  Jank:         {JankFrames} frames ({JankPercentage:F1}%) threshold={JankThresholdMs:F1}ms");
        output.WriteLine($"  UPS:          avg {AvgUps:F1}  min {MinUps:F1}  max {MaxUps:F1}  ({TotalUpdates} updates)");
        output.WriteLine($"  Update time:  avg {AvgUpdateTimeMs:F2}ms  P50={P50UpdateTimeMs:F2}ms  P90={P90UpdateTimeMs:F2}ms  P99={P99UpdateTimeMs:F2}ms");
        output.WriteLine($"  Std dev:      {UpdateTimeStdDev:F2}ms");
        output.WriteLine($"  Scroll vel:   avg {AvgScrollVelocityPxPerSec:F0} px/s  max(P99) {MaxScrollVelocityPxPerSec:F0} px/s");

        if (verbose && rawUpdateDeltas is { Count: > 0 })
        {
            output.WriteLine($"  Raw update deltas (ms):");
            var sb = new StringBuilder("    ");
            for (int i = 0; i < rawUpdateDeltas.Count; i++)
            {
                sb.Append($"{rawUpdateDeltas[i]:F1}");
                if (i < rawUpdateDeltas.Count - 1) sb.Append(", ");
                if (sb.Length > 100)
                {
                    output.WriteLine(sb.ToString());
                    sb.Clear();
                    sb.Append("    ");
                }
            }
            if (sb.Length > 4)
                output.WriteLine(sb.ToString());
        }
    }
}

/// <summary>
/// Complete benchmark report with all scenario results.
/// </summary>
internal sealed class BenchmarkReport
{
    public string Timestamp { get; init; } = DateTimeOffset.Now.ToString("o");
    public string Platform { get; init; } = Environment.OSVersion.ToString();
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;
    public List<FrameStatistics> Scenarios { get; init; } = [];

    public void WriteSummary(BenchmarkOutput output)
    {
        output.WriteLine();
        output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        output.WriteLine("║              Lumi Scroll Benchmark Results                  ║");
        output.WriteLine("╠══════════════════════════════════════════════════════════════╣");
        output.WriteLine($"║  Time: {Timestamp[..19],-20} Cores: {ProcessorCount,-5}              ║");
        output.WriteLine($"║  Platform: {Platform,-48}║");
        output.WriteLine("╚══════════════════════════════════════════════════════════════╝");

        foreach (var s in Scenarios)
            s.WriteTo(output);

        output.WriteLine();
        output.WriteLine("  ═══════════════════════════════════════════");
        output.WriteLine("  OVERALL SUMMARY");
        output.WriteLine("  ═══════════════════════════════════════════");

        if (Scenarios.Count > 0)
        {
            output.WriteLine($"  Scenarios run:      {Scenarios.Count}");
            output.WriteLine($"  Avg render FPS:     {Scenarios.Average(s => s.AvgRenderFps):F1}");
            output.WriteLine($"  Min render FPS:     {Scenarios.Min(s => s.MinRenderFps):F1}  (500ms window)");
            output.WriteLine($"  Avg UPS:            {Scenarios.Average(s => s.AvgUps):F1}");
            output.WriteLine($"  Worst P99 render:   {Scenarios.Max(s => s.P99RenderFrameTimeMs):F2}ms");
            output.WriteLine($"  Worst P99 update:   {Scenarios.Max(s => s.P99UpdateTimeMs):F2}ms");
            output.WriteLine($"  Worst jank %:       {Scenarios.Max(s => s.JankPercentage):F1}%");
            output.WriteLine($"  Total jank frames:  {Scenarios.Sum(s => s.JankFrames)}");
            output.WriteLine($"  Total dropped:      {Scenarios.Sum(s => s.DroppedFrames)}");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, BenchmarkReportJsonContext.Default.BenchmarkReport);
}

[JsonSerializable(typeof(BenchmarkReport))]
[JsonSerializable(typeof(FrameStatistics))]
internal partial class BenchmarkReportJsonContext : JsonSerializerContext;
