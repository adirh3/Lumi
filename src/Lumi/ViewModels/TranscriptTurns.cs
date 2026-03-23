using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lumi.ViewModels;

public sealed class TranscriptTurn : ObservableObject
{
    private double _measuredHeight;

    public ObservableCollection<TranscriptItem> Items { get; } = [];
    public string StableId { get; }

    public TranscriptTurn(string stableId)
    {
        StableId = stableId;
    }

    public double MeasuredHeight
    {
        get => _measuredHeight;
        set => SetProperty(ref _measuredHeight, value);
    }

    public int IndexOf(TranscriptItem item) => Items.IndexOf(item);

    public bool Remove(TranscriptItem item) => Items.Remove(item);

}

public readonly record struct TranscriptTurnControlDiagnosticsSnapshot(
    int ControlCreateCount,
    int ItemHostCreateCount);

public sealed class TranscriptTurnControl : UserControl
{
    private readonly StackPanel _itemsHost;
    private TranscriptTurn? _turn;
    private bool _isAttachedToVisualTree;
    private bool _isSubscribedToTurnItems;
    private static int _controlCreateCount;
    private static int _itemHostCreateCount;

    public static readonly StyledProperty<TranscriptTurn?> TurnProperty =
        AvaloniaProperty.Register<TranscriptTurnControl, TranscriptTurn?>(nameof(Turn));

    static TranscriptTurnControl()
    {
        TurnProperty.Changed.AddClassHandler<TranscriptTurnControl>((control, args) =>
            control.OnTurnChanged(control._turn, control.Turn));
    }

    public TranscriptTurnControl()
    {
        Interlocked.Increment(ref _controlCreateCount);

        _itemsHost = new StackPanel
        {
            Spacing = TranscriptLayoutMetrics.TurnSpacing
        };

        Content = _itemsHost;
        SizeChanged += OnSizeChanged;
    }

    public TranscriptTurn? Turn
    {
        get => GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    public ObservableCollection<TranscriptItem>? Items => Turn?.Items;

    public string? StableId => Turn?.StableId;

    public static TranscriptTurnControlDiagnosticsSnapshot CaptureDiagnostics() => new(
        Volatile.Read(ref _controlCreateCount),
        Volatile.Read(ref _itemHostCreateCount));

    public static void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _controlCreateCount, 0);
        Interlocked.Exchange(ref _itemHostCreateCount, 0);
    }

    private void OnTurnChanged(TranscriptTurn? oldTurn, TranscriptTurn? newTurn)
    {
        if (ReferenceEquals(oldTurn, newTurn))
            return;

        UnsubscribeFromTurnItems(oldTurn);

        _turn = newTurn;

        SubscribeToTurnItems(newTurn);
        if (newTurn is not null && Bounds.Height > 0)
            newTurn.MeasuredHeight = Bounds.Height;

        // Only build children when attached to the visual tree. The inner
        // ContentPresenters need to walk up the tree to resolve DataTemplates
        // defined in ChatView.axaml. If built before attachment, the template
        // lookup fails silently and the presenters measure at zero height,
        // leaving the turn invisible. OnAttachedToVisualTree calls
        // RebuildItemHosts once the tree connection is established.
        if (_isAttachedToVisualTree)
            RebuildItemHosts();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        SubscribeToTurnItems(_turn);
        RebuildItemHosts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromTurnItems(_turn);
        _isAttachedToVisualTree = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_turn is not null && e.NewSize.Height > 0)
            _turn.MeasuredHeight = e.NewSize.Height;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count; i++)
                    _itemsHost.Children.Insert(e.NewStartingIndex + i, CreateItemHost((TranscriptItem)e.NewItems[i]!));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                for (var i = 0; i < e.OldItems.Count; i++)
                    _itemsHost.Children.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count; i++)
                    _itemsHost.Children[e.NewStartingIndex + i] = CreateItemHost((TranscriptItem)e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Move:
            case NotifyCollectionChangedAction.Reset:
            default:
                RebuildItemHosts();
                break;
        }
    }

    private void RebuildItemHosts()
    {
        _itemsHost.Children.Clear();
        if (_turn is null)
            return;

        foreach (var item in _turn.Items)
            _itemsHost.Children.Add(CreateItemHost(item));
    }

    private static Control CreateItemHost(TranscriptItem item)
    {
        Interlocked.Increment(ref _itemHostCreateCount);
        return new ContentPresenter
        {
            Content = item,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private void SubscribeToTurnItems(TranscriptTurn? turn)
    {
        if (!_isAttachedToVisualTree || _isSubscribedToTurnItems || turn is null)
            return;

        turn.Items.CollectionChanged += OnItemsChanged;
        _isSubscribedToTurnItems = true;
    }

    private void UnsubscribeFromTurnItems(TranscriptTurn? turn)
    {
        if (!_isSubscribedToTurnItems || turn is null)
            return;

        turn.Items.CollectionChanged -= OnItemsChanged;
        _isSubscribedToTurnItems = false;
    }
}