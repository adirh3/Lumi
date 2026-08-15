using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.ViewModels;

public readonly record struct TranscriptRealizationDiagnosticsSnapshot(
    int RealizeCount,
    int DrainCount,
    int MaxBatchSize);

/// <summary>
/// Spreads the cost of materialising viewport-active transcript turns across several UI frames instead
/// of one giant synchronous layout pass. Stable offscreen turns remain lightweight height placeholders;
/// only the stopped viewport and its small cache request their heavy retained subtrees.
///
/// Each control reserves its local height and asks the scheduler to realize it. Within one window,
/// controls drain newest-first (controls attach top→bottom, so the tail is the newest request).
/// Across windows, queues rotate round-robin so one streaming transcript cannot starve another.
/// Work remains frame-budgeted and yields to the dispatcher between batches.
/// UI-thread affine; all members must be touched from the dispatcher thread.
/// </summary>
internal sealed class TranscriptRealizationScheduler
{
    private const int MaxRealizationsPerDrain = 1;
    private const int MaxNewestFirstStreak = 8;

    public static TranscriptRealizationScheduler Instance { get; } = new();

    private readonly Dictionary<object, List<TranscriptTurnControl>> _pendingByOwner =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TranscriptTurnControl, object> _ownerByControl =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _newestStreakByOwner =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _ownerOrder = [];
    private int _nextOwnerIndex;
    private bool _drainQueued;

    private static int _realizeCount;
    private static int _drainCount;
    private static int _maxBatchSize;

    /// <summary>
    /// Soft per-frame budget. The scheduler realizes turns until this elapses, then yields. A single
    /// turn whose measure exceeds the budget is still realized atomically (layout can't be split), so
    /// the worst hitch is bounded by the heaviest single turn rather than the whole window.
    /// </summary>
    public double FrameBudgetMs { get; set; } = 12d;

    /// <summary>
    /// True while one or more viewport-active turns are still queued for deferred realization. The chat
    /// surface uses this to keep its loading overlay up (and absorbing clicks) until the freshly
    /// opened transcript has actually been measured, instead of revealing a blank / still-settling
    /// transcript the instant the placeholders are mounted. UI-thread affine.
    /// </summary>
    public bool HasPendingWork => _ownerByControl.Count > 0;

    public static TranscriptRealizationDiagnosticsSnapshot CaptureDiagnostics() => new(
        Volatile.Read(ref _realizeCount),
        Volatile.Read(ref _drainCount),
        Volatile.Read(ref _maxBatchSize));

