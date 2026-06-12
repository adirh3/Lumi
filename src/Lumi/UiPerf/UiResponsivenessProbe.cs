#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Lumi.UiPerf;

/// <summary>
/// Continuously measures UI-thread responsiveness by posting low-priority callbacks to the
/// Avalonia dispatcher and recording how long each waits before it runs. A large wait means the
/// UI thread is saturated (layout, rendering, data binding, the workload under test) and would
/// not promptly service user input or repaint — i.e. the app feels laggy or frozen right then.
/// </summary>
internal sealed class UiResponsivenessProbe : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<LatencySample> _samples = new();
    private readonly object _gate = new();
    private readonly int _intervalMs;
    private readonly DispatcherPriority _priority;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UiResponsivenessProbe(int intervalMs = 8, DispatcherPriority? priority = null)
    {
        _intervalMs = Math.Clamp(intervalMs, 1, 200);
        // Background priority is below input and rendering, so a serviced probe means the UI
        // thread had nothing more urgent to do — a clean "the app is idle/responsive" signal.
        _priority = priority ?? DispatcherPriority.Background;
    }

    /// <summary>Monotonic clock (ms) shared with the harness for slicing samples into action windows.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public void Start()
    {
        if (_loop is not null)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SampleLoopAsync(_cts.Token));
    }

    /// <summary>Latency samples whose timestamps fall within [startMs, endMs].</summary>
    public IReadOnlyList<double> LatenciesInWindow(double startMs, double endMs)
    {
        var result = new List<double>();
        lock (_gate)
        {
            foreach (var sample in _samples)
            {
                if (sample.TimestampMs >= startMs && sample.TimestampMs <= endMs)
                    result.Add(sample.LatencyMs);
            }
        }

        return result;
    }

    public int SampleCount
    {
        get { lock (_gate) return _samples.Count; }
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var postedAt = NowMs;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var ranAt = NowMs;
                    lock (_gate)
                        _samples.Add(new LatencySample(ranAt, ranAt - postedAt));
                    tcs.TrySetResult();
                }, _priority);
            }
            catch
            {
                break;
            }

            try
            {
                // Wait until the probe is serviced (a long freeze yields one large sample once
                // the UI thread frees up), then pace the next sample.
                await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(_intervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        try { _loop?.Wait(750); } catch { /* ignore shutdown races */ }
        try { _cts?.Dispose(); } catch { /* already disposed */ }
    }
}
#endif
