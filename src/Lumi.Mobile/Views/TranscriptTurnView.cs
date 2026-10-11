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
/// the viewport. Like desktop transcript turns, a revisited turn reuses its retained controls.
/// </summary>
internal sealed class TranscriptTurnView : Decorator
{
    private TranscriptTurnViewModel? _turn;
    private ItemsControl? _items;
    private ScrollViewer? _scroll;
    private Rect _viewport;
    private double _reservedHeight;
    private bool _attached;
    private bool _updateQueued;
    private bool _registeredAnchor;
    private int _version;

    internal bool IsReadyForEntrance =>
        !_updateQueued && (!IsNearViewport || _turn?.DisplayItems.Count == 0
            || (Child is not null && IsMeasureValid && IsArrangeValid));

    private bool IsNearViewport =>
        _viewport.Width > 0 && _viewport.Height > 0 && double.IsFinite(_viewport.Height)
        && new Rect(Bounds.Size).Intersects(_viewport.Inflate(new Thickness(0, _viewport.Height * 0.5)));

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
        if (_turn is not null)
            _turn.DisplayItems.CollectionChanged += OnItemsChanged;
        QueueUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _version++;
        _updateQueued = false;
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
        if (Child is null)
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, _reservedHeight);

        var measured = base.MeasureOverride(availableSize);
        _reservedHeight = measured.Height;
        return measured;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Child is not null)
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
            var bounds = new Rect(Bounds.Size);
            var hasViewport = _viewport.Width > 0 && _viewport.Height > 0 &&
                              double.IsFinite(_viewport.Height);
            var visible = hasViewport && bounds.Intersects(_viewport);
            SetAnchor(visible);
            var nearby = hasViewport &&
                         bounds.Intersects(_viewport.Inflate(new Thickness(0, _viewport.Height * 0.5)));
            var chat = this.FindAncestorOfType<ChatDetailView>();
            if (!nearby || _turn is null || chat?.ShouldDeferTranscriptRealization == true)
            {
                Child = null;
            }
            else
            {
                if (_items is null)
                {
                    _items = new ItemsControl
                    {
                        DataContext = _turn,
                        ItemsSource = new ObservableCollection<TranscriptItemViewModel>()
                    };
                    _items.ContainerPrepared += OnContainerPrepared;
                }
                Child = _items;
                RefreshItems();
            }
            chat?.QueueChatEntrance();
        }, DispatcherPriority.Background);
    }

    private void RefreshItems()
    {
        if (_items is null || _turn is null)
            return;

        // A detached host keeps its last item snapshot; collection changes are applied on return.
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
        return turn.DisplayItems.Sum(item => item switch
        {
            AssistantItemViewModel answer => 64 + Math.Ceiling(answer.Text.Length / 50d) * 22,
            UserTurnItemViewModel user => 48 + Math.Ceiling(user.Text.Length / 50d) * 22,
            _ => 64d
        });
    }
}
