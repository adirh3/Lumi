using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Lumi.ViewModels;

internal sealed class UiThrottler(
    Action action,
    TimeSpan minimumInterval,
    DispatcherPriority priority,
    Action<Exception>? onError = null) : IDisposable
{
    private readonly Action _action = action;
    private readonly TimeSpan _minimumInterval = minimumInterval < TimeSpan.Zero ? TimeSpan.Zero : minimumInterval;
    private readonly DispatcherPriority _priority = priority;
    private readonly Action<Exception>? _onError = onError;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _scheduled;
    private DateTime _lastRunUtc;
    private CancellationTokenSource? _delayCts;

    public UiThrottler(Action action, TimeSpan minimumInterval)
        : this(action, minimumInterval, DispatcherPriority.Normal)
    {
    }

    public void Request(bool immediate = false)
    {
        CancellationToken token;
        TimeSpan delay;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (_scheduled && !immediate)
                return;

            if (_scheduled)
            {
                _delayCts?.Cancel();
                _delayCts?.Dispose();
                _delayCts = null;
            }

            _scheduled = true;
            delay = immediate ? TimeSpan.Zero : GetDelayNoLock();
            var cts = new CancellationTokenSource();
            _delayCts = cts;
            token = cts.Token;
        }

        _ = ScheduleAsync(delay, token);
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            _scheduled = false;
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _scheduled = false;
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = null;
        }
    }

    private TimeSpan GetDelayNoLock()
    {
        if (_lastRunUtc == default)
            return TimeSpan.Zero;

        var remaining = _minimumInterval - (DateTime.UtcNow - _lastRunUtc);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private async Task ScheduleAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return;

            Dispatcher.UIThread.Post(() => Flush(token), _priority);
        }
        catch (OperationCanceledException)
        {
            // Token was cancelled — the caller has already scheduled a replacement.
        }
        catch (Exception ex)
        {
            // Unexpected error — reset _scheduled so future requests aren't permanently blocked.
            lock (_gate)
            {
                _scheduled = false;
            }

            Trace.TraceError("UiThrottler scheduling failed: {0}", ex);
        }
    }

    private void Flush(CancellationToken token)
    {
        lock (_gate)
        {
            if (_disposed || token.IsCancellationRequested || !_scheduled)
                return;

            _scheduled = false;
            _delayCts?.Dispose();
            _delayCts = null;
            _lastRunUtc = DateTime.UtcNow;
        }

        try
        {
            _action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UiThrottler action failed: {ex}");
            _onError?.Invoke(ex);
        }
    }
}
