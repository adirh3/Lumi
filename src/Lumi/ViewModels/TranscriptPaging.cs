using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumi.ViewModels;

internal static class TranscriptLayoutMetrics
{
    public const double TurnSpacing = 8d;
    public const double MinimumEstimatedTurnHeight = 72d;
}

internal sealed class TranscriptPagingOptions
{
    public int MaxPageWeight { get; init; } = 34;
    public int MaxTurnsPerPage { get; init; } = 8;
    public int MinInitialPages { get; init; } = 2;
    public int MaxMountedPages { get; init; } = 6;
    public int TrimToMountedPages { get; init; } = 4;
    public int PrependBatchPageCount { get; init; } = 3;
    public double InitialViewportFillMultiplier { get; init; } = 1.6d;
    public double MountedViewportFillMultiplier { get; init; } = 2.1d;
    public double PrependTriggerPixels { get; init; } = 220d;
    public double RetainAboveViewportPixels { get; init; } = 320d;
    public double EstimatedPixelsPerWeightUnit { get; init; } = 56d;
    public bool EnableDiagnostics { get; init; }
}

internal static class TranscriptPageWeightEstimator
{
    public static int EstimateTurnWeight(TranscriptTurn turn)
    {
        if (turn.Items.Count == 0)
            return 1;

        var weight = 0;
        foreach (var item in turn.Items)
            weight += EstimateItemWeight(item);

        return Math.Max(1, weight);
    }

    public static double EstimateTurnHeight(TranscriptTurn turn, double pixelsPerWeightUnit)
    {
        if (turn.MeasuredHeight > 0)
            return turn.MeasuredHeight;

        return Math.Max(TranscriptLayoutMetrics.MinimumEstimatedTurnHeight, EstimateTurnWeight(turn) * pixelsPerWeightUnit);
    }

    private static int EstimateItemWeight(TranscriptItem item)
    {
        return item switch
        {
            AssistantMessageItem assistant => EstimateTextWeight(assistant.Content, 3),
            UserMessageItem user => EstimateTextWeight(user.Content, 2),
            JobWakeItem jobWake => Math.Max(5, EstimateTextWeight(jobWake.SearchText, 4)),
            ErrorMessageItem error => EstimateTextWeight(error.Content, 2),
            ReasoningItem reasoning => EstimateTextWeight(reasoning.Content, 4),
            SubagentToolCallItem subagent => Math.Max(
                4,
                2 + subagent.Activities.Count
                  + EstimateAdditionalTextWeight(subagent.TranscriptText)
                  + EstimateAdditionalTextWeight(subagent.ReasoningText)),
            SubagentGroupItem subagentGroup => Math.Max(
                4,
                2 + subagentGroup.Subagents.Sum(EstimateItemWeight)),
            ToolGroupItem toolGroup => Math.Max(4, 2 + toolGroup.ToolCalls.Count),
            QuestionItem => 3,
            PlanCardItem => 3,
            FileChangesSummaryItem => 4,
            SingleToolItem => 3,
            _ => 2,
        };
    }

    private static int EstimateAdditionalTextWeight(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return 1 + Math.Min(6, Math.Max(0, text.Length / 450));
    }

    private static int EstimateTextWeight(string? text, int baseWeight)
    {
        if (string.IsNullOrWhiteSpace(text))
            return baseWeight;

        return baseWeight + Math.Min(8, Math.Max(0, text.Length / 450));
    }
}

internal sealed class TranscriptPage
{
    public TranscriptPage(
        string pageId,
        int pageIndex,
        IReadOnlyList<TranscriptTurn> turns,
        int firstTurnIndex,
        int lastTurnIndex,
        int itemCount,
        int estimatedWeight)
    {
        PageId = pageId;
        PageIndex = pageIndex;
        Turns = turns;
        FirstTurnIndex = firstTurnIndex;
        LastTurnIndex = lastTurnIndex;
        ItemCount = itemCount;
        EstimatedWeight = estimatedWeight;
    }

    public string PageId { get; }
    public int PageIndex { get; }
    public IReadOnlyList<TranscriptTurn> Turns { get; }
    public int FirstTurnIndex { get; }
    public int LastTurnIndex { get; }
    public int TurnCount => Turns.Count;
    public int ItemCount { get; }
    public int EstimatedWeight { get; }

