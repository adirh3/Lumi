#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;

namespace Lumi.MemoryDiagnostics;

/// <summary>
/// Scrolls the transcript as a two-finger touchpad does and measures how smooth it looks. Each
/// gesture sends wheel reports of a few pixels at the touchpad's report rate — a steady stroke, then
/// a glide that slows to a stop — and then pauses until what it reached shows (capped), as a reader
/// would. Reports are raised on the element under the pointer, so every wheel handler on the way
/// (Avalonia's scroll presenter, the chat shell, the transcript) runs as it does for a real
/// touchpad. Each frame the UI thread produces is recorded with how much of the viewport showed no
/// content yet, and each report with how long it waited for the UI thread. The history harness runs
/// the same file in every build it compares.
/// </summary>
internal sealed class TouchpadScroll
{
    // Precision touchpads report at about 120 Hz.
    private const double ReportIntervalMs = 1000d / 120;
    private const double StrokeMs = 400;
    // After the fingers lift, the content glides on, slowing exponentially until it barely moves.
    private const double GlideTimeConstantMs = 325;
    private const double GlideStopPxPerSecond = 50;
    // Reading, skimming and flicking speeds, in turn.
    private static readonly double[] StrokeSpeedsPxPerSecond = [1200, 2400, 4000];
    // Avalonia's ScrollContentPresenter moves 50 px per wheel unit.
    private const double PixelsPerWheelUnit = 50;
    // The viewport shows its content once no more than this share of it is still empty.
    private const double ShownBlankFraction = 0.02;
    private const double MaxWaitForContentMs = 1500;
    private const double ReadingPauseMs = 120;
    private const int IdleFramesMs = 600;

    private readonly ScrollViewer _scrollViewer;
    private readonly ScrollContentPresenter _presenter;
    private readonly Panel _panel;
    private readonly TopLevel _root;
    private readonly Point _pointerPosition;
    private readonly Pointer _pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Frame> _frames = new(8192);
    private readonly List<WheelRecord> _wheels = new(8192);
    private bool _recording;

    // Written on the UI thread, read by the gesture thread.
    private double _lastFrameMs;
    private double _lastFrameBlank;
    private double _lastHandledMs;
    private int _handled;
    private int _atEdge;

    private TouchpadScroll(ScrollViewer scrollViewer, ScrollContentPresenter presenter, Panel panel, TopLevel root, Point pointerPosition)
    {
        _scrollViewer = scrollViewer;
        _presenter = presenter;
        _panel = panel;
        _root = root;
        _pointerPosition = pointerPosition;
    }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Scrolls toward the top (or the bottom) for up to <paramref name="gestures"/> gestures.</summary>
    public static async Task<TouchpadResult> RunAsync(ScrollViewer scrollViewer, bool toTop, int gestures)
    {
        var scroll = await Dispatcher.UIThread.InvokeAsync(() => Create(scrollViewer));
        return await scroll.ScrollAsync(toTop, gestures);
    }

    private static TouchpadScroll Create(ScrollViewer scrollViewer)
    {
        var presenter = scrollViewer.Presenter as ScrollContentPresenter
            ?? throw new InvalidOperationException("The transcript has no scroll presenter.");
        var items = scrollViewer.GetVisualDescendants().OfType<TranscriptItemsControl>().FirstOrDefault()
            ?? throw new InvalidOperationException("The transcript's items were not found.");
        var panel = items.ItemsPanelRoot
            ?? throw new InvalidOperationException("The transcript has no items panel.");
        var root = TopLevel.GetTopLevel(scrollViewer)
            ?? throw new InvalidOperationException("The transcript is not in a window.");
        // The pointer rests over the transcript, clear of the header and the composer floating over it.
        var pointer = scrollViewer.TranslatePoint(new Point(scrollViewer.Bounds.Width / 2, scrollViewer.Bounds.Height * 0.4), root)
            ?? throw new InvalidOperationException("The transcript has no position in its window.");
        return new TouchpadScroll(scrollViewer, presenter, panel, root, pointer);
    }

