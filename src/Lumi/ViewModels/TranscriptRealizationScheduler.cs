using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace Lumi.ViewModels;

public readonly record struct TranscriptRealizationDiagnosticsSnapshot(
    int RealizeCount,
    int DrainCount,
    int MaxBatchSize);

/// <summary>
/// Spreads the cost of materialising mounted transcript turns across several UI frames instead of
/// one giant synchronous layout pass. When the user switches to a large chat, every mounted
/// <see cref="TranscriptTurnControl"/> re-enters the visual tree at once; measuring all of their
/// (retained, already-parsed) subtrees in a single pass freezes the UI thread for hundreds of ms.
///
/// Each control reserves its known height and asks the scheduler to realize it. The scheduler
/// drains the queue newest-first (controls attach top→bottom, so the tail is the bottom / most
/// recently scrolled-to content the user actually looks at) under a per-frame time budget, yielding
/// to the dispatcher between batches. The total work is unchanged, but it never blocks the thread
/// long enough to feel like a freeze — the switch stays responsive and content fills in bottom-first.
/// UI-thread affine; all members must be touched from the dispatcher thread.
/// </summary>
internal sealed class TranscriptRealizationScheduler
{
    public static TranscriptRealizationScheduler Instance { get; } = new();

    private readonly List<TranscriptTurnControl> _pending = [];
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
    /// True while one or more mounted turns are still queued for deferred realization. The chat
    /// surface uses this to keep its loading overlay up (and absorbing clicks) until the freshly
    /// opened transcript has actually been measured, instead of revealing a blank / still-settling
    /// transcript the instant the placeholders are mounted. UI-thread affine.
    /// </summary>
    public bool HasPendingWork => _pending.Count > 0;

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
        // Move-to-tail so the most recently attached control (bottom of the transcript) is realized
        // first on the next drain.
        _pending.Remove(control);
        _pending.Add(control);

        var size = _pending.Count;
        if (size > Volatile.Read(ref _maxBatchSize))
            Interlocked.Exchange(ref _maxBatchSize, size);

        QueueDrain();
    }

    public void Cancel(TranscriptTurnControl control) => _pending.Remove(control);

    /// <summary>Realizes a specific pending control immediately (e.g. before scrolling to it).</summary>
    public void FlushControl(TranscriptTurnControl control)
    {
        if (_pending.Remove(control))
            RealizeOne(control);
    }

    /// <summary>Realizes every queued control synchronously. Used by tests and forced jumps.</summary>
    public void FlushAll()
    {
        while (_pending.Count > 0)
        {
            var index = _pending.Count - 1;
            var control = _pending[index];
            _pending.RemoveAt(index);
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
        while (_pending.Count > 0)
        {
            var index = _pending.Count - 1;
            var control = _pending[index];
            _pending.RemoveAt(index);
            RealizeOne(control);

            if (stopwatch.Elapsed.TotalMilliseconds >= FrameBudgetMs && _pending.Count > 0)
            {
                QueueDrain();
                return;
            }
        }
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