    public double GetMeasuredHeight(double fallbackPerWeight)
    {
        var total = 0d;
        for (var i = 0; i < Turns.Count; i++)
        {
            var turn = Turns[i];
            total += TranscriptPageWeightEstimator.EstimateTurnHeight(turn, fallbackPerWeight);
        }

        if (Turns.Count > 1)
            total += (Turns.Count - 1) * TranscriptLayoutMetrics.TurnSpacing;

        return total;
    }
}

internal enum TranscriptWindowMutationKind
{
    None,
    Reset,
    EnsureCoverage,
    Prepend,
    TrimHead,
    TailRestore,
}

internal readonly record struct TranscriptViewportState(
    double OffsetY,
    double ViewportHeight,
    double ExtentHeight,
    bool IsPinnedToBottom,
    double DistanceFromBottom);

internal readonly record struct TranscriptWindowMutation(
    TranscriptWindowMutationKind Kind,
    string Reason,
    int AddedPageCount,
    int RemovedPageCount,
    double EstimatedHeightDelta,
    bool RequiresAnchorRestore)
{
    public static TranscriptWindowMutation None { get; } = new(
        TranscriptWindowMutationKind.None,
        string.Empty,
        0,
        0,
        0,
        false);

    public bool HasChanges => Kind != TranscriptWindowMutationKind.None;
}

internal readonly record struct TranscriptWindowDiagnosticsSnapshot(
    int TotalTurnCount,
    int TotalItemCount,
    int TotalPageCount,
    int MountedPageCount,
    int MountedTurnCount,
    int MountedItemCount,
    bool IsPinnedToBottom,
    double DistanceFromBottom,
    int PageLoadCount,
    int PageUnloadCount,
    int PrependCount,
    int CleanupCount,
    int StreamingUpdateCount,
    double InitialLoadMilliseconds,
    double LastCompensationBeforeOffset,
    double LastCompensationAfterOffset,
    string MountedPageSummary);

internal sealed class TranscriptWindowController : ObservableObject, IDisposable
{
    internal const double DefaultInitialViewportHeight = 720d;

    private readonly TranscriptPagingOptions _options;
    private ObservableCollection<TranscriptTurn>? _sourceTurns;
    private readonly List<TranscriptPage> _pages = [];
    private int _firstMountedPageIndex = -1;
    private int _lastMountedPageIndex = -1;
    private bool _disposed;
    private string _diagnosticsText = string.Empty;
    private bool _isFollowingTail = true;
    private bool _isPinnedToBottom = true;
    private double _distanceFromBottom;
    private int _pageLoadCount;
    private int _pageUnloadCount;
    private int _prependCount;
    private int _cleanupCount;
    private int _streamingUpdateCount;
    private double _initialLoadMilliseconds;
    private double _lastCompensationBeforeOffset;
    private double _lastCompensationAfterOffset;

    public TranscriptWindowController(TranscriptPagingOptions? options = null)
    {
        _options = options ?? new TranscriptPagingOptions();
        MountedTurns = [];
        UpdateDiagnostics("init", "created");
    }

    public ObservableCollection<TranscriptTurn> MountedTurns { get; }

    public IReadOnlyList<TranscriptPage> Pages => _pages;

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public bool IsPinnedToBottom
    {
        get => _isPinnedToBottom;
        private set => SetProperty(ref _isPinnedToBottom, value);
    }

    public bool IsFollowingTail => _isFollowingTail;

    public double DistanceFromBottom
    {
        get => _distanceFromBottom;
        private set => SetProperty(ref _distanceFromBottom, value);
    }

    public void BindTranscript(ObservableCollection<TranscriptTurn> sourceTurns, string reason)
    {
        if (ReferenceEquals(_sourceTurns, sourceTurns))
        {
            RebuildPages();
            ClampMountedRange();
            TrimMountedTailOverflow();
            ReconcileMountedTurns(BuildDesiredMountedTurns());
            UpdateDiagnostics("bind", reason);
            return;
        }

        if (_sourceTurns is not null)
            _sourceTurns.CollectionChanged -= OnSourceTurnsCollectionChanged;

        _sourceTurns = sourceTurns;
        _sourceTurns.CollectionChanged += OnSourceTurnsCollectionChanged;
        RebuildPages();
        ClampMountedRange();
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("bind", reason);
    }

