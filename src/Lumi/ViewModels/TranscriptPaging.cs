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
    /// <summary>
    /// Keeps the visual turn collection identity-stable. Retained for focused regression coverage;
    /// production uses progressive history so unknown geometry never enters the scroll extent.
    /// </summary>
    public bool MaintainStableMembership { get; init; }
    public bool MaintainStableGeometry { get; init; }
    public bool ProgressiveHistory { get; init; }
    public int MaxPageWeight { get; init; } = 34;
    public int MaxTurnsPerPage { get; init; } = 8;
    public int MinInitialPages { get; init; } = 2;
    public int MaxMountedPages { get; init; } = 6;
    public int TrimToMountedPages { get; init; } = 4;
    public int PrependBatchPageCount { get; init; } = 3;
    public int AppendBatchPageCount { get; init; } = 3;
    public double InitialViewportFillMultiplier { get; init; } = 1.6d;
    public double MountedViewportFillMultiplier { get; init; } = 2.1d;
    public double PrependTriggerPixels { get; init; } = 220d;
    public double AppendTriggerPixels { get; init; } = 220d;
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
        if (turn.HasMeasuredRealizedHeight && turn.MeasuredHeight > 0)
            return turn.MeasuredHeight;

        return Math.Max(TranscriptLayoutMetrics.MinimumEstimatedTurnHeight, EstimateTurnWeight(turn) * pixelsPerWeightUnit);
    }

    internal static int EstimateItemWeight(TranscriptItem item)
    {
        return item switch
        {
            AssistantMessageItem assistant => EstimateTextWeight(assistant.Content, 3),
            UserMessageItem user => EstimateTextWeight(user.Content, 2),
            JobWakeItem jobWake => Math.Max(5, EstimateTextWeight(jobWake.SearchText, 4)),
            ErrorMessageItem error => EstimateTextWeight(error.Content, 2),
            ReasoningItem { IsExpanded: false } => 1,
            ReasoningItem reasoning => EstimateTextWeight(reasoning.Content, 4),
            SubagentToolCallItem { IsExpanded: false } => 1,
            SubagentToolCallItem => 9,
            SubagentGroupItem { IsExpanded: false } => 1,
            SubagentGroupItem => 9,
            ToolGroupItem { IsExpanded: false } => 1,
            ToolGroupItem => 9,
            TurnSummaryItem { IsExpanded: false } => 1,
            TurnSummaryItem summary => Math.Max(2, summary.InnerItems.Sum(EstimateItemWeight)),
            QuestionItem => 3,
            PlanCardItem => 3,
            FileChangesSummaryItem => 4,
            SingleToolItem => 1,
            _ => 2,
        };
    }

    private static int EstimateTextWeight(string? text, int baseWeight)
    {
        if (string.IsNullOrWhiteSpace(text))
            return baseWeight;

        var explicitLineCount = 1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
                explicitLineCount++;
        }

        // At the transcript's narrow supported widths, prose averages roughly 64 characters per
        // wrapped line. One 56px weight unit covers about two rendered markdown lines including
        // paragraph/list spacing. Unlike the old capped heuristic, this scales for very long answers
        // instead of reserving a few hundred pixels for content that measures tens of thousands.
        var wrappedLineCount = Math.Max(1, (int)Math.Ceiling(text.Length / 64d));
        var visualLineCount = Math.Max(explicitLineCount, wrappedLineCount);
        return Math.Max(baseWeight, 1 + (int)Math.Ceiling(visualLineCount / 2d));
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
    Append,
    TrimHead,
    TailRestore,
    Rewindow,
}

internal enum TranscriptPagingDirection
{
    None,
    TowardOlder,
    TowardNewer,
}

