using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Client-side, per-<see cref="ByokModel"/> requests-per-minute (RPM) rate limiter for BYOK
/// providers. Prevents a single model from exceeding its configured RPM budget, which is the
/// most common cause of provider 429s (CAPI 729) when a user fires many turns quickly.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> v1 tracks RPM only (no token estimation). Each <see cref="ByokModel.Id"/>
/// gets its own independent sliding window — there is no shared quota across models that point
/// at the same endpoint+key. That is an accepted limitation for a personal app; raising it would
/// require key-level accounting.</para>
/// <para><b>Default is a no-op.</b> When <see cref="ByokModel.MaxRequestsPerMinute"/> is
/// <c>null</c> or <c>&lt;= 0</c>, <see cref="AcquireSendSlotAsync"/> returns immediately without
/// allocating a limiter entry and <see cref="IsRateLimited"/> stays <c>false</c>. This means a
/// user who never opens the Advanced section sees zero behavioral change — the limiter is purely
/// opt-in, exactly like the rest of the BYOK advanced settings.</para>
/// <para><b>Threading.</b> All state is guarded by a single lock; the blocking path uses a
/// <see cref="SemaphoreSlim"/>-free <see cref="Task.Delay"/> loop so cancellation unwinds
/// cleanly without orphaned waiters. The limiter is safe to call from the UI thread because
/// the only <c>await</c> points are on <see cref="Task.Delay(CancellationToken)"/>.</para>
/// </remarks>
public sealed class ByokRateLimiter
{
    private readonly Dictionary<string, LimiterState> _limiters = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;

    /// <summary>
    /// Constructs the limiter. Production callers should use the default constructor (real wall
    /// clock, 60-second window). Tests inject <see cref="TimeProvider"/> and a shorter window via
    /// <see cref="ByokRateLimiter(TimeProvider, TimeSpan?)"/> so the sliding window is
    /// deterministic without long real sleeps.
    /// </summary>
    public ByokRateLimiter() : this(TimeProvider.System, null) { }

    /// <summary>Test hook: inject a fake clock and an optional shorter window.</summary>
    internal ByokRateLimiter(TimeProvider timeProvider, TimeSpan? window = null)
    {
        _timeProvider = timeProvider;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// True when <paramref name="modelId"/> currently has a positive RPM limit configured and
    /// is therefore subject to throttling. UI uses this to decide whether to show a
    /// "rate-limited / waiting" hint. Always <c>false</c> for models without a configured limit
    /// (the default no-op case).
    /// </summary>
    public bool IsRateLimited(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return false;

        lock (_lock)
        {
            return _limiters.TryGetValue(modelId!, out var state) && state.Limit > 0;
        }
    }

    /// <summary>
    /// Acquires a send slot for the given model, blocking until a slot is free under the model's
    /// configured RPM window. When <paramref name="limit"/> is <c>null</c> or <c>&lt;= 0</c>, this
    /// is a pure no-op: no state is touched, no allocation happens, and the method returns
    /// synchronously — preserving current behavior for users who never configure a limit.
    /// </summary>
    /// <param name="modelId">The <see cref="ByokModel.Id"/> to throttle under. Callers should
    /// pass the resolved BYOK model id; non-BYOK/null ids are always a no-op.</param>
    /// <param name="limit">The RPM limit from <see cref="ByokModel.MaxRequestsPerMinute"/>.</param>
    /// <param name="cancellationToken">Propagated to every <see cref="Task.Delay"/> wait so a
    /// cancelled send unblocks promptly instead of waiting out the window.</param>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/>
    /// is cancelled while waiting for a slot.</exception>
    public async Task AcquireSendSlotAsync(string? modelId, int? limit, CancellationToken cancellationToken)
    {
        // No-op fast path: this is the DEFAULT case for every model that hasn't opted in.
        // Keeping it allocation-free and lock-free matters because the hot send path runs here
        // for every single message, including the overwhelming majority that have no limit.
        if (string.IsNullOrEmpty(modelId) || limit is null || limit <= 0)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        var nowUtc = _timeProvider.GetUtcNow();

        // Spin-wait on the window with short sleeps. The short delay bounds how often we
        // re-check, while still letting cancellation tear through immediately. We don't use
        // SemaphoreSlim here because the rate limit is time-windowed, not a fixed concurrency
        // pool: a semaphore would block indefinitely without the timestamp eviction logic, and
        // wiring up manual releases on window expiry is more fragile than this simple loop.
        while (true)
        {
            TimeSpan? waitTime;
            lock (_lock)
            {
                var state = GetOrCreateState(modelId!);
                state.Limit = limit.Value;
                EvictExpired(state, nowUtc);

                if (state.RecentSends.Count < limit.Value)
                {
                    state.RecentSends.Add(nowUtc);
                    return; // slot acquired
                }

                // Window full: compute how long until the oldest send falls out of the window.
                // That's the minimum wait before a slot can open. Add a tiny epsilon so we don't
                // busy-loop on the exact boundary.
                var oldest = state.RecentSends[0];
                var windowEnd = oldest.Add(_window);
                waitTime = windowEnd - nowUtc;
                if (waitTime <= TimeSpan.Zero)
                    waitTime = MinRecheckDelay;
                else if (waitTime > MaxRecheckDelay)
                    waitTime = MaxRecheckDelay;
            }

            // Wait outside the lock so other models aren't blocked by this one's wait.
            await Task.Delay(waitTime.Value, cancellationToken).ConfigureAwait(false);
            nowUtc = _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Removes any cached limiter state for <paramref name="modelId"/>. Safe to call when the
    /// model never had a limit (no-op). Used when a model is deleted or its RPM limit is cleared
    /// so the dictionary doesn't grow unbounded over the app lifetime.
    /// </summary>
    public void Clear(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
            return;

        lock (_lock)
        {
            _limiters.Remove(modelId!);
        }
    }

    private LimiterState GetOrCreateState(string modelId)
    {
        // Caller holds _lock.
        if (!_limiters.TryGetValue(modelId, out var state))
        {
            state = new LimiterState();
            _limiters[modelId] = state;
        }
        return state;
    }

    private void EvictExpired(LimiterState state, DateTimeOffset nowUtc)
    {
        // Caller holds _lock. Remove every timestamp older than the sliding window.
        // List is kept in insertion order (oldest first), so we can trim from the head.
        var i = 0;
        for (; i < state.RecentSends.Count; i++)
        {
            if (nowUtc - state.RecentSends[i] < _window)
                break;
        }

        if (i > 0)
            state.RecentSends.RemoveRange(0, i);
    }

    private sealed class LimiterState
    {
        /// <summary>Cached RPM limit last seen for this model. Re-synced every acquire.</summary>
        public int Limit;

        /// <summary>Timestamps of sends inside the current 60s window, oldest first.</summary>
        public List<DateTimeOffset> RecentSends { get; } = new();
    }

    private static readonly TimeSpan MinRecheckDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxRecheckDelay = TimeSpan.FromMilliseconds(250);
}