    public void Clear(string reason)
    {
        _pages.Clear();
        _firstMountedPageIndex = -1;
        _lastMountedPageIndex = -1;
        ReleaseAllMountedHosts();
        MountedTurns.Clear();
        UpdateDiagnostics("clear", reason);
    }

    /// <summary>
    /// Sheds the realized (built) Avalonia control subtrees for every mounted turn while keeping the
    /// paging/mount structure and the turn view-models intact. Used to release the heavy rendered
    /// transcript of a surface that is cached but no longer visible, so idle chats retain only their
    /// lightweight view-models instead of hundreds of live controls each. The hosts rebuild lazily
    /// from the turns' live items — through the normal frame-budgeted realization path — the next
    /// time the surface is attached to the visual tree, so switching back is not a blank transcript.
    /// Must be called on the UI thread (it mutates Avalonia controls).
    /// </summary>
    public void ReleaseRealizedHosts(string reason)
    {
        ReleaseAllMountedHosts();
        UpdateDiagnostics("release-hosts", reason);
    }

    public TranscriptWindowMutation ResetToLatest(double viewportHeight, string reason)
    {
        viewportHeight = SanitizeViewportHeight(viewportHeight);
        var stopwatch = Stopwatch.StartNew();
        _isFollowingTail = true;

        RebuildPages();
        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            ReleaseAllMountedHosts();
            MountedTurns.Clear();
            stopwatch.Stop();
            _initialLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            UpdateDiagnostics("reset", reason);
            return new TranscriptWindowMutation(TranscriptWindowMutationKind.Reset, reason, 0, 0, 0, false);
        }

        var estimatedHeight = 0d;
        var targetHeight = viewportHeight * _options.InitialViewportFillMultiplier;
        var firstIndex = _pages.Count - 1;

        while (firstIndex >= 0)
        {
            estimatedHeight += GetEffectivePageHeight(_pages[firstIndex]);
            var mountedPageCount = (_pages.Count - 1) - firstIndex + 1;
            if (mountedPageCount > 1)
                estimatedHeight += TranscriptLayoutMetrics.TurnSpacing;

            if (mountedPageCount >= _options.MinInitialPages && estimatedHeight >= targetHeight)
                break;

            if (mountedPageCount >= _options.MaxMountedPages || firstIndex == 0)
                break;

            firstIndex--;
        }

        _firstMountedPageIndex = Math.Max(0, firstIndex);
        _lastMountedPageIndex = _pages.Count - 1;
        var desiredTurns = BuildDesiredMountedTurns();
        var previousPageCount = MountedPageCount;
        ReconcileMountedTurns(desiredTurns);