    private async Task<TouchpadResult> ScrollAsync(bool toTop, int gestures)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _recording = true;
            _root.RequestAnimationFrame(OnFrame);
        });

        // The frames before the first gesture give the display's frame interval.
        await Task.Delay(IdleFramesMs);
        var gesturesStartMs = Now;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunGestures(toTop, gestures, finished))
        {
            IsBackground = true,
            Name = "Touchpad gestures",
        };
        thread.Start();
        await finished.Task;

        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _recording = false;
            return Analyze(gesturesStartMs);
        });
    }

    private void RunGestures(bool toTop, int gestures, TaskCompletionSource finished)
    {
        try
        {
            var sent = 0;
            for (var gesture = 0; gesture < gestures && Volatile.Read(ref _atEdge) == 0; gesture++)
            {
                var speed = StrokeSpeedsPxPerSecond[gesture % StrokeSpeedsPxPerSecond.Length];
                var endMs = StrokeMs + GlideTimeConstantMs * Math.Log(speed / GlideStopPxPerSecond);
                var startMs = Now;
                var travelled = 0d;
                for (var report = 1; ; report++)
                {
                    var elapsedMs = Math.Min(report * ReportIntervalMs, endMs);
                    WaitUntil(startMs + elapsedMs);
                    var position = Travel(speed, elapsedMs);
                    var wheel = new Wheel(gesture, startMs + elapsedMs, toTop ? position - travelled : travelled - position);
                    travelled = position;
                    Dispatcher.UIThread.Post(() => Apply(wheel), DispatcherPriority.Input);
                    sent++;
                    if (elapsedMs >= endMs)
                        break;
                }

                // The fingers are off: wait until every report is handled, then until what the gesture
                // reached shows, as a reader would before scrolling on.
                WaitFor(() => Volatile.Read(ref _handled) >= sent, Now + 30_000);
                var stoppedMs = Volatile.Read(ref _lastHandledMs);
                WaitFor(
                    () => Volatile.Read(ref _lastFrameMs) > stoppedMs && Volatile.Read(ref _lastFrameBlank) <= ShownBlankFraction,
                    stoppedMs + MaxWaitForContentMs);
                WaitUntil(Now + ReadingPauseMs);
            }

            finished.SetResult();
        }
        catch (Exception ex)
        {
            finished.SetException(ex);
        }
    }

    /// <summary>How far a gesture at <paramref name="speedPxPerSecond"/> has moved after <paramref name="elapsedMs"/>.</summary>
    private static double Travel(double speedPxPerSecond, double elapsedMs)
    {
        var stroke = Math.Min(elapsedMs, StrokeMs);
        var glide = Math.Max(0, elapsedMs - StrokeMs);
        return speedPxPerSecond / 1000 * (stroke + GlideTimeConstantMs * (1 - Math.Exp(-glide / GlideTimeConstantMs)));
    }

    // Reports keep the touchpad's rhythm whatever the UI thread is doing; a sleep could overshoot by a
    // whole timer tick, so the last stretch spins.
    private void WaitUntil(double targetMs)
    {
        while (true)
        {
            var remaining = targetMs - Now;
            if (remaining <= 0)
                return;
            if (remaining > 20)
                Thread.Sleep(1);
            else
                Thread.Yield();
        }
    }

    private void WaitFor(Func<bool> condition, double deadlineMs)
    {
        while (!condition() && Now < deadlineMs)
            Thread.Sleep(1);
    }

    private void Apply(Wheel wheel)
    {
        var handledMs = Now;
        var before = _presenter.Offset.Y;
        var target = TargetUnderPointer();
        target.RaiseEvent(new PointerWheelEventArgs(
            target,
            _pointer,
            _root,
            _pointerPosition,
            (ulong)Environment.TickCount64,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None,
            new Vector(0, wheel.Pixels / PixelsPerWheelUnit)));
        _wheels.Add(new WheelRecord(wheel.Gesture, wheel.SentMs, handledMs, _presenter.Offset.Y - before));

        var maxOffset = Math.Max(0, _presenter.Extent.Height - _presenter.Viewport.Height);
        if (wheel.Pixels > 0 ? _presenter.Offset.Y <= 0.5 : _presenter.Offset.Y >= maxOffset - 0.5)
            Volatile.Write(ref _atEdge, 1);
        Volatile.Write(ref _lastHandledMs, handledMs);
        Interlocked.Increment(ref _handled);
    }

    private Interactive TargetUnderPointer()
        => _root.InputHitTest(_pointerPosition) is Interactive hit
           && hit is Visual visual
           && _presenter.IsVisualAncestorOf(visual)
            ? hit
            : _presenter;

    private void OnFrame(TimeSpan _)
    {
        if (!_recording)
            return;

        var nowMs = Now;
        var blank = BlankFraction();
        _frames.Add(new Frame(nowMs, blank));
        Volatile.Write(ref _lastFrameBlank, blank);
        Volatile.Write(ref _lastFrameMs, nowMs);
        _root.RequestAnimationFrame(OnFrame);
    }

    /// <summary>
    /// The share of the viewport covered by turns that show nothing yet: a turn whose place is held
    /// empty until it is built for the screen, or — in builds that load history on demand — an older
    /// exchange that is not read and built yet.
    /// </summary>
    private double BlankFraction()
    {
        var viewport = _scrollViewer.Viewport.Height;
        if (!(viewport > 0) || _panel.TranslatePoint(default, _scrollViewer) is not { } origin)
            return 0;

        var children = _panel.Children;
        int low = 0, high = children.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (origin.Y + children[middle].Bounds.Bottom <= 0)
                low = middle + 1;
            else
                high = middle;
        }

        var blank = 0d;
        for (var i = low; i < children.Count; i++)
        {
            var bounds = children[i].Bounds;
            var top = origin.Y + bounds.Top;
            if (top >= viewport)
                break;
            var shown = Math.Min(origin.Y + bounds.Bottom, viewport) - Math.Max(top, 0);
            if (shown > 0 && IsEmpty(children[i]))
                blank += shown;
        }

        return Math.Min(1, blank / viewport);
    }

    private static bool IsEmpty(Control container)
    {
        if ((container as ContentPresenter)?.Child is not TranscriptTurnControl control)
            return false;
        if (control.Content is null)
            return true;
        var items = control.Turn?.Items;
        return items is { Count: 1 } && items[0].GetType().Name == "HistoryPlaceholderItem";
    }

    private TouchpadResult Analyze(double gesturesStartMs)
    {
        // A gesture moves the content from its first handled report until the frame after its last.
        var windows = _wheels
            .GroupBy(static wheel => wheel.Gesture)
            .OrderBy(static group => group.Key)
            .Select(static group => (Start: group.Min(static wheel => wheel.HandledMs), End: group.Max(static wheel => wheel.HandledMs)))
            .ToList();

        // The display's frame interval: the usual gap between frames at rest. Where Windows composes
        // only when something changes (a Remote Desktop session), frames at rest come seldom, so the
        // usual gap while content moves counts too. Two callbacks in one pass are one frame.
        var idle = new List<double>();
        var moving = new List<double>();
        for (int i = 1, w = 0; i < _frames.Count; i++)
        {
            var interval = _frames[i].Ms - _frames[i - 1].Ms;
            if (interval < 2)
                continue;
            if (_frames[i].Ms < gesturesStartMs)
            {
                idle.Add(interval);
                continue;
            }

            while (w < windows.Count && _frames[i].Ms > windows[w].End)
                w++;
            if (w < windows.Count && _frames[i - 1].Ms >= windows[w].Start)
                moving.Add(interval);
        }

        var frameMs = Math.Min(
            idle.Count > 0 ? Percentile(idle, 0.5) : double.MaxValue,
            moving.Count > 0 ? Percentile(moving, 0.25) : double.MaxValue);
        if (frameMs == double.MaxValue)
            frameMs = 1000d / 60;

        var intervals = new List<double>();
        var dropped = 0;
        var hitchMs = 0d;
        var motionMs = 0d;
        var blankMs = 0d;
        var mostlyBlankMs = 0d;
        var window = 0;
        for (var i = 1; i < _frames.Count && window < windows.Count; i++)
        {
            var frame = _frames[i];
            while (window < windows.Count && frame.Ms > windows[window].End + frameMs)
                window++;
            if (window >= windows.Count || frame.Ms <= windows[window].Start)
                continue;

            var interval = frame.Ms - _frames[i - 1].Ms;
            intervals.Add(interval);
            dropped += Math.Max(0, (int)Math.Round(interval / frameMs) - 1);
            if (interval > frameMs * 1.5)
                hitchMs += interval - frameMs;
            motionMs += interval;
            blankMs += interval * frame.Blank;
            if (frame.Blank > 0.25)
                mostlyBlankMs += interval;
        }

        // After each gesture: how much was still empty when it stopped, and how long until it showed.
        var fills = new List<double>();
        var blankAtStop = new List<double>();
        var unfilled = 0;
        var lastFrameMs = _frames.Count > 0 ? _frames[^1].Ms : 0;
        for (var g = 0; g < windows.Count; g++)
        {
            var stopMs = windows[g].End;
            var nextMs = g + 1 < windows.Count ? windows[g + 1].Start : double.MaxValue;
            Frame? first = null;
            Frame? shown = null;
            foreach (var frame in _frames)
            {
                if (frame.Ms <= stopMs)
                    continue;
                if (frame.Ms >= nextMs)
                    break;
                first ??= frame;
                if (frame.Blank <= ShownBlankFraction)
                {
                    shown = frame;
                    break;
                }
            }

            if (first is { } stopped)
                blankAtStop.Add(stopped.Blank);
            if (shown is { } filled)
            {
                fills.Add(filled.Ms - stopMs);
            }
            else
            {
                unfilled++;
                fills.Add(Math.Min(nextMs, lastFrameMs) - stopMs);
            }
        }

        var delays = _wheels.Select(static wheel => wheel.HandledMs - wheel.SentMs).ToList();
        return new TouchpadResult(
            Gestures: windows.Count,
            Reports: _wheels.Count,
            DistancePx: _wheels.Sum(static wheel => Math.Abs(wheel.Moved)),
            DurationMs: lastFrameMs - gesturesStartMs,
            FrameMs: frameMs,
            MotionFrames: intervals.Count,
            DroppedFrames: dropped,
            FrameP50Ms: Percentile(intervals, 0.50),
            FrameP95Ms: Percentile(intervals, 0.95),
            FrameP99Ms: Percentile(intervals, 0.99),
            FrameMaxMs: intervals.Count > 0 ? intervals.Max() : 0,
            Frames50Ms: intervals.Count(static interval => interval > 50),
            Frames100Ms: intervals.Count(static interval => interval > 100),
            HitchMs: hitchMs,
            InputP50Ms: Percentile(delays, 0.50),
            InputP95Ms: Percentile(delays, 0.95),
            InputP99Ms: Percentile(delays, 0.99),
            InputMaxMs: delays.Count > 0 ? delays.Max() : 0,
            BlankPct: motionMs > 0 ? 100 * blankMs / motionMs : 0,
            MostlyBlankPct: motionMs > 0 ? 100 * mostlyBlankMs / motionMs : 0,
            BlankAtStopPct: 100 * Percentile(blankAtStop, 0.5),
            FillP50Ms: Percentile(fills, 0.50),
            FillP90Ms: Percentile(fills, 0.90),
            FillMaxMs: fills.Count > 0 ? fills.Max() : 0,
            Unfilled: unfilled,
            ReachedEdge: Volatile.Read(ref _atEdge) != 0);
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(static value => value).ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private readonly record struct Wheel(int Gesture, double SentMs, double Pixels);

    private readonly record struct WheelRecord(int Gesture, double SentMs, double HandledMs, double Moved);

    private readonly record struct Frame(double Ms, double Blank);
}

