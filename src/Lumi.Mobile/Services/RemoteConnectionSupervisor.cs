namespace Lumi.Mobile.Services;

/// <summary>Tuning for <see cref="RemoteConnectionSupervisor"/>. Tests shorten or freeze these.</summary>
public sealed class RemoteConnectionOptions
{
    public static RemoteConnectionOptions Default { get; } = new();

    /// <summary>
    /// How long a backgrounded app keeps its quiet stream, so a quick switch (file picker, share
    /// sheet, a glance at another app) resumes without a reconnect. Zero disconnects immediately.
    /// </summary>
    public TimeSpan BackgroundGrace { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>After this long without reaching Lumi, stop retrying until something changes.</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a resumed stream may stay silent before it is replaced.</summary>
    public TimeSpan ResumeProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A network change restarts a live stream only if it has been silent this long.</summary>
    public TimeSpan StaleStreamAfterNetworkChange { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Random spread applied to retry pauses (0.2 = ±20%).</summary>
    public double Jitter { get; init; } = 0.2;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Uniform [0, 1) source for jitter.</summary>
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    /// <summary>Null uses <see cref="MobilePlatformServices.NetworkMonitor"/>.</summary>
    public IRemoteNetworkMonitor? Network { get; init; }
}

/// <summary>
/// The single owner of how hard the phone works to stay connected: retry pacing for the handshake
/// and the live stream, giving up while the PC is unreachable, retrying immediately when the
/// network returns or the user asks, and the background grace period that keeps a quiet stream
/// through short app switches.
/// </summary>
public sealed class RemoteConnectionSupervisor : IRemoteReconnectPolicy, IDisposable
{
    private readonly object _gate = new();
    private readonly RemoteConnectionOptions _options;
    private readonly IRemoteNetworkMonitor _network;
    private TaskCompletionSource _trigger = NewTrigger();
    private DateTimeOffset? _failingSince;
    private bool _isOffline;
    private ITimer? _graceTimer;
    private DateTimeOffset? _backgroundSince;
    private long _graceGeneration;
    private bool _disposed;

    public RemoteConnectionSupervisor(RemoteConnectionOptions? options = null)
    {
        _options = options ?? RemoteConnectionOptions.Default;
        _network = _options.Network ?? MobilePlatformServices.NetworkMonitor;
        _network.NetworkChanged += OnNetworkChanged;
    }

    public TimeProvider Time => _options.Time;

    public TimeSpan BackgroundGrace => _options.BackgroundGrace;

    public TimeSpan ResumeProbeTimeout => _options.ResumeProbeTimeout;

    public TimeSpan StaleStreamAfterNetworkChange => _options.StaleStreamAfterNetworkChange;

    public bool IsNetworkAvailable => _network.IsNetworkAvailable;

    /// <summary>True while retrying is suspended until the network changes, the app resumes or Retry.</summary>
    public bool IsOffline
    {
        get
        {
            lock (_gate)
                return _isOffline;
        }
    }

    public bool IsInBackgroundGrace
    {
        get
        {
            lock (_gate)
                return _backgroundSince is not null;
        }
    }

    public event Action? OfflineChanged;

    /// <summary>Raised after the network becomes available or moves; any wait has already ended.</summary>
    public event Action? NetworkRestored;

    public TimeSpan? NextDelay(int consecutiveFailures)
    {
        bool changed;
        TimeSpan? delay;
        lock (_gate)
        {
            var now = _options.Time.GetUtcNow();
            _failingSince ??= now;
            if (!_network.IsNetworkAvailable || now - _failingSince.Value >= _options.GiveUpAfter)
            {
                changed = SetOfflineLocked(true);
                delay = null;
            }
            else
            {
                changed = false;
                var exponent = Math.Clamp(consecutiveFailures - 1, 0, 16);
                var pause = Math.Min(
                    _options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent),
                    _options.MaxRetryDelay.TotalMilliseconds);
                var spread = 1 + _options.Jitter * (2 * _options.Random() - 1);
                delay = TimeSpan.FromMilliseconds(Math.Max(0, pause * spread));
            }
        }

        if (changed)
            OfflineChanged?.Invoke();
        return delay;
    }

    public async Task WaitAsync(TimeSpan? delay, CancellationToken cancellationToken)
    {
        Task trigger;
        lock (_gate)
            trigger = _trigger.Task;

        if (delay is not { } pause)
        {
            await trigger.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var elapsed = Task.Delay(pause, _options.Time, timer.Token);
        await Task.WhenAny(elapsed, trigger).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void OnConnected()
    {
        bool changed;
        lock (_gate)
        {
            _failingSince = null;
            changed = SetOfflineLocked(false);
        }

        if (changed)
            OfflineChanged?.Invoke();
    }

    /// <summary>Ends any retry pause or offline wait now and starts a fresh failure streak.</summary>
    public void RetryNow()
    {
        TaskCompletionSource fired;
        bool changed;
        lock (_gate)
        {
            _failingSince = null;
            changed = SetOfflineLocked(false);
            fired = _trigger;
            _trigger = NewTrigger();
        }

        fired.TrySetResult();
        if (changed)
            OfflineChanged?.Invoke();
    }

    /// <summary>
    /// Starts the background grace period. <paramref name="onExpired"/> runs if it ends before the
    /// app returns; a later <see cref="EndBackgroundGrace"/> cancels it.
    /// </summary>
    public void BeginBackgroundGrace(Func<Task> onExpired)
    {
        ArgumentNullException.ThrowIfNull(onExpired);
        lock (_gate)
        {
            if (_disposed)
                return;

            _graceTimer?.Dispose();
            _backgroundSince = _options.Time.GetUtcNow();
            var generation = ++_graceGeneration;
            _graceTimer = _options.Time.CreateTimer(
                _ =>
                {
                    if (TryExpireGrace(generation))
                        _ = onExpired();
                },
                null,
                _options.BackgroundGrace,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Ends the grace period because the app is back. True when it returned inside the window, so
    /// the kept stream can be resumed; false when there was none or it already ran out (even if a
    /// frozen process never got to run the timer).
    /// </summary>
    public bool EndBackgroundGrace()
    {
        lock (_gate)
        {
            var since = _backgroundSince;
            _backgroundSince = null;
            _graceGeneration++;
            _graceTimer?.Dispose();
            _graceTimer = null;
            return since is { } started
                   && _options.Time.GetUtcNow() - started < _options.BackgroundGrace;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _graceTimer?.Dispose();
            _graceTimer = null;
            _backgroundSince = null;
        }

        _network.NetworkChanged -= OnNetworkChanged;
    }

    private bool TryExpireGrace(long generation)
    {
        lock (_gate)
        {
            if (generation != _graceGeneration || _backgroundSince is null)
                return false;

            _backgroundSince = null;
            _graceTimer?.Dispose();
            _graceTimer = null;
            return true;
        }
    }

    private void OnNetworkChanged()
    {
        if (!_network.IsNetworkAvailable)
            return;

        RetryNow();
        NetworkRestored?.Invoke();
    }

    private bool SetOfflineLocked(bool value)
    {
        if (_isOffline == value)
            return false;

        _isOffline = value;
        return true;
    }

    private static TaskCompletionSource NewTrigger() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
