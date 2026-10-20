namespace Lumi.Tests;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> for cadence tests: time only moves when
/// <see cref="Advance"/> is called, and due timers fire synchronously inside that call.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return _now.UtcTicks;
    }

    public int PendingTimerCount
    {
        get
        {
            lock (_gate)
                return _timers.Count(static timer => timer.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        DateTimeOffset target;
        lock (_gate)
            target = _now + amount;

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(timer => timer.DueAt is { } due && due <= target)
                    .OrderBy(static timer => timer.DueAt)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.DueAt!.Value;
                next.DueAt = next.Period is { } period && period > TimeSpan.Zero
                    ? _now + period
                    : null;
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