/// <summary>What one touchpad scroll measured; frame figures cover the frames while content moved.</summary>
internal sealed record TouchpadResult(
    int Gestures,
    int Reports,
    double DistancePx,
    double DurationMs,
    double FrameMs,
    int MotionFrames,
    int DroppedFrames,
    double FrameP50Ms,
    double FrameP95Ms,
    double FrameP99Ms,
    double FrameMaxMs,
    int Frames50Ms,
    int Frames100Ms,
    double HitchMs,
    double InputP50Ms,
    double InputP95Ms,
    double InputP99Ms,
    double InputMaxMs,
    double BlankPct,
    double MostlyBlankPct,
    double BlankAtStopPct,
    double FillP50Ms,
    double FillP90Ms,
    double FillMaxMs,
    int Unfilled,
    bool ReachedEdge)
{
    /// <summary>Frames shown on time while content moved, out of the frames the display could show.</summary>
    public double SmoothPct => MotionFrames + DroppedFrames > 0 ? 100d * MotionFrames / (MotionFrames + DroppedFrames) : 100;

    /// <summary>
    /// Under 30 frames a second both at rest and while content moves: Windows is not composing the
    /// display (the session is locked or disconnected, or the display is off), so frame figures say
    /// nothing about smoothness.
    /// </summary>
    public bool Throttled => FrameMs > 34;

    public string Describe()
        => (Throttled ? "THROTTLED (display not composing; frame figures invalid) " : "") +
           $"gestures={Gestures} distance={DistancePx:0}px frame={FrameMs:0.0}ms smooth={SmoothPct:0.0}% " +
           $"dropped={DroppedFrames} >50ms={Frames50Ms} >100ms={Frames100Ms} hitch={HitchMs:0}ms " +
           $"frame p95={FrameP95Ms:0.0} max={FrameMaxMs:0.0}ms input p95={InputP95Ms:0.0} max={InputMaxMs:0.0}ms " +
           $"blank={BlankPct:0.0}% (>25%: {MostlyBlankPct:0.0}%) at-stop={BlankAtStopPct:0.0}% " +
           $"fill p50={FillP50Ms:0} p90={FillP90Ms:0} max={FillMaxMs:0}ms unfilled={Unfilled}";

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["gestures"] = Gestures,
        ["reports"] = Reports,
        ["reachedEdge"] = ReachedEdge,
        ["throttled"] = Throttled,
        ["distancePx"] = Math.Round(DistancePx),
        ["durationMs"] = Math.Round(DurationMs),
        ["frameMs"] = Math.Round(FrameMs, 2),
        ["motionFrames"] = MotionFrames,
        ["droppedFrames"] = DroppedFrames,
        ["smoothPct"] = Math.Round(SmoothPct, 1),
        ["frameP50Ms"] = Math.Round(FrameP50Ms, 1),
        ["frameP95Ms"] = Math.Round(FrameP95Ms, 1),
        ["frameP99Ms"] = Math.Round(FrameP99Ms, 1),
        ["frameMaxMs"] = Math.Round(FrameMaxMs, 1),
        ["frames50Ms"] = Frames50Ms,
        ["frames100Ms"] = Frames100Ms,
        ["hitchMs"] = Math.Round(HitchMs),
        ["inputP50Ms"] = Math.Round(InputP50Ms, 1),
        ["inputP95Ms"] = Math.Round(InputP95Ms, 1),
        ["inputP99Ms"] = Math.Round(InputP99Ms, 1),
        ["inputMaxMs"] = Math.Round(InputMaxMs, 1),
        ["blankPct"] = Math.Round(BlankPct, 1),
        ["mostlyBlankPct"] = Math.Round(MostlyBlankPct, 1),
        ["blankAtStopPct"] = Math.Round(BlankAtStopPct, 1),
        ["fillP50Ms"] = Math.Round(FillP50Ms),
        ["fillP90Ms"] = Math.Round(FillP90Ms),
        ["fillMaxMs"] = Math.Round(FillMaxMs),
        ["unfilled"] = Unfilled,
    };
}
#endif