internal readonly record struct TranscriptViewportState(
    double OffsetY,
    double ViewportHeight,
    double ExtentHeight,
    bool IsPinnedToBottom,
    double DistanceFromBottom,
    TranscriptPagingDirection PagingDirection = TranscriptPagingDirection.None);

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
    private readonly Dictionary<TranscriptTurn, int> _pageIndexByTurn =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TranscriptTurn> _turnByStableId =
        new(StringComparer.Ordinal);
    private int _firstMountedPageIndex = -1;
    private int _lastMountedPageIndex = -1;
    private TranscriptTurn? _firstMountedTurnBoundary;
    private TranscriptTurn? _lastMountedTurnBoundary;
    private bool _disposed;
    private string _diagnosticsText = string.Empty;
    private bool _isFollowingTail = true;
    private bool _isPinnedToBottom = true;
    private double _distanceFromBottom;
    private double _topSpacerHeight;
    private double _bottomSpacerHeight;
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

    public bool MaintainsStableMembership => _options.MaintainStableMembership;
    public bool MaintainsStableGeometry => _options.MaintainStableGeometry;
    public bool UsesProgressiveHistory => _options.ProgressiveHistory;

    public double TopSpacerHeight
    {
        get => _topSpacerHeight;
        private set => SetProperty(ref _topSpacerHeight, value);
    }

    public double BottomSpacerHeight
    {
        get => _bottomSpacerHeight;
        private set => SetProperty(ref _bottomSpacerHeight, value);
    }

    public bool HasOlderPages => !_options.MaintainStableMembership && _firstMountedPageIndex > 0;

    public bool HasNewerPages =>
        !_options.MaintainStableMembership
        && !_options.ProgressiveHistory
        && _lastMountedPageIndex >= 0
        && _lastMountedPageIndex < _pages.Count - 1;

    public double DistanceFromBottom
    {
        get => _distanceFromBottom;
        private set => SetProperty(ref _distanceFromBottom, value);
    }

    public void BindTranscript(ObservableCollection<TranscriptTurn> sourceTurns, string reason)
    {
        if (ReferenceEquals(_sourceTurns, sourceTurns))
        {
            var previouslyMountedTurns = MountedTurns.ToArray();
            RebuildPages();
            if (_options.MaintainStableMembership)
            {
                SetFullMountedRange();
                ReconcileMountedTurns(sourceTurns);
                UpdateDiagnostics("bind", reason);
                return;
            }

            RestoreMountedRangeByIdentity(previouslyMountedTurns, keepLatestTail: _options.ProgressiveHistory);
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
        if (_options.ProgressiveHistory)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            _firstMountedTurnBoundary = null;
            _lastMountedTurnBoundary = null;
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            ReleaseAllMountedHosts();
            MountedTurns.Clear();
            UpdateDiagnostics("bind", reason);
            return;
        }

        if (_options.MaintainStableMembership)
        {
            SetFullMountedRange();
            ReconcileMountedTurns(sourceTurns);
            UpdateDiagnostics("bind", reason);
            return;
        }

        ClampMountedRange();
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("bind", reason);
    }

    public void Clear(string reason)
    {
        _pages.Clear();
        _pageIndexByTurn.Clear();
        _turnByStableId.Clear();
        _firstMountedPageIndex = -1;
        _lastMountedPageIndex = -1;
        _firstMountedTurnBoundary = null;
        _lastMountedTurnBoundary = null;
        TopSpacerHeight = 0;
        BottomSpacerHeight = 0;
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
            _firstMountedTurnBoundary = null;
            _lastMountedTurnBoundary = null;
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            ReleaseAllMountedHosts();
            MountedTurns.Clear();
            stopwatch.Stop();
            _initialLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            UpdateDiagnostics("reset", reason);
            return new TranscriptWindowMutation(TranscriptWindowMutationKind.Reset, reason, 0, 0, 0, false);
        }

        if (_options.MaintainStableMembership)
        {
            var stablePreviousPageCount = MountedPageCount;
            SetFullMountedRange();
            ReconcileMountedTurns(_sourceTurns!);

            stopwatch.Stop();
            _initialLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            var stableAddedPages = Math.Max(0, MountedPageCount - stablePreviousPageCount);
            _pageLoadCount += stableAddedPages;
            UpdateDiagnostics("reset", reason);
            return new TranscriptWindowMutation(TranscriptWindowMutationKind.Reset, reason, stableAddedPages, 0, 0, false);
        }

        var estimatedHeight = 0d;
        var targetHeight = viewportHeight * _options.InitialViewportFillMultiplier;
        var firstIndex = _pages.Count - 1;

        while (firstIndex >= 0)
        {
            estimatedHeight += GetPageContentHeight(_pages[firstIndex]);
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
        SetMountedTurnBoundariesToPageEdges();
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

    public TranscriptWindowMutation ResetToBoundary(string firstMountedStableId, string reason)
    {
        if (!_options.ProgressiveHistory
            || !_turnByStableId.TryGetValue(firstMountedStableId, out var firstMountedTurn)
            || !_pageIndexByTurn.TryGetValue(firstMountedTurn, out var firstMountedPageIndex)
            || _pages.Count == 0)
        {
            return ResetToLatest(DefaultInitialViewportHeight, reason);
        }

        var previousPageCount = MountedPageCount;
        _isFollowingTail = false;
        _firstMountedPageIndex = firstMountedPageIndex;
        _lastMountedPageIndex = _pages.Count - 1;
        _firstMountedTurnBoundary = firstMountedTurn;
        _lastMountedTurnBoundary = _pages[^1].Turns[^1];
        ReconcileMountedTurns(BuildDesiredMountedTurns());

        var addedPages = Math.Max(0, MountedPageCount - previousPageCount);
        _pageLoadCount += addedPages;
        UpdateDiagnostics("reset-boundary", reason);
        return new TranscriptWindowMutation(
            TranscriptWindowMutationKind.Reset,
            reason,
            addedPages,
            0,
            0,
            false);
    }

    public TranscriptWindowMutation EnsureViewportCoverage(double viewportHeight, string reason, double? actualExtentHeight = null)
    {
        if (_options.MaintainStableMembership || _options.MaintainStableGeometry)
            return TranscriptWindowMutation.None;

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
        var previousFirstMountedPageIndex = _firstMountedPageIndex;
        while (_firstMountedPageIndex > 0
               && (_options.ProgressiveHistory || MountedPageCount < _options.MaxMountedPages)
               && currentHeight < targetHeight)
        {
            _firstMountedPageIndex--;
            var page = _pages[_firstMountedPageIndex];
            _firstMountedTurnBoundary = page.Turns[0];
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
        if (_options.ProgressiveHistory)
            PrependMountedPages(_firstMountedPageIndex, previousFirstMountedPageIndex - 1);
        else
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

        if (_options.MaintainStableMembership)
            return TranscriptWindowMutation.None;

        if (_options.MaintainStableGeometry)
            return UpdateStableGeometryViewport(state, reason);

        if (_pages.Count == 0 || MountedPageCount == 0)
            return TranscriptWindowMutation.None;

        var isPagingTowardOlder = state.PagingDirection == TranscriptPagingDirection.TowardOlder;
        var isPagingTowardNewer = state.PagingDirection == TranscriptPagingDirection.TowardNewer;

        if (state.OffsetY <= _options.PrependTriggerPixels
            && _firstMountedPageIndex > 0
            && !isPagingTowardNewer)
        {
            // Load a small chunk of older pages per near-top scroll. This gives the reader more
            // history above the anchor and avoids a render/layout cycle for every single page.
            // Keep at least the previously-first mounted page so anchor restoration can still land.
            var maxBatch = _options.ProgressiveHistory
                ? Math.Max(1, _options.PrependBatchPageCount)
                : Math.Min(
                    Math.Max(1, _options.PrependBatchPageCount),
                    Math.Max(1, MountedPageCount - 1));
            var addedPages = 0;
            var estimatedDelta = 0d;
            var previousFirstMountedPageIndex = _firstMountedPageIndex;
            while (_firstMountedPageIndex > 0 && addedPages < maxBatch)
            {
                _firstMountedPageIndex--;
                var page = _pages[_firstMountedPageIndex];
                _firstMountedTurnBoundary = page.Turns[0];
                estimatedDelta += GetEffectivePageHeight(page) + TranscriptLayoutMetrics.TurnSpacing;
                addedPages++;
            }

            if (addedPages == 0)
                return TranscriptWindowMutation.None;

            _prependCount += addedPages;
            _pageLoadCount += addedPages;
            var removedTailPages = _options.ProgressiveHistory ? 0 : TrimMountedTailOverflow();
            if (_options.ProgressiveHistory)
                PrependMountedPages(_firstMountedPageIndex, previousFirstMountedPageIndex - 1);
            else
                ReconcileMountedTurns(BuildDesiredMountedTurns());
            UpdateDiagnostics("prepend", reason);
            return new TranscriptWindowMutation(TranscriptWindowMutationKind.Prepend, reason, addedPages, removedTailPages, estimatedDelta, true);
        }

        var isApproachingLocalBottom =
            state.DistanceFromBottom <= _options.AppendTriggerPixels
            && !isPagingTowardOlder
            && (isPagingTowardNewer
                || state.IsPinnedToBottom
                || state.OffsetY > _options.PrependTriggerPixels);
        if (isApproachingLocalBottom && HasNewerPages)
        {
            // Move the bounded reader window forward again when the user reaches its local bottom.
            // Keep at least one previously-mounted page so ChatView can restore a stable visible anchor
            // while older head pages are evicted.
            var maxBatch = Math.Min(
                Math.Max(1, _options.AppendBatchPageCount),
                Math.Max(1, MountedPageCount - 1));
            var addedPages = 0;
            var estimatedDelta = 0d;
            while (_lastMountedPageIndex < _pages.Count - 1 && addedPages < maxBatch)
            {
                _lastMountedPageIndex++;
                var page = _pages[_lastMountedPageIndex];
                _lastMountedTurnBoundary = page.Turns[^1];
                estimatedDelta += GetEffectivePageHeight(page) + TranscriptLayoutMetrics.TurnSpacing;
                addedPages++;
            }

            if (addedPages == 0)
                return TranscriptWindowMutation.None;

            _pageLoadCount += addedPages;
            var removedHeadPages = TrimMountedHeadOverflow();
            ReconcileMountedTurns(BuildDesiredMountedTurns());
            UpdateDiagnostics("append", reason);
            return new TranscriptWindowMutation(
                TranscriptWindowMutationKind.Append,
                reason,
                addedPages,
                removedHeadPages,
                estimatedDelta,
                removedHeadPages > 0);
        }

        if (!_options.ProgressiveHistory && MountedPageCount > _options.MaxMountedPages)
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
                _firstMountedTurnBoundary = _pages[_firstMountedPageIndex].Turns[0];
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
        if (HasNewerPages)
        {
            // The ScrollViewer only knows about the mounted window. Its local bottom is not the
            // conversation tail while newer pages are unmounted, so it must not re-enable follow mode.
            isFollowingTail = false;
            isPinnedToBottom = false;
        }

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

        if (_options.MaintainStableMembership)
            return false;

        if (_pages.Count == 0)
            return false;

        if (_lastMountedPageIndex >= _pages.Count - 1)
            return false;

        _lastMountedPageIndex = _pages.Count - 1;
        _lastMountedTurnBoundary = _pages[^1].Turns[^1];
        ClampMountedRange();
        if (!_options.ProgressiveHistory)
            TrimMountedHeadOverflow();
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("ensure-latest", reason);
        return true;
    }

    public TranscriptWindowMutation EnsureLatestMountedIfAdjacentTailGap(string reason)
    {
        if (_options.MaintainStableMembership)
            return TranscriptWindowMutation.None;

        if (_options.ProgressiveHistory)
            return TranscriptWindowMutation.None;

        if (_pages.Count == 0 || MountedPageCount == 0)
            return TranscriptWindowMutation.None;

        var latestPageIndex = _pages.Count - 1;
        if (_lastMountedPageIndex >= latestPageIndex)
            return TranscriptWindowMutation.None;

        if (_lastMountedPageIndex + 1 != latestPageIndex)
            return TranscriptWindowMutation.None;

        _lastMountedPageIndex = latestPageIndex;
        _lastMountedTurnBoundary = _pages[latestPageIndex].Turns[^1];
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
    /// Rewindows legacy bounded modes around a turn. Progressive history deliberately rejects this
    /// operation because mounting an unknown target-to-tail gap would defeat bounded admission.
    /// </summary>
    public bool MountPageContainingTurn(TranscriptTurn turn, string reason)
    {
        if (_options.MaintainStableMembership || _options.ProgressiveHistory)
            return false;

        if (_pages.Count == 0) return false;

        if (!_pageIndexByTurn.TryGetValue(turn, out var targetPageIndex))
            return false;

        if (targetPageIndex < _pages.Count - 1)
            _isFollowingTail = false;

        if (targetPageIndex >= _firstMountedPageIndex && targetPageIndex <= _lastMountedPageIndex)
            return false; // Already mounted

        // Shift the mounted window to center on the target page
        _firstMountedPageIndex = targetPageIndex;
        _lastMountedPageIndex = Math.Min(targetPageIndex + _options.MaxMountedPages - 1, _pages.Count - 1);
        SetMountedTurnBoundariesToPageEdges();
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
        var previouslyMountedTurns = MountedTurns.ToArray();
        RebuildPages();

        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            _firstMountedTurnBoundary = null;
            _lastMountedTurnBoundary = null;
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            ReleaseAllMountedHosts();
            MountedTurns.Clear();
            UpdateDiagnostics("source-change", e.Action.ToString());
            return;
        }

        if (_options.MaintainStableMembership)
        {
            SetFullMountedRange();
            ReconcileMountedTurns(_sourceTurns!);

            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
                _streamingUpdateCount++;

            var stablePageDelta = _pages.Count - previousPageCount;
            if (stablePageDelta > 0)
                _pageLoadCount += stablePageDelta;

            UpdateDiagnostics("source-change", e.Action.ToString());
            return;
        }

        if (_options.ProgressiveHistory)
        {
            if (_firstMountedPageIndex < 0 || _lastMountedPageIndex < 0 || MountedTurns.Count == 0)
            {
                ResetToLatest(DefaultInitialViewportHeight, $"source-change:{e.Action}");
                return;
            }

            RestoreMountedRangeByIdentity(previouslyMountedTurns, keepLatestTail: true);
            ReconcileMountedTurns(BuildDesiredMountedTurns());

            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
                _streamingUpdateCount++;

            var progressivePageDelta = _pages.Count - previousPageCount;
            if (progressivePageDelta > 0)
                _pageLoadCount += progressivePageDelta;

            UpdateDiagnostics("source-change", e.Action.ToString());
            return;
        }

        if (_firstMountedPageIndex < 0 || _lastMountedPageIndex < 0 || MountedTurns.Count == 0)
        {
            ResetToLatest(DefaultInitialViewportHeight, $"source-change:{e.Action}");
            return;
        }

        RestoreMountedRangeByIdentity(previouslyMountedTurns, keepLatestTail: false);
        if (shouldTrackLatestTail)
        {
            _lastMountedPageIndex = _pages.Count - 1;
            _lastMountedTurnBoundary = _pages[^1].Turns[^1];
            TrimMountedHeadOverflow();
        }
        else
        {
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
        _pageIndexByTurn.Clear();
        _turnByStableId.Clear();

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

    private void SetFullMountedRange()
    {
        _firstMountedPageIndex = _pages.Count == 0 ? -1 : 0;
        _lastMountedPageIndex = _pages.Count - 1;
        SetMountedTurnBoundariesToPageEdges();
    }

    private void AddPage(List<TranscriptTurn> pageTurns, int firstTurnIndex, int lastTurnIndex, int itemCount, int estimatedWeight)
    {
        var pageIndex = _pages.Count;
        var page = new TranscriptPage(
            pageId: BuildStablePageId(pageTurns),
            pageIndex: pageIndex,
            turns: pageTurns.ToArray(),
            firstTurnIndex: firstTurnIndex,
            lastTurnIndex: lastTurnIndex,
            itemCount: itemCount,
            estimatedWeight: estimatedWeight);
        _pages.Add(page);
        foreach (var turn in page.Turns)
        {
            _pageIndexByTurn.Add(turn, pageIndex);
            _turnByStableId.TryAdd(turn.StableId, turn);
        }
    }

    private void RestoreMountedRangeByIdentity(
        IReadOnlyList<TranscriptTurn> previouslyMountedTurns,
        bool keepLatestTail)
    {
        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            return;
        }

        if (!keepLatestTail)
        {
            var firstSurvivingPageIndex = int.MaxValue;
            var lastSurvivingPageIndex = -1;
            for (var index = 0; index < previouslyMountedTurns.Count; index++)
            {
                if (!_pageIndexByTurn.TryGetValue(previouslyMountedTurns[index], out var pageIndex))
                    continue;

                firstSurvivingPageIndex = Math.Min(firstSurvivingPageIndex, pageIndex);
                lastSurvivingPageIndex = Math.Max(lastSurvivingPageIndex, pageIndex);
            }

            if (lastSurvivingPageIndex < 0)
            {
                ClampMountedRange();
                return;
            }

            _firstMountedPageIndex = firstSurvivingPageIndex;
            _lastMountedPageIndex = lastSurvivingPageIndex;
            SetMountedTurnBoundariesToPageEdges();
            ClampMountedRange();
            return;
        }

        var firstPageIndex = FindFirstSurvivingPageIndex(previouslyMountedTurns);
        var lastPageIndex = _pages.Count - 1;

        if (firstPageIndex < 0 && lastPageIndex < 0)
        {
            ClampMountedRange();
            return;
        }

        if (firstPageIndex < 0)
            firstPageIndex = lastPageIndex;
        if (lastPageIndex < firstPageIndex)
            lastPageIndex = firstPageIndex;

        _firstMountedPageIndex = firstPageIndex;
        _lastMountedPageIndex = lastPageIndex;
        _firstMountedTurnBoundary = FindFirstSurvivingTurn(previouslyMountedTurns);
        _lastMountedTurnBoundary = _pages[^1].Turns[^1];
        ClampMountedRange();
    }

    private int FindFirstSurvivingPageIndex(IReadOnlyList<TranscriptTurn> turns)
    {
        for (var index = 0; index < turns.Count; index++)
        {
            if (_pageIndexByTurn.TryGetValue(turns[index], out var pageIndex))
                return pageIndex;
        }

        return -1;
    }

    private TranscriptTurn? FindFirstSurvivingTurn(IReadOnlyList<TranscriptTurn> turns)
    {
        for (var index = 0; index < turns.Count; index++)
        {
            if (_pageIndexByTurn.ContainsKey(turns[index]))
                return turns[index];
        }

        return null;
    }

    private static string BuildStablePageId(IReadOnlyList<TranscriptTurn> turns)
    {
        var firstStableId = turns[0].StableId;
        var lastStableId = turns[^1].StableId;
        return ReferenceEquals(turns[0], turns[^1])
            ? $"page:{firstStableId}"
            : $"page:{firstStableId}..{lastStableId}";
    }

    private TranscriptWindowMutation UpdateStableGeometryViewport(TranscriptViewportState state, string reason)
    {
        if (_pages.Count == 0)
            return TranscriptWindowMutation.None;

        var firstVisiblePage = FindPageAtOffset(state.OffsetY);
        var lastVisiblePage = FindPageAtOffset(state.OffsetY + Math.Max(0d, state.ViewportHeight - 1d));
        var maxPages = Math.Max(1, _options.MaxMountedPages);

        var desiredFirst = Math.Max(0, firstVisiblePage - 1);
        var desiredLast = Math.Min(_pages.Count - 1, desiredFirst + maxPages - 1);
        var bufferedLast = Math.Min(_pages.Count - 1, lastVisiblePage + 1);
        if (desiredLast < bufferedLast)
        {
            desiredLast = bufferedLast;
            desiredFirst = Math.Max(0, desiredLast - maxPages + 1);
        }

        if (desiredFirst == _firstMountedPageIndex && desiredLast == _lastMountedPageIndex)
            return TranscriptWindowMutation.None;

        var previousFirst = _firstMountedPageIndex;
        var previousLast = _lastMountedPageIndex;
        _firstMountedPageIndex = desiredFirst;
        _lastMountedPageIndex = desiredLast;
        SetMountedTurnBoundariesToPageEdges();

        var addedPages = CountRangeDifference(desiredFirst, desiredLast, previousFirst, previousLast);
        var removedPages = CountRangeDifference(previousFirst, previousLast, desiredFirst, desiredLast);
        _pageLoadCount += addedPages;
        _pageUnloadCount += removedPages;
        if (removedPages > 0)
            _cleanupCount++;

        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("rewindow", reason);
        return new TranscriptWindowMutation(
            TranscriptWindowMutationKind.Rewindow,
            reason,
            addedPages,
            removedPages,
            0,
            false);
    }

    private int FindPageAtOffset(double offsetY)
    {
        var remaining = Math.Max(0d, offsetY);
        for (var pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            var height = GetPageLayoutHeight(_pages[pageIndex]);
            if (remaining < height)
                return pageIndex;

            remaining -= height;
        }

        return _pages.Count - 1;
    }

    private static int CountRangeDifference(int first, int last, int otherFirst, int otherLast)
    {
        if (first < 0 || last < first)
            return 0;

        var count = 0;
        for (var index = first; index <= last; index++)
        {
            if (index < otherFirst || index > otherLast)
                count++;
        }

        return count;
    }

    private void ClampMountedRange()
    {
        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            _firstMountedTurnBoundary = null;
            _lastMountedTurnBoundary = null;
            return;
        }

        if (_lastMountedPageIndex < 0)
            _lastMountedPageIndex = _pages.Count - 1;

        _lastMountedPageIndex = Math.Clamp(_lastMountedPageIndex, 0, _pages.Count - 1);
        if (_firstMountedPageIndex < 0)
            _firstMountedPageIndex = _lastMountedPageIndex;

        _firstMountedPageIndex = Math.Clamp(_firstMountedPageIndex, 0, _lastMountedPageIndex);
        if (_firstMountedTurnBoundary is null
            || !_pageIndexByTurn.TryGetValue(_firstMountedTurnBoundary, out var firstBoundaryPage)
            || firstBoundaryPage < _firstMountedPageIndex
            || firstBoundaryPage > _lastMountedPageIndex)
        {
            _firstMountedTurnBoundary = _pages[_firstMountedPageIndex].Turns[0];
        }

        if (_lastMountedTurnBoundary is null
            || !_pageIndexByTurn.TryGetValue(_lastMountedTurnBoundary, out var lastBoundaryPage)
            || lastBoundaryPage < _firstMountedPageIndex
            || lastBoundaryPage > _lastMountedPageIndex)
        {
            _lastMountedTurnBoundary = _pages[_lastMountedPageIndex].Turns[^1];
        }
    }

    private IReadOnlyList<TranscriptTurn> BuildDesiredMountedTurns()
    {
        if (_pages.Count == 0 || _firstMountedPageIndex < 0 || _lastMountedPageIndex < _firstMountedPageIndex)
            return Array.Empty<TranscriptTurn>();

        var turnCount = 0;
        for (var pageIndex = _firstMountedPageIndex; pageIndex <= _lastMountedPageIndex; pageIndex++)
            turnCount += _pages[pageIndex].TurnCount;

        var turns = new List<TranscriptTurn>(turnCount);
        var reachedFirstBoundary = _firstMountedTurnBoundary is null;
        for (var pageIndex = _firstMountedPageIndex; pageIndex <= _lastMountedPageIndex; pageIndex++)
        {
            foreach (var turn in _pages[pageIndex].Turns)
            {
                if (!reachedFirstBoundary)
                {
                    if (!ReferenceEquals(turn, _firstMountedTurnBoundary))
                        continue;

                    reachedFirstBoundary = true;
                }

                turns.Add(turn);
                if (ReferenceEquals(turn, _lastMountedTurnBoundary))
                    return turns;
            }
        }

        return turns;
    }

    private void SetMountedTurnBoundariesToPageEdges()
    {
        if (_pages.Count == 0
            || _firstMountedPageIndex < 0
            || _lastMountedPageIndex < _firstMountedPageIndex)
        {
            _firstMountedTurnBoundary = null;
            _lastMountedTurnBoundary = null;
            return;
        }

        _firstMountedTurnBoundary = _pages[_firstMountedPageIndex].Turns[0];
        _lastMountedTurnBoundary = _pages[_lastMountedPageIndex].Turns[^1];
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

        UpdateSpacerHeights();
    }

    private void PrependMountedPages(int firstPageIndex, int lastPageIndex)
    {
        for (var pageIndex = lastPageIndex; pageIndex >= firstPageIndex; pageIndex--)
        {
            var pageTurns = _pages[pageIndex].Turns;
            for (var turnIndex = pageTurns.Count - 1; turnIndex >= 0; turnIndex--)
                MountedTurns.Insert(0, pageTurns[turnIndex]);
        }

        UpdateSpacerHeights();
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

    private double GetPageContentHeight(TranscriptPage page)
        => _options.MaintainStableGeometry
            ? page.GetMeasuredHeight(_options.EstimatedPixelsPerWeightUnit)
            : GetEffectivePageHeight(page);

    private double GetPageLayoutHeight(TranscriptPage page)
        => GetPageContentHeight(page) + TranscriptLayoutMetrics.TurnSpacing;

    private void UpdateSpacerHeights()
    {
        if (!_options.MaintainStableGeometry || _pages.Count == 0 || MountedPageCount == 0)
        {
            TopSpacerHeight = 0;
            BottomSpacerHeight = 0;
            return;
        }

        var top = 0d;
        for (var pageIndex = 0; pageIndex < _firstMountedPageIndex; pageIndex++)
            top += GetPageLayoutHeight(_pages[pageIndex]);

        var bottom = 0d;
        for (var pageIndex = _lastMountedPageIndex + 1; pageIndex < _pages.Count; pageIndex++)
            bottom += GetPageLayoutHeight(_pages[pageIndex]);

        TopSpacerHeight = top;
        BottomSpacerHeight = bottom;
    }

    private double GetMountedRangeHeight(int firstPageIndex, int lastPageIndex)
    {
        var total = 0d;
        for (var pageIndex = firstPageIndex; pageIndex <= lastPageIndex; pageIndex++)
            total += GetPageContentHeight(_pages[pageIndex]);

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
        _lastMountedTurnBoundary = _pages[_lastMountedPageIndex].Turns[^1];
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
        _firstMountedTurnBoundary = _pages[_firstMountedPageIndex].Turns[0];
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
