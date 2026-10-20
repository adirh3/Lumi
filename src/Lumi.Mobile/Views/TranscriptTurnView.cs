using System.Collections.Specialized;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Mobile.Views;

/// <summary>
/// Keeps a stable height for offscreen turns and defers their heavy content until it is near
/// the viewport. Built turns stay attached but hidden so returning does not reapply their styles.
/// </summary>
/// <remarks>
/// Turns about to be seen are built at once. Turns further out are prepared ahead of the viewport,
/// one per frame and nearest first (see <see cref="ChatDetailView.RequestRealization"/>), so a
/// fling meets built turns instead of building several in one frame. Content is hidden well past
/// the band it is built in, and released when its bounded transcript page leaves the tree.
/// </remarks>
internal sealed class TranscriptTurnView : Decorator
{
    // In viewport heights beyond each edge of the viewport.
    private const double EntranceBand = 0.5;
    private const double RealizeBand = 1;
    private const double RetainBand = 1.5;

    private TranscriptTurnViewModel? _turn;
    private ItemsControl? _items;
    private ScrollViewer? _scroll;
    private ChatDetailView? _chat;
    private Rect _viewport;
    private double _reservedHeight;
    private bool _attached;
    private bool _updateQueued;
    private bool _registeredAnchor;
    private int _version;

    internal bool IsRealized => _items?.IsVisible == true;

    internal bool IsReadyForEntrance =>
        !_updateQueued && (!IsNearViewport || _turn?.DisplayItems.Count == 0
            || (IsRealized && IsMeasureValid && IsArrangeValid));

    private bool HasViewport =>
        _viewport.Width > 0 && _viewport.Height > 0 && double.IsFinite(_viewport.Height);

    private bool IsNearViewport => IsWithin(EntranceBand);

    /// <summary>How far this turn is from the viewport, for building the nearest turns first.</summary>
    internal double ViewportDistance
    {
        get
        {
            if (!HasViewport)
                return double.MaxValue;
            var bounds = new Rect(Bounds.Size);
            return bounds.Bottom < _viewport.Top ? _viewport.Top - bounds.Bottom
                : bounds.Top > _viewport.Bottom ? bounds.Top - _viewport.Bottom
                : 0;
        }
    }

    public TranscriptTurnView()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            QueueUpdate();
        };
        SizeChanged += (_, _) => QueueUpdate();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        _version++;
        _updateQueued = false;
        _chat?.CancelRealization(this);
        if (_attached && _turn is not null)
            _turn.DisplayItems.CollectionChanged -= OnItemsChanged;
        Child = null;
        _items = null;
        _turn = DataContext as TranscriptTurnViewModel;
        _reservedHeight = EstimateHeight(_turn);
        if (_attached && _turn is not null)
            _turn.DisplayItems.CollectionChanged += OnItemsChanged;
        base.OnDataContextChanged(e);
        InvalidateMeasure();
        QueueUpdate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        _chat = this.FindAncestorOfType<ChatDetailView>();
        if (_turn is not null)
            _turn.DisplayItems.CollectionChanged += OnItemsChanged;
        QueueUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _version++;
        _updateQueued = false;
        _chat?.CancelRealization(this);
        _chat = null;
        if (_turn is not null)
            _turn.DisplayItems.CollectionChanged -= OnItemsChanged;
        SetAnchor(false);
        _scroll = null;
        Child = null;
        _items = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!IsRealized)
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, _reservedHeight);

        var measured = base.MeasureOverride(availableSize);
        _reservedHeight = measured.Height;
        return measured;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsRealized)
            QueueUpdate();
    }

    internal void QueueUpdate()
    {
        if (!_attached || _updateQueued)
            return;
        _updateQueued = true;
        var version = _version;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || version != _version)
                return;
            _updateQueued = false;
            SetAnchor(IsWithin(0));
            var chat = _chat;
            if (_turn is null || chat?.ShouldDeferTranscriptRealization == true || !IsWithin(RetainBand))
            {
                chat?.CancelRealization(this);
                if (_items is not null)
                    _items.IsVisible = false;
            }
            else if (IsRealized)
            {
                RefreshItems();
            }
            else if (chat is null || IsWithin(EntranceBand))
            {
                // Content about to be seen appears at once, never one turn per frame.
                Realize();
            }
            else if (IsWithin(RealizeBand))
            {
                chat.RequestRealization(this);
            }
            chat?.QueueChatEntrance();
        }, DispatcherPriority.Background);
    }

    /// <summary>Builds or restores this turn's content; false when there was nothing to do.</summary>
    internal bool Realize()
    {
        if (!_attached || _turn is null || IsRealized || !IsWithin(RealizeBand)
            || _chat?.ShouldDeferTranscriptRealization == true)
            return false;

        if (_items is null)
        {
            _items = new ItemsControl
            {
                DataContext = _turn,
                ItemsSource = new ObservableCollection<TranscriptItemViewModel>(_turn.DisplayItems),
                IsVisible = false
            };
            _items.ContainerPrepared += OnContainerPrepared;
            Child = _items;
        }
        else
            RefreshItems();
        _items.IsVisible = true;
        _chat?.QueueChatEntrance();
        return true;
    }

    private bool IsWithin(double band) =>
        HasViewport
        && new Rect(Bounds.Size).Intersects(_viewport.Inflate(new Thickness(0, _viewport.Height * band)));

    private void RefreshItems()
    {
        if (_items is null || _turn is null)
            return;

        // A hidden host keeps its last item snapshot; collection changes are applied on return.
        // Existing item properties remain live, while retained markdown defers offscreen rebuilding.
        if (_items.ItemsSource is ObservableCollection<TranscriptItemViewModel> snapshot)
            TranscriptTurnViewModel.SynchronizeDisplayItems(snapshot, _turn.DisplayItems);
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var version = _version;
        var container = e.Container;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || version != _version
                || container.DataContext is not TranscriptItemViewModel item
                || !container.IsAttachedToVisualTree()
                || !item.TryConsumeEntranceAnimation())
                return;

            var position = container.TranslatePoint(default, this);
            if (this.FindAncestorOfType<ChatDetailView>()?.CanAnimateArrival == true
                && position is { } point
                && new Rect(point, container.Bounds.Size).Intersects(_viewport))
                SlideFadeEntrance.PlayComposited(container, offsetY: 12, duration: TimeSpan.FromMilliseconds(260));
        }, DispatcherPriority.Background);
    }

    private void SetAnchor(bool active)
    {
        if (_registeredAnchor == active || _scroll is null)
            return;
        _registeredAnchor = active;
        if (active)
            _scroll.RegisterAnchorCandidate(this);
        else
            _scroll.UnregisterAnchorCandidate(this);
    }

    private static double EstimateHeight(TranscriptTurnViewModel? turn)
    {
        if (turn is null)
            return 0;
        // Measured on a 412 dp phone: a bubble line holds about 42 characters, an answer line 44,
        // and a closing answer adds its footer row.
        return turn.DisplayItems.Sum(item => item switch
        {
            AssistantItemViewModel answer => 10 + Math.Ceiling(answer.Text.Length / 44d) * 25
                                                + (answer.ShowAnswerActions ? 40 : 0),
            UserTurnItemViewModel user => 66 + Math.Ceiling(user.Text.Length / 42d) * 24,
            ActivitySummaryItemViewModel or WorkSummaryItemViewModel => 48,
            _ => 64d
        });
    }
}