        stopwatch.Stop();
        _initialLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var addedPages = Math.Max(0, MountedPageCount - previousPageCount);
        _pageLoadCount += addedPages;
        UpdateDiagnostics("reset", reason);
        return new TranscriptWindowMutation(TranscriptWindowMutationKind.Reset, reason, addedPages, 0, 0, false);
    }

    public TranscriptWindowMutation EnsureViewportCoverage(double viewportHeight, string reason, double? actualExtentHeight = null)
    {
        if (_pages.Count == 0 || _firstMountedPageIndex <= 0)
            return TranscriptWindowMutation.None;

        viewportHeight = SanitizeViewportHeight(viewportHeight);
        var targetHeight = viewportHeight * _options.MountedViewportFillMultiplier;
        var useActualExtentHeight = TrySanitizeExtentHeight(actualExtentHeight, out var currentHeight);
        if (!useActualExtentHeight)
            currentHeight = GetMountedHeight();

        if (currentHeight >= targetHeight)
            return TranscriptWindowMutation.None;

        var addedPages = 0;
        var estimatedDelta = 0d;
        while (_firstMountedPageIndex > 0 && MountedPageCount < _options.MaxMountedPages && currentHeight < targetHeight)
        {
            _firstMountedPageIndex--;
            var page = _pages[_firstMountedPageIndex];
            var addedHeight = (useActualExtentHeight
                ? GetConservativePageHeight(page)
                : GetEffectivePageHeight(page))
                + TranscriptLayoutMetrics.TurnSpacing;
            estimatedDelta += addedHeight;
            currentHeight += addedHeight;
            addedPages++;
        }

        if (addedPages == 0)
            return TranscriptWindowMutation.None;

        _pageLoadCount += addedPages;
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("coverage", reason);
        return new TranscriptWindowMutation(TranscriptWindowMutationKind.EnsureCoverage, reason, addedPages, 0, estimatedDelta, true);
    }

    public TranscriptWindowMutation UpdateViewport(TranscriptViewportState state, string reason)
        => UpdateViewport(state, state.IsPinnedToBottom, reason);

    public TranscriptWindowMutation UpdateViewport(
        TranscriptViewportState state,
        bool isFollowingTail,
        string reason)
    {
        UpdateScrollState(
            isFollowingTail,
            state.IsPinnedToBottom,
            state.DistanceFromBottom,
            $"viewport:{reason}");

        if (_pages.Count == 0 || MountedPageCount == 0)
            return TranscriptWindowMutation.None;

        if (state.OffsetY <= _options.PrependTriggerPixels && _firstMountedPageIndex > 0)
        {
            // Load a small chunk of older pages per near-top scroll. This gives the reader more
            // history above the anchor and avoids a render/layout cycle for every single page.
            // Keep at least the previously-first mounted page so anchor restoration can still land.
            var maxBatch = Math.Min(
                Math.Max(1, _options.PrependBatchPageCount),
                Math.Max(1, MountedPageCount - 1));
            var addedPages = 0;
            var estimatedDelta = 0d;
            while (_firstMountedPageIndex > 0 && addedPages < maxBatch)
            {
                _firstMountedPageIndex--;
                var page = _pages[_firstMountedPageIndex];
                estimatedDelta += GetEffectivePageHeight(page) + TranscriptLayoutMetrics.TurnSpacing;
                addedPages++;
            }

            if (addedPages == 0)
                return TranscriptWindowMutation.None;

            _prependCount += addedPages;
            _pageLoadCount += addedPages;
            var removedTailPages = TrimMountedTailOverflow();
            ReconcileMountedTurns(BuildDesiredMountedTurns());
            UpdateDiagnostics("prepend", reason);
            return new TranscriptWindowMutation(TranscriptWindowMutationKind.Prepend, reason, addedPages, removedTailPages, estimatedDelta, true);
        }

        if (MountedPageCount > _options.MaxMountedPages)
        {
            var removedPages = 0;
            var estimatedDelta = 0d;
            while (MountedPageCount - removedPages > _options.TrimToMountedPages && _firstMountedPageIndex + removedPages < _lastMountedPageIndex)
            {
                var page = _pages[_firstMountedPageIndex + removedPages];
                var pageHeight = GetEffectivePageHeight(page) + TranscriptLayoutMetrics.TurnSpacing;
                var remainingOffset = state.OffsetY - estimatedDelta;
                if (remainingOffset <= pageHeight + _options.RetainAboveViewportPixels)
                    break;

                estimatedDelta += pageHeight;
                removedPages++;
            }

            if (removedPages > 0)
            {
                _firstMountedPageIndex += removedPages;
                _cleanupCount++;
                _pageUnloadCount += removedPages;
                ReconcileMountedTurns(BuildDesiredMountedTurns());
                UpdateDiagnostics("cleanup", reason);
                return new TranscriptWindowMutation(TranscriptWindowMutationKind.TrimHead, reason, 0, removedPages, -estimatedDelta, true);
            }
        }

        return TranscriptWindowMutation.None;
    }

    public void RecordScrollCompensation(string reason, double beforeOffset, double afterOffset)
    {
        _lastCompensationBeforeOffset = beforeOffset;
        _lastCompensationAfterOffset = afterOffset;
        UpdateDiagnostics("compensate", reason);
    }

    public void UpdatePinnedState(bool isPinnedToBottom, double distanceFromBottom, string reason)
        => UpdateScrollState(isPinnedToBottom, isPinnedToBottom, distanceFromBottom, reason);

    public void UpdateScrollState(
        bool isFollowingTail,
        bool isPinnedToBottom,
        double distanceFromBottom,
        string reason)
    {
        var changed = _isFollowingTail != isFollowingTail || IsPinnedToBottom != isPinnedToBottom;
        _isFollowingTail = isFollowingTail;
        IsPinnedToBottom = isPinnedToBottom;
        DistanceFromBottom = distanceFromBottom;
        if (changed)
            UpdateDiagnostics("scroll-state", reason);
    }

    /// <summary>
    /// Ensures the mounted window includes the latest pages (tail-tracking).
    /// Call when the user sends a message after scrolling up, so the viewport
    /// snaps to the newest content. Trims head pages to stay within limits.
    /// </summary>
    public bool EnsureLatestMounted(string reason)
    {
        _isFollowingTail = true;

        if (_pages.Count == 0)
            return false;

        if (_lastMountedPageIndex >= _pages.Count - 1)
            return false;

        _lastMountedPageIndex = _pages.Count - 1;
        ClampMountedRange();
        TrimMountedHeadOverflow();
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("ensure-latest", reason);
        return true;
    }

    public TranscriptWindowMutation EnsureLatestMountedIfAdjacentTailGap(string reason)
    {
        if (_pages.Count == 0 || MountedPageCount == 0)
            return TranscriptWindowMutation.None;

        var latestPageIndex = _pages.Count - 1;
        if (_lastMountedPageIndex >= latestPageIndex)
            return TranscriptWindowMutation.None;

        if (_lastMountedPageIndex + 1 != latestPageIndex)
            return TranscriptWindowMutation.None;

        _lastMountedPageIndex = latestPageIndex;
        _pageLoadCount++;
        var removedPages = TrimMountedHeadOverflow();
        ClampMountedRange();

        var latestPage = _pages[latestPageIndex];
        var estimatedDelta = GetEffectivePageHeight(latestPage) + TranscriptLayoutMetrics.TurnSpacing;
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("tail-restore", reason);

        return new TranscriptWindowMutation(
            TranscriptWindowMutationKind.TailRestore,
            reason,
            1,
            removedPages,
            estimatedDelta,
            true);
    }

    /// <summary>
    /// Mounts the page containing the specified turn so it becomes part of MountedTurns.
    /// Returns true if the mounted range changed.
    /// </summary>
    public bool MountPageContainingTurn(TranscriptTurn turn, string reason)
    {
        if (_pages.Count == 0) return false;

        var targetPageIndex = -1;
        for (var p = 0; p < _pages.Count; p++)
        {
            foreach (var t in _pages[p].Turns)
            {
                if (ReferenceEquals(t, turn))
                {
                    targetPageIndex = p;
                    break;
                }
            }
            if (targetPageIndex >= 0) break;
        }

        if (targetPageIndex < 0) return false;

        if (targetPageIndex < _pages.Count - 1)
            _isFollowingTail = false;

        if (targetPageIndex >= _firstMountedPageIndex && targetPageIndex <= _lastMountedPageIndex)
            return false; // Already mounted

        // Shift the mounted window to center on the target page
        _firstMountedPageIndex = targetPageIndex;
        _lastMountedPageIndex = Math.Min(targetPageIndex + _options.MaxMountedPages - 1, _pages.Count - 1);
        ClampMountedRange();
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("search-jump", reason);
        return true;
    }

    public TranscriptWindowDiagnosticsSnapshot CaptureSnapshot()
    {
        return new TranscriptWindowDiagnosticsSnapshot(
            TotalTurnCount,
            TotalItemCount,
            _pages.Count,
            MountedPageCount,
            MountedTurns.Count,
            MountedTurns.Sum(static turn => turn.Items.Count),
            IsPinnedToBottom,
            DistanceFromBottom,
            _pageLoadCount,
            _pageUnloadCount,
            _prependCount,
            _cleanupCount,
            _streamingUpdateCount,
            _initialLoadMilliseconds,
            _lastCompensationBeforeOffset,
            _lastCompensationAfterOffset,
            BuildMountedPageSummary());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_sourceTurns is not null)
            _sourceTurns.CollectionChanged -= OnSourceTurnsCollectionChanged;

        ReleaseAllMountedHosts();
        _disposed = true;
    }

    private int TotalTurnCount => _sourceTurns?.Count ?? 0;

    private int TotalItemCount => _sourceTurns?.Sum(static turn => turn.Items.Count) ?? 0;

    private int MountedPageCount => _firstMountedPageIndex < 0 || _lastMountedPageIndex < _firstMountedPageIndex
        ? 0
        : (_lastMountedPageIndex - _firstMountedPageIndex) + 1;

    private void OnSourceTurnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed)
            return;

        var previousPageCount = _pages.Count;
        var shouldTrackLatestTail = _isFollowingTail;
        RebuildPages();

        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            ReleaseAllMountedHosts();
            MountedTurns.Clear();
            UpdateDiagnostics("source-change", e.Action.ToString());
            return;
        }

        if (_firstMountedPageIndex < 0 || _lastMountedPageIndex < 0 || MountedTurns.Count == 0)
        {
            ResetToLatest(DefaultInitialViewportHeight, $"source-change:{e.Action}");
            return;
        }

        if (shouldTrackLatestTail)
        {
            _lastMountedPageIndex = _pages.Count - 1;
            TrimMountedHeadOverflow();
        }
        else
        {
            _lastMountedPageIndex = Math.Min(_lastMountedPageIndex, _pages.Count - 1);
            TrimMountedTailOverflow();
        }

        ClampMountedRange();
        ReconcileMountedTurns(BuildDesiredMountedTurns());

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
            _streamingUpdateCount++;

        var pageDelta = _pages.Count - previousPageCount;
        if (pageDelta > 0)
            _pageLoadCount += pageDelta;

        UpdateDiagnostics("source-change", e.Action.ToString());
    }

    private void RebuildPages()
    {
        _pages.Clear();

        if (_sourceTurns is null || _sourceTurns.Count == 0)
            return;

        var pageTurns = new List<TranscriptTurn>(_options.MaxTurnsPerPage);
        var pageWeight = 0;
        var pageItemCount = 0;
        var pageStartTurnIndex = 0;
        var filteredTurnIndex = 0;

        foreach (var turn in _sourceTurns)
        {
            if (turn.Items.Count == 0)
                continue;

            var turnWeight = TranscriptPageWeightEstimator.EstimateTurnWeight(turn);

            if (pageTurns.Count > 0 && (pageWeight + turnWeight > _options.MaxPageWeight || pageTurns.Count >= _options.MaxTurnsPerPage))
            {
                AddPage(pageTurns, pageStartTurnIndex, filteredTurnIndex - 1, pageItemCount, pageWeight);
                pageTurns = new List<TranscriptTurn>(_options.MaxTurnsPerPage);
                pageWeight = 0;
                pageItemCount = 0;
                pageStartTurnIndex = filteredTurnIndex;
            }

            pageTurns.Add(turn);
            pageWeight += turnWeight;
            pageItemCount += turn.Items.Count;
            filteredTurnIndex++;
        }

        if (pageTurns.Count > 0)
            AddPage(pageTurns, pageStartTurnIndex, filteredTurnIndex - 1, pageItemCount, pageWeight);
    }

    private void AddPage(List<TranscriptTurn> pageTurns, int firstTurnIndex, int lastTurnIndex, int itemCount, int estimatedWeight)
    {
        var pageIndex = _pages.Count;
        _pages.Add(new TranscriptPage(
            pageId: $"page:{pageIndex:D4}",
            pageIndex: pageIndex,
            turns: pageTurns.ToArray(),
            firstTurnIndex: firstTurnIndex,
            lastTurnIndex: lastTurnIndex,
            itemCount: itemCount,
            estimatedWeight: estimatedWeight));
    }

    private void ClampMountedRange()
    {
        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            return;
        }

        if (_lastMountedPageIndex < 0)
            _lastMountedPageIndex = _pages.Count - 1;

        _lastMountedPageIndex = Math.Clamp(_lastMountedPageIndex, 0, _pages.Count - 1);
        if (_firstMountedPageIndex < 0)
            _firstMountedPageIndex = _lastMountedPageIndex;

        _firstMountedPageIndex = Math.Clamp(_firstMountedPageIndex, 0, _lastMountedPageIndex);
    }

    private IReadOnlyList<TranscriptTurn> BuildDesiredMountedTurns()
    {
        if (_pages.Count == 0 || _firstMountedPageIndex < 0 || _lastMountedPageIndex < _firstMountedPageIndex)
            return Array.Empty<TranscriptTurn>();

        var turnCount = 0;
        for (var pageIndex = _firstMountedPageIndex; pageIndex <= _lastMountedPageIndex; pageIndex++)
            turnCount += _pages[pageIndex].TurnCount;

        var turns = new List<TranscriptTurn>(turnCount);
        for (var pageIndex = _firstMountedPageIndex; pageIndex <= _lastMountedPageIndex; pageIndex++)
            turns.AddRange(_pages[pageIndex].Turns);

        return turns;
    }

    private void ReconcileMountedTurns(IReadOnlyList<TranscriptTurn> desiredTurns)
    {
        // Reconcile by identity so a turn that stays mounted keeps its realized host — and therefore
        // its already parsed markdown and highlighted code blocks — instead of being torn down and
        // rebuilt from scratch.
        //
        // A prefix/suffix diff looks cheap but releases every turn in the "changed middle", even
        // turns that remain desired and only shifted position. When an assistant message finishes,
        // the typing turn is removed AND (while pinned to the tail) the head page can trim in the
        // same source-collection change. That breaks the prefix (head shifted) and the suffix (old
        // tail is the typing turn, new tail is the assistant turn) simultaneously, so the diff
        // collapses to "replace the whole mounted range": it releases the just-finished assistant
        // turn's host and re-realizes it, re-parsing the markdown and synchronously re-highlighting
        // its code blocks on the UI thread — the multi-second finish-writing freeze. Releasing only
        // turns that truly left the window keeps that work off the finalize path.
        var desiredSet = new HashSet<TranscriptTurn>(desiredTurns, ReferenceEqualityComparer.Instance);

        for (var i = MountedTurns.Count - 1; i >= 0; i--)
        {
            var turn = MountedTurns[i];
            if (desiredSet.Contains(turn))
                continue;

            MountedTurns.RemoveAt(i);
            // The turn left the mounted window: tear down its retained realized host so its
            // controls/parsed markdown are released (bounds retention to mounted turns).
            turn.ReleaseRealizedHost();
        }

        // Bring MountedTurns to match desiredTurns by identity, reusing each surviving entry (and its
        // realized host) in place. In every production path the survivors are already an in-order
        // subsequence of desiredTurns (transcript turns keep chronological/source order and are never
        // reordered), so this just inserts the missing turns. The move branch keeps the reconcile
        // correct even if a desired turn is present but out of order — without it, a bare Insert would
        // duplicate that turn — so the method never depends on callers preserving ordering.
        for (var i = 0; i < desiredTurns.Count; i++)
        {
            var desired = desiredTurns[i];
            if (i < MountedTurns.Count && ReferenceEquals(MountedTurns[i], desired))
                continue;

            var existingIndex = -1;
            for (var j = i + 1; j < MountedTurns.Count; j++)
            {
                if (ReferenceEquals(MountedTurns[j], desired))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
                MountedTurns.Move(existingIndex, i);
            else
                MountedTurns.Insert(i, desired);
        }
    }

    private void ReleaseAllMountedHosts()
    {
        for (var i = 0; i < MountedTurns.Count; i++)
            MountedTurns[i].ReleaseRealizedHost();
    }

    private double GetMountedHeight()
    {
        if (_pages.Count == 0 || MountedPageCount == 0)
            return 0d;

        return GetMountedRangeHeight(_firstMountedPageIndex, _lastMountedPageIndex);
    }

    private static double GetConservativePageHeight(TranscriptPage page)
    {
        var total = 0d;
        for (var i = 0; i < page.Turns.Count; i++)
            total += page.Turns[i].MeasuredHeight > 0
                ? page.Turns[i].MeasuredHeight
                : TranscriptLayoutMetrics.MinimumEstimatedTurnHeight;

        if (page.TurnCount > 1)
            total += (page.TurnCount - 1) * TranscriptLayoutMetrics.TurnSpacing;

        return total;
    }

    private double GetEffectivePageHeight(TranscriptPage page)
    {
        return Math.Max(
            page.EstimatedWeight * _options.EstimatedPixelsPerWeightUnit,
            page.GetMeasuredHeight(_options.EstimatedPixelsPerWeightUnit));
    }

    private double GetMountedRangeHeight(int firstPageIndex, int lastPageIndex)
    {
        var total = 0d;
        for (var pageIndex = firstPageIndex; pageIndex <= lastPageIndex; pageIndex++)
            total += GetEffectivePageHeight(_pages[pageIndex]);

        if (lastPageIndex > firstPageIndex)
            total += (lastPageIndex - firstPageIndex) * TranscriptLayoutMetrics.TurnSpacing;

        return total;
    }

    private int TrimMountedTailOverflow()
    {
        if (_pages.Count == 0)
            return 0;

        var overflow = MountedPageCount - _options.MaxMountedPages;
        if (overflow <= 0)
            return 0;

        var removablePages = Math.Min(overflow, Math.Max(0, _lastMountedPageIndex - _firstMountedPageIndex));
        if (removablePages <= 0)
            return 0;

        _lastMountedPageIndex -= removablePages;
        _cleanupCount++;
        _pageUnloadCount += removablePages;
        return removablePages;
    }

    private int TrimMountedHeadOverflow()
    {
        if (_pages.Count == 0)
            return 0;

        var overflow = MountedPageCount - _options.MaxMountedPages;
        if (overflow <= 0)
            return 0;

        var removablePages = Math.Min(overflow, Math.Max(0, _lastMountedPageIndex - _firstMountedPageIndex));
        if (removablePages <= 0)
            return 0;

        _firstMountedPageIndex += removablePages;
        _cleanupCount++;
        _pageUnloadCount += removablePages;
        return removablePages;
    }

    private string BuildMountedPageSummary()
    {
        if (_pages.Count == 0 || MountedPageCount == 0)
            return "none";

        return string.Join(", ",
            _pages
                .Skip(_firstMountedPageIndex)
                .Take(MountedPageCount)
                .Select(static page => $"{page.PageId}[{page.FirstTurnIndex}-{page.LastTurnIndex}]")
                .ToArray());
    }

    private void UpdateDiagnostics(string stage, string reason)
    {
        if (!_options.EnableDiagnostics)
            return;

        var snapshot = CaptureSnapshot();
        var builder = new StringBuilder(256);
        builder.Append("items ").Append(snapshot.TotalItemCount)
            .Append(" | turns ").Append(snapshot.TotalTurnCount)
            .Append(" | pages ").Append(snapshot.TotalPageCount)
            .Append(" | mounted pages ").Append(snapshot.MountedPageCount)
            .Append(" | mounted turns ").Append(snapshot.MountedTurnCount)
            .Append(" | mounted items ").Append(snapshot.MountedItemCount)
            .AppendLine();
        builder.Append("following ").Append(IsFollowingTail)
            .Append(" | pinned ").Append(snapshot.IsPinnedToBottom)
            .Append(" | dist ").Append(snapshot.DistanceFromBottom.ToString("0.0"))
            .Append(" | loads ").Append(snapshot.PageLoadCount)
            .Append(" | unloads ").Append(snapshot.PageUnloadCount)
            .Append(" | prepends ").Append(snapshot.PrependCount)
            .Append(" | cleanups ").Append(snapshot.CleanupCount)
            .AppendLine();
        builder.Append("stream ").Append(snapshot.StreamingUpdateCount)
            .Append(" | init ").Append(snapshot.InitialLoadMilliseconds.ToString("0.0")).Append("ms")
            .Append(" | offset ").Append(snapshot.LastCompensationBeforeOffset.ToString("0.0"))
            .Append(" -> ").Append(snapshot.LastCompensationAfterOffset.ToString("0.0"))
            .AppendLine();
        builder.Append("mounted ").Append(snapshot.MountedPageSummary)
            .AppendLine();
        builder.Append(stage).Append(": ").Append(reason);

        DiagnosticsText = builder.ToString();
        Debug.WriteLine($"[TranscriptWindow] {stage}: {reason} | {snapshot.MountedPageSummary}");
    }

    private static double SanitizeViewportHeight(double viewportHeight)
    {
        if (double.IsNaN(viewportHeight) || double.IsInfinity(viewportHeight) || viewportHeight <= 0)
            return DefaultInitialViewportHeight;

        return viewportHeight;
    }

    private static bool TrySanitizeExtentHeight(double? extentHeight, out double sanitizedExtentHeight)
    {
        if (extentHeight is double value
            && !double.IsNaN(value)
            && !double.IsInfinity(value)
            && value > 0)
        {
            sanitizedExtentHeight = value;
            return true;
        }

        sanitizedExtentHeight = 0d;
        return false;
    }
}
