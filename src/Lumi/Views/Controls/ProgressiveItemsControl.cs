using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lumi.Views.Controls;

/// <summary>
/// Shows <see cref="Source"/> from the top a few items at a time: the first page with the list, then
/// enough to keep one screen of items built below its scroll area's view, building more as the reader
/// scrolls toward the end. Building yields once a per-frame budget is spent, with input and rendering
/// in between, so a long list of heavy items opens at once without freezing the window, and items the
/// reader never scrolls to are never built. Items are laid out in a plain panel, with no size
/// estimates or recycling, and released when the list leaves the visual tree.
/// </summary>
public sealed class ProgressiveItemsControl : ItemsControl
{
    public static readonly StyledProperty<IList?> SourceProperty =
        AvaloniaProperty.Register<ProgressiveItemsControl, IList?>(nameof(Source));

    /// <summary>Items shown with the list itself, enough to fill a bounded area on open.</summary>
    internal const int FirstPageSize = 12;

    /// <summary>Building beyond the first page yields once this much time has been spent.</summary>
    private const double FrameBudgetMs = 12d;

    /// <summary>
    /// Building beyond the first page starts this long after the first page has been laid out, so a row
    /// that opens glides over its first page undisturbed.
    /// </summary>
    internal static readonly TimeSpan FillDelay = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(1);

    private readonly ObservableCollection<object?> _shown = [];
    private INotifyCollectionChanged? _observed;
    private ScrollViewer? _scroller;
    private bool _isAttached;
    private bool _isFillScheduled;

    static ProgressiveItemsControl()
    {
        SourceProperty.Changed.AddClassHandler<ProgressiveItemsControl>((list, _) => list.OnSourceChanged());
    }

    public ProgressiveItemsControl() => ItemsSource = _shown;

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public IList? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>How many of the leading <see cref="Source"/> items are shown so far.</summary>
    internal int ShownCount => _shown.Count;

    /// <summary>
    /// Shows the list through <paramref name="item"/>, building it under the same budget while yielding
    /// to the window between steps. Returns once the item is shown, or the list has left the tree.
    /// </summary>
    internal async Task ShowThroughAsync(object item)
    {
        while (_isAttached && Source is { } source && source.IndexOf(item) is var index and >= 0 && index >= _shown.Count)
        {
            FillWithinBudget(index);
            await Task.Delay(1);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null)
            _scroller.ScrollChanged += OnScrollChanged;
        Observe(Source);
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        if (_scroller is not null)
            _scroller.ScrollChanged -= OnScrollChanged;
        _scroller = null;
        Observe(null);
        _shown.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => ScheduleFill();

    /// <summary>
    /// More items are wanted while less than one screen of them is built below the scroll area's view.
    /// Without a scroll area, every item is wanted.
    /// </summary>
    private bool WantsMore(IList source)
    {
        if (_shown.Count >= source.Count)
            return false;
        if (_scroller is not { } scroller)
            return true;

        var builtBelow = scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height;
        return builtBelow < Math.Max(scroller.Viewport.Height, 1);
    }

    private void OnSourceChanged()
    {
        if (!_isAttached)
            return;

        Observe(Source);
        Restart();
    }

    private void Observe(IList? source)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnSourceCollectionChanged;

        _observed = source as INotifyCollectionChanged;
        if (_observed is not null)
            _observed.CollectionChanged += OnSourceCollectionChanged;
    }

    /// <summary>Starts again from the top: the first page now (the list is often being laid out), the rest later.</summary>
    private void Restart()
    {
        _shown.Clear();
        if (!_isAttached || Source is not { } source)
            return;

        for (var i = 0; i < FirstPageSize && i < source.Count; i++)
            _shown.Add(source[i]);

        if (_isFillScheduled || _shown.Count >= source.Count)
            return;

        // Once the first page has been laid out, wait a moment longer before building the rest.
        _isFillScheduled = true;
        Dispatcher.UIThread.Post(
            () => DispatcherTimer.RunOnce(FillStep, FillDelay, DispatcherPriority.Normal),
            DispatcherPriority.Loaded);
    }

    /// <summary>Keeps the shown items equal to the leading items of the source as the source changes.</summary>
    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, Source))
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                if (e.NewStartingIndex <= _shown.Count)
                {
                    for (var i = 0; i < e.NewItems.Count; i++)
                        _shown.Insert(e.NewStartingIndex + i, e.NewItems[i]);
                }
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                for (var i = 0; i < e.OldItems.Count && e.OldStartingIndex < _shown.Count; i++)
                    _shown.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count && e.NewStartingIndex + i < _shown.Count; i++)
                    _shown[e.NewStartingIndex + i] = e.NewItems[i];
                break;
            default:
                Restart();
                return;
        }

        ScheduleFill();
    }

    private void ScheduleFill()
    {
        if (_isFillScheduled || !_isAttached || Source is not { } source || !WantsMore(source))
            return;

        _isFillScheduled = true;
        // A short timer lets input and rendering run between steps. Background priority would wait for
        // a moment without pending input, which never comes while the reader scrolls.
        DispatcherTimer.RunOnce(FillStep, StepInterval, DispatcherPriority.Normal);
    }

    private void FillStep()
    {
        _isFillScheduled = false;
        FillWithinBudget();
        ScheduleFill();
    }

    /// <summary>
    /// Shows further items while they are wanted, or through <paramref name="throughIndex"/>, laying each
    /// out at once so its real cost counts against the budget. One item is always shown, so the list
    /// keeps moving even when a single item takes longer than the budget.
    /// </summary>
    private void FillWithinBudget(int throughIndex = -1)
    {
        if (!_isAttached || Source is not { } source)
            return;

        var started = Stopwatch.GetTimestamp();
        while (_shown.Count < source.Count && (_shown.Count <= throughIndex || WantsMore(source)))
        {
            _shown.Add(source[_shown.Count]);
            UpdateLayout();
            if (!_isAttached || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= FrameBudgetMs)
                break;
        }
    }
}
