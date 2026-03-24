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
            ErrorMessageItem error => EstimateTextWeight(error.Content, 2),
            ReasoningItem reasoning => EstimateTextWeight(reasoning.Content, 4),
            SubagentToolCallItem subagent => Math.Max(
                4,
                2 + subagent.Activities.Count
                  + EstimateAdditionalTextWeight(subagent.TranscriptText)
                  + EstimateAdditionalTextWeight(subagent.ReasoningText)),
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
        MountedTurns.Clear();
        UpdateDiagnostics("clear", reason);
    }

    public TranscriptWindowMutation ResetToLatest(double viewportHeight, string reason)
    {
        viewportHeight = SanitizeViewportHeight(viewportHeight);
        var stopwatch = Stopwatch.StartNew();

        RebuildPages();
        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
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

    public TranscriptWindowMutation EnsureViewportCoverage(double viewportHeight, string reason)
    {
        if (_pages.Count == 0 || _firstMountedPageIndex <= 0)
            return TranscriptWindowMutation.None;

        viewportHeight = SanitizeViewportHeight(viewportHeight);
        var targetHeight = viewportHeight * _options.MountedViewportFillMultiplier;
        var currentHeight = GetMountedHeight();
        if (currentHeight >= targetHeight)
            return TranscriptWindowMutation.None;

        var addedPages = 0;
        var estimatedDelta = 0d;
        while (_firstMountedPageIndex > 0 && MountedPageCount < _options.MaxMountedPages && currentHeight < targetHeight)
        {
            _firstMountedPageIndex--;
            var page = _pages[_firstMountedPageIndex];
            var addedHeight = GetEffectivePageHeight(page) + TranscriptLayoutMetrics.TurnSpacing;
            estimatedDelta += addedHeight;
            currentHeight += addedHeight;
            addedPages++;
        }

        if (addedPages == 0)
            return TranscriptWindowMutation.None;

        _pageLoadCount += addedPages;
        ReconcileMountedTurns(BuildDesiredMountedTurns());
        UpdateDiagnostics("coverage", reason);
        return new TranscriptWindowMutation(TranscriptWindowMutationKind.EnsureCoverage, reason, addedPages, 0, estimatedDelta, false);
    }

    public TranscriptWindowMutation UpdateViewport(TranscriptViewportState state, string reason)
    {
        UpdatePinnedState(state.IsPinnedToBottom, state.DistanceFromBottom, $"viewport:{reason}");

        if (_pages.Count == 0 || MountedPageCount == 0)
            return TranscriptWindowMutation.None;

        if (state.OffsetY <= _options.PrependTriggerPixels && _firstMountedPageIndex > 0)
        {
            _firstMountedPageIndex--;
            _prependCount++;
            _pageLoadCount++;
            var page = _pages[_firstMountedPageIndex];
            var estimatedDelta = GetEffectivePageHeight(page) + TranscriptLayoutMetrics.TurnSpacing;
            var removedTailPages = TrimMountedTailOverflow();
            ReconcileMountedTurns(BuildDesiredMountedTurns());
            UpdateDiagnostics("prepend", reason);
            return new TranscriptWindowMutation(TranscriptWindowMutationKind.Prepend, reason, 1, removedTailPages, estimatedDelta, true);
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
    {
        var changed = IsPinnedToBottom != isPinnedToBottom;
        IsPinnedToBottom = isPinnedToBottom;
        DistanceFromBottom = distanceFromBottom;
        if (changed)
            UpdateDiagnostics("pinned", reason);
    }

    /// <summary>
    /// Ensures the mounted window includes the latest pages (tail-tracking).
    /// Call when the user sends a message after scrolling up, so the viewport
    /// snaps to the newest content. Trims head pages to stay within limits.
    /// </summary>
    public bool EnsureLatestMounted(string reason)
    {
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
        var wasMountedToLatestTail = previousPageCount > 0
            && _lastMountedPageIndex >= 0
            && _lastMountedPageIndex >= previousPageCount - 1;
        RebuildPages();

        if (_pages.Count == 0)
        {
            _firstMountedPageIndex = -1;
            _lastMountedPageIndex = -1;
            MountedTurns.Clear();
            UpdateDiagnostics("source-change", e.Action.ToString());
            return;
        }

        if (_firstMountedPageIndex < 0 || _lastMountedPageIndex < 0 || MountedTurns.Count == 0)
        {
            ResetToLatest(DefaultInitialViewportHeight, $"source-change:{e.Action}");
            return;
        }

        if (wasMountedToLatestTail)
        {
            _lastMountedPageIndex = _pages.Count - 1;
            TrimMountedHeadOverflow();

            // After trimming, backfill earlier pages if there's room. Without
            // this, _firstMountedPageIndex ratchets up permanently over repeated
            // add/remove cycles (e.g. typing indicator shown at turn start,
            // hidden at turn end) — each Add may trim the head for overflow,
            // but the subsequent Remove never reclaims the freed slot.
            while (_firstMountedPageIndex > 0 && MountedPageCount < _options.MaxMountedPages)
                _firstMountedPageIndex--;
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
        var prefix = 0;
        while (prefix < MountedTurns.Count && prefix < desiredTurns.Count && ReferenceEquals(MountedTurns[prefix], desiredTurns[prefix]))
            prefix++;

        var currentSuffix = MountedTurns.Count - 1;
        var desiredSuffix = desiredTurns.Count - 1;
        while (currentSuffix >= prefix && desiredSuffix >= prefix && ReferenceEquals(MountedTurns[currentSuffix], desiredTurns[desiredSuffix]))
        {
            currentSuffix--;
            desiredSuffix--;
        }

        for (var i = currentSuffix; i >= prefix; i--)
            MountedTurns.RemoveAt(i);

        for (var i = prefix; i <= desiredSuffix; i++)
            MountedTurns.Insert(i, desiredTurns[i]);
    }

    private double GetMountedHeight()
    {
        if (_pages.Count == 0 || MountedPageCount == 0)
            return 0d;

        return GetMountedRangeHeight(_firstMountedPageIndex, _lastMountedPageIndex);
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
        builder.Append("pinned ").Append(snapshot.IsPinnedToBottom)
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
}