    public static void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _realizeCount, 0);
        Interlocked.Exchange(ref _drainCount, 0);
        Interlocked.Exchange(ref _maxBatchSize, 0);
    }

    public void Request(TranscriptTurnControl control)
    {
        VerifyUiThread();
        RemovePending(control);

        var owner = (object?)TopLevel.GetTopLevel(control) ?? control;
        if (!_pendingByOwner.TryGetValue(owner, out var pending))
        {
            pending = [];
            _pendingByOwner.Add(owner, pending);
            _newestStreakByOwner.Add(owner, 0);
            _ownerOrder.Add(owner);
        }

        // Move-to-tail within this window so its most recently attached (bottom-most) turn realizes
        // first, while TakeNextPending rotates between different window queues.
        pending.Add(control);
        _ownerByControl.Add(control, owner);

        var size = _ownerByControl.Count;
        if (size > Volatile.Read(ref _maxBatchSize))
            Interlocked.Exchange(ref _maxBatchSize, size);

        QueueDrain();
    }

    public void Cancel(TranscriptTurnControl control)
    {
        VerifyUiThread();
        RemovePending(control);
    }

    /// <summary>Realizes a specific pending control immediately (e.g. before scrolling to it).</summary>
    public void FlushControl(TranscriptTurnControl control)
    {
        VerifyUiThread();
        if (RemovePending(control))
            RealizeOne(control);
    }

    /// <summary>Realizes every queued control synchronously. Used by tests and forced jumps.</summary>
    public void FlushAll()
    {
        VerifyUiThread();
        while (TakeNextPending() is { } control)
        {
            RealizeOne(control);
        }
    }

    private void QueueDrain()
    {
        if (_drainQueued)
            return;

        _drainQueued = true;
        Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
    }

    private void Drain()
    {
        _drainQueued = false;
        Interlocked.Increment(ref _drainCount);

        var stopwatch = Stopwatch.StartNew();
        var realizedThisDrain = 0;
        while (TakeNextPending() is { } control)
        {
            RealizeOne(control);
            realizedThisDrain++;

            if ((realizedThisDrain >= MaxRealizationsPerDrain
                 || stopwatch.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                && HasPendingWork)
            {
                QueueDrain();
                return;
            }
        }
    }

    private TranscriptTurnControl? TakeNextPending()
    {
        while (_ownerOrder.Count > 0)
        {
            if (_nextOwnerIndex >= _ownerOrder.Count)
                _nextOwnerIndex = 0;

            var ownerIndex = _nextOwnerIndex;
            var owner = _ownerOrder[ownerIndex];
            var pending = _pendingByOwner[owner];
            if (pending.Count == 0)
            {
                RemoveOwnerAt(ownerIndex);
                continue;
            }

            var newestStreak = _newestStreakByOwner[owner];
            var controlIndex = pending.Count > 1 && newestStreak >= MaxNewestFirstStreak
                ? 0
                : pending.Count - 1;
            var control = pending[controlIndex];
            pending.RemoveAt(controlIndex);
            _ownerByControl.Remove(control);
            _newestStreakByOwner[owner] = controlIndex == 0 ? 0 : newestStreak + 1;

            if (pending.Count == 0)
            {
                RemoveOwnerAt(ownerIndex);
            }
            else
            {
                _nextOwnerIndex = (ownerIndex + 1) % _ownerOrder.Count;
            }

            return control;
        }

        return null;
    }

    private bool RemovePending(TranscriptTurnControl control)
    {
        if (!_ownerByControl.TryGetValue(control, out var owner)
            || !_pendingByOwner.TryGetValue(owner, out var pending))
        {
            return false;
        }

        var controlIndex = ReferenceIndexOf(pending, control);
        if (controlIndex < 0)
            return false;

        pending.RemoveAt(controlIndex);
        _ownerByControl.Remove(control);
        if (pending.Count == 0)
        {
            var ownerIndex = ReferenceIndexOf(_ownerOrder, owner);
            if (ownerIndex >= 0)
                RemoveOwnerAt(ownerIndex);
        }

        return true;
    }

    private void RemoveOwnerAt(int ownerIndex)
    {
        var owner = _ownerOrder[ownerIndex];
        _ownerOrder.RemoveAt(ownerIndex);
        _pendingByOwner.Remove(owner);
        _newestStreakByOwner.Remove(owner);

        if (_ownerOrder.Count == 0)
        {
            _nextOwnerIndex = 0;
            return;
        }

        if (ownerIndex < _nextOwnerIndex)
            _nextOwnerIndex--;
        if (_nextOwnerIndex >= _ownerOrder.Count)
            _nextOwnerIndex = 0;
    }

    private static int ReferenceIndexOf<T>(IReadOnlyList<T> items, T target)
        where T : class
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], target))
                return index;
        }

        return -1;
    }

    private static void VerifyUiThread()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("Transcript realization scheduling must run on the UI thread.");
    }

    private static void RealizeOne(TranscriptTurnControl control)
    {
        Interlocked.Increment(ref _realizeCount);
        try
        {
            control.RealizePendingHost();
        }
        catch (Exception ex)
        {
            // A single turn failing to realize must never abort the drain loop: that would leave every
            // other queued turn stranded as a blank placeholder (the loop wouldn't reschedule) and would
            // surface as an unhandled dispatcher exception in Release. Swallow + log so the rest of the
            // transcript still fills in; the offending turn stays a placeholder until it is re-requested.
            Debug.WriteLine($"[TranscriptRealizationScheduler] RealizePendingHost threw, skipping turn: {ex}");
        }
    }
}
