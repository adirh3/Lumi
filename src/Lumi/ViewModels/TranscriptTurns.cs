using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using StrataTheme.Controls;

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

    /// <summary>
    /// The realized item-host for this turn, cached on the stable turn object so switching away
    /// from and back within the same window reuses the already-built and parsed transcript controls.
    /// Avalonia controls cannot safely move between different layout roots, so the owning top-level
    /// is tracked separately and a new host is built when the turn moves to another window.
    /// </summary>
    internal StackPanel? RealizedItemsHost { get; set; }
    internal WeakReference<TopLevel>? RealizedItemsHostRoot { get; set; }

    public int IndexOf(TranscriptItem item) => Items.IndexOf(item);

    public bool Remove(TranscriptItem item) => Items.Remove(item);

    /// <summary>Tears down and drops the cached realized host so its controls can be collected.</summary>
    internal void ReleaseRealizedHost()
    {
        var host = RealizedItemsHost;
        if (host is null)
            return;

        RealizedItemsHost = null;
        RealizedItemsHostRoot = null;
        var owner = host.Parent as TranscriptTurnControl
            ?? host.GetVisualParent() as TranscriptTurnControl;
        if (owner is not null)
        {
            owner.ReleaseTerminalHost(host);
            return;
        }

        TranscriptTurnControl.ReleaseHost(host);
    }
}

public readonly record struct TranscriptTurnControlDiagnosticsSnapshot(
    int ControlCreateCount,
    int ItemHostCreateCount,
    int ActiveRealizedHostCount,
    int PeakActiveRealizedHostCount);

public sealed class TranscriptTurnControl : UserControl
{
    private TranscriptTurn? _turn;
    private StackPanel? _host;
    private bool _isAttachedToVisualTree;
    private bool _isSubscribedToTurnItems;
    private bool _realizationPending;
    private bool _isViewportActive;
    private static int _controlCreateCount;
    private static int _itemHostCreateCount;
    private static int _activeRealizedHostCount;
    private static int _peakActiveRealizedHostCount;

    // Matches TranscriptPagingOptions.EstimatedPixelsPerWeightUnit so every stable placeholder reserves
    // the same initial height used by transcript diagnostics before the turn has been measured.
    private const double PlaceholderPixelsPerWeightUnit = 56d;

    public static readonly StyledProperty<TranscriptTurn?> TurnProperty =
        AvaloniaProperty.Register<TranscriptTurnControl, TranscriptTurn?>(nameof(Turn));

    public static readonly StyledProperty<bool> IsViewportManagedProperty =
        AvaloniaProperty.Register<TranscriptTurnControl, bool>(nameof(IsViewportManaged));

    private static readonly AttachedProperty<IDisposable?> ItemVisibilityBindingProperty =
        AvaloniaProperty.RegisterAttached<TranscriptTurnControl, Control, IDisposable?>("ItemVisibilityBinding");

    private static readonly AttachedProperty<bool> IsTrackedRealizedHostProperty =
        AvaloniaProperty.RegisterAttached<TranscriptTurnControl, Control, bool>("IsTrackedRealizedHost");

    // Tracks which TranscriptItem a host child renders, so a retained host can be reconciled
    // back to the live item list by identity. Set on the built view (or fallback presenter)
    // rather than relying on ContentPresenter.Content, since host children are now the
    // directly-built item views, not ContentPresenters.
    private static readonly AttachedProperty<TranscriptItem?> HostedItemProperty =
        AvaloniaProperty.RegisterAttached<TranscriptTurnControl, Control, TranscriptItem?>("HostedItem");

    /// <summary>
    /// The <see cref="TranscriptItem"/> a realized host child renders (null on any other control).
    /// Item hosts are directly-built item views carrying this attached property rather than
    /// <see cref="ContentPresenter"/>s whose Content is the item, so callers that need to locate a
    /// specific item's visual (e.g. in-chat search) must match on this instead of
    /// <c>ContentPresenter.Content</c>.
    /// </summary>
    internal static TranscriptItem? GetHostedItem(Control host) => host.GetValue(HostedItemProperty);

    static TranscriptTurnControl()
    {
        TurnProperty.Changed.AddClassHandler<TranscriptTurnControl>((control, args) =>
            control.OnTurnChanged(control._turn, control.Turn));
    }

    public TranscriptTurnControl()
    {
        Interlocked.Increment(ref _controlCreateCount);
        SizeChanged += OnSizeChanged;
    }

    public TranscriptTurn? Turn
    {
        get => GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    public bool IsViewportManaged
    {
        get => GetValue(IsViewportManagedProperty);
        set => SetValue(IsViewportManagedProperty, value);
    }

    public ObservableCollection<TranscriptItem>? Items => Turn?.Items;

    public string? StableId => Turn?.StableId;

    public static TranscriptTurnControlDiagnosticsSnapshot CaptureDiagnostics() => new(
        Volatile.Read(ref _controlCreateCount),
        Volatile.Read(ref _itemHostCreateCount),
        Volatile.Read(ref _activeRealizedHostCount),
        Volatile.Read(ref _peakActiveRealizedHostCount));

    public static void ResetDiagnostics()
    {
        Interlocked.Exchange(ref _controlCreateCount, 0);
        Interlocked.Exchange(ref _itemHostCreateCount, 0);
        Interlocked.Exchange(ref _peakActiveRealizedHostCount, Volatile.Read(ref _activeRealizedHostCount));
    }

    private void OnTurnChanged(TranscriptTurn? oldTurn, TranscriptTurn? newTurn)
    {
        if (ReferenceEquals(oldTurn, newTurn))
            return;

        VerifyUiThread();
        UnsubscribeFromTurnItems(oldTurn);
        ReleaseAdoptedHost();

        _turn = newTurn;

        SubscribeToTurnItems(newTurn);
        AdoptHost();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        _isViewportActive = !IsViewportManaged;
        SubscribeToTurnItems(_turn);
        AdoptHost();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromTurnItems(_turn);
        _isAttachedToVisualTree = false;
        ReleaseAdoptedHost();
        base.OnDetachedFromVisualTree(e);
    }

    // Keep the recycled transcript subtree OUT of the UI Automation (accessibility) tree.
    // When a UIA client is active, Avalonia (12.0.5) lazily creates a managed AutomationPeer plus a
    // Win32 AutomationNode for every control it walks, pins the node in a ConditionalWeakTable keyed
    // by the peer, and does NOT release either when the control is later detached. The transcript
    // constantly streams, rebuilds, and recycles its per-message controls, so those orphaned
    // peers/nodes accumulate without bound — each one pinning a whole detached StrataChatMessage
    // subtree and its render-thread composition visuals. Over a long session that flood starves the
    // UI/render thread: animations break, the navigation menu stops compositing, and everything slows
    // (the reported cumulative degradation). Turn controls are bounded and reused, so exposing each as
    // an automation LEAF (no children) prevents per-message peers from ever being created while
    // keeping the app's real landmarks — nav, composer, the transcript container — accessible.
    protected override AutomationPeer OnCreateAutomationPeer()
        => new TranscriptTurnAutomationPeer(this);

    // A control peer that deliberately reports no automation children, pruning the message subtree
    // from the UIA tree without removing the turn node itself.
    private sealed class TranscriptTurnAutomationPeer : ControlAutomationPeer
    {
        public TranscriptTurnAutomationPeer(Control owner) : base(owner)
        {
        }

        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => [];
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_turn is not null && e.NewSize.Height > 0)
            _turn.MeasuredHeight = e.NewSize.Height;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        VerifyUiThread();

        var host = _host;
        if (host is null)
        {
            // No adopted host yet (e.g. detached); a later AdoptHost builds/reconciles it.
            AdoptHost();
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                if (e.NewStartingIndex > host.Children.Count)
                {
                    RebuildHostChildren();
                    break;
                }

                for (var i = 0; i < e.NewItems.Count; i++)
                    host.Children.Insert(e.NewStartingIndex + i, CreateItemHost(GetTranscriptItem(e.NewItems[i])));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                if (e.OldStartingIndex + e.OldItems.Count > host.Children.Count)
                {
                    RebuildHostChildren();
                    break;
                }

                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    var removed = host.Children[e.OldStartingIndex];
                    host.Children.RemoveAt(e.OldStartingIndex);
                    ReleaseItemHost(removed);
                }
                break;
            case NotifyCollectionChangedAction.Replace
                when e.OldItems is not null
                     && e.NewItems is not null
                     && e.OldStartingIndex >= 0
                     && e.NewStartingIndex == e.OldStartingIndex
                     && e.OldItems.Count == e.NewItems.Count
                     && e.NewStartingIndex + e.NewItems.Count <= host.Children.Count:
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    var index = e.NewStartingIndex + i;
                    var replaced = host.Children[index];
                    host.Children.RemoveAt(index);
                    ReleaseItemHost(replaced);
                    host.Children.Insert(index, CreateItemHost(GetTranscriptItem(e.NewItems[i])));
                }
                break;
            case NotifyCollectionChangedAction.Move
                when e.OldItems is not null
                     && e.OldItems.Count == 1
                     && e.OldStartingIndex >= 0
                     && e.NewStartingIndex >= 0
                     && e.OldStartingIndex < host.Children.Count:
                var child = host.Children[e.OldStartingIndex];
                host.Children.RemoveAt(e.OldStartingIndex);
                if (e.NewStartingIndex <= host.Children.Count)
                    host.Children.Insert(e.NewStartingIndex, child);
                else
                    RebuildHostChildren();
                break;
            case NotifyCollectionChangedAction.Move:
            case NotifyCollectionChangedAction.Reset:
            default:
                RebuildHostChildren();
                break;
        }
    }

    // Adopt the current turn's retained host (building it once if absent) as our Content, so
    // switching away from and back to a chat reuses already-built/parsed transcript controls
    // instead of rebuilding them. Hosts only matter while attached: their content templates
    // inflate (and markdown parses) on measure, which only happens in the visual tree.
    private void AdoptHost()
    {
        VerifyUiThread();

        if (!_isAttachedToVisualTree || _turn is null)
            return;

        // Already realized and shown for this turn: just keep its children in sync (the cheap
        // streaming/reconcile path) without re-queuing a realization.
        if (_host is not null && ReferenceEquals(Content, _host))
        {
            ReconcileHostChildren(_host, _turn.Items);
            return;
        }

        ReservePlaceholderHeight();
        if (IsViewportManaged && !_isViewportActive)
            return;

        // Defer the heavy realization (host build for a fresh turn, or visual-tree re-measure for a
        // retained one) so a chat switch doesn't measure every mounted turn in one synchronous layout
        // pass — the long freeze the user feels. Reserve the turn's known height meanwhile so the
        // scrollbar and scroll anchor stay correct, then let the scheduler realize bottom-first in
        // small frame-budgeted batches.
        _realizationPending = true;
        TranscriptRealizationScheduler.Instance.Request(this);
    }

    internal bool IsViewportActive => _isViewportActive;

    internal void SetViewportActive(bool isActive, bool retainHostWhenInactive = false)
    {
        if (!IsViewportManaged || _isViewportActive == isActive)
            return;

        _isViewportActive = isActive;
        if (isActive)
        {
            AdoptHost();
            return;
        }

        if (_realizationPending)
        {
            _realizationPending = false;
            TranscriptRealizationScheduler.Instance.Cancel(this);
        }

        ReservePlaceholderHeight();
        var host = _host;
        _host = null;
        if (host is null)
            return;

        if (retainHostWhenInactive
            && _turn is not null
            && ReferenceEquals(_turn.RealizedItemsHost, host))
        {
            return;
        }

        if (_turn is not null && ReferenceEquals(_turn.RealizedItemsHost, host))
        {
            _turn.RealizedItemsHost = null;
            _turn.RealizedItemsHostRoot = null;
        }

        ReleaseHost(host);
    }

    internal bool HasRetainedViewportHost
    {
        get
        {
            if (_turn?.RealizedItemsHost is not { Parent: null })
                return false;

            var currentRoot = TopLevel.GetTopLevel(this);
            return currentRoot is not null
                && _turn.RealizedItemsHostRoot is { } rootReference
                && rootReference.TryGetTarget(out var hostRoot)
                && ReferenceEquals(hostRoot, currentRoot);
        }
    }

    internal void ReleaseCachedViewportHost()
    {
        if (!HasRetainedViewportHost || _turn?.RealizedItemsHost is not { } host)
            return;

        _turn.RealizedItemsHost = null;
        _turn.RealizedItemsHostRoot = null;
        ReleaseHost(host);
    }

    internal void ReleaseTerminalHost(StackPanel host)
    {
        VerifyUiThread();
        if (ReferenceEquals(Content, host))
            Content = null;
        if (ReferenceEquals(_host, host))
            _host = null;
        ReleaseHost(host);
    }

    // Performs the deferred heavy work for this turn. Invoked by the realization scheduler (or
    // directly when an immediate realize is required, e.g. scrolling to a searched turn).
    internal void RealizePendingHost()
    {
        VerifyUiThread();
        _realizationPending = false;

        if (!_isAttachedToVisualTree || _turn is null)
            return;

        var currentRoot = TopLevel.GetTopLevel(this)
            ?? throw new InvalidOperationException("Attached transcript turn control has no top-level.");
        var host = _turn.RealizedItemsHost;
        var canReuseCachedHost =
            host is not null &&
            _turn.RealizedItemsHostRoot is { } rootReference &&
            rootReference.TryGetTarget(out var hostRoot) &&
            ReferenceEquals(hostRoot, currentRoot);

        if (host is not null && !canReuseCachedHost)
        {
            // Never move realized controls between windows. Avalonia can leave the old layout
            // manager's arrange queue pointing at them and later terminate the app with
            // "Attempt to call InvalidateArrange on wrong LayoutManager."
            if (host.Parent is null)
                ReleaseHost(host);

            host = null;
        }

        if (host is null)
        {
            host = new StackPanel
            {
                Spacing = TranscriptLayoutMetrics.TurnSpacing
            };
            TrackRealizedHost(host);

            foreach (var item in _turn.Items)
                host.Children.Add(CreateItemHost(item));

            _turn.RealizedItemsHost = host;
            _turn.RealizedItemsHostRoot = new WeakReference<TopLevel>(currentRoot);
        }
        else
        {
            // Same layout root: detach from a stale recycled owner before re-parenting.
            if (host.Parent is ContentControl owner && !ReferenceEquals(owner, this))
                owner.Content = null;

            // The host may have gone stale while un-parented (e.g. background streaming changed
            // the turn's items); reconcile it back to the live item list before reuse.
            ReconcileHostChildren(host, _turn.Items);
        }

        _host = host;
        if (!ReferenceEquals(Content, host))
            Content = host;
        ClearPlaceholderHeight();
    }

    // Reserve the turn's known (or estimated) height with empty content so the placeholder occupies
    // the right space until the real subtree is measured. Exact for a switch-back (cached
    // MeasuredHeight) → no reflow; an estimate on first realization.
    private void ReservePlaceholderHeight()
    {
        if (_turn is null)
            return;

        MinHeight = TranscriptPageWeightEstimator.EstimateTurnHeight(_turn, PlaceholderPixelsPerWeightUnit);
        if (Content is not null)
            Content = null;
    }

    private void ClearPlaceholderHeight() => ClearValue(MinHeightProperty);

    // Un-parent the adopted host but leave it cached on the turn for reuse on a later re-adopt.
    // The retained host is torn down only when the turn is evicted (TranscriptTurn.ReleaseRealizedHost).
    private void ReleaseAdoptedHost()
    {
        if (_realizationPending)
        {
            _realizationPending = false;
            TranscriptRealizationScheduler.Instance.Cancel(this);
        }

        ClearPlaceholderHeight();

        if (_host is null)
            return;

        if (ReferenceEquals(Content, _host))
            Content = null;

        if (_turn is not null && !ReferenceEquals(_turn.RealizedItemsHost, _host))
            ReleaseHost(_host);

        _host = null;
    }

    private void RebuildHostChildren()
    {
        VerifyUiThread();
        if (!_isAttachedToVisualTree || _host is null || _turn is null)
            return;

        ReleaseHostChildren(_host);

        foreach (var item in _turn.Items)
            _host.Children.Add(CreateItemHost(item));
    }

    // Brings a retained host's children back in sync with the live item list. Fast no-op when the
    // children already match by identity (the common switch-back case); otherwise rebuilds.
    private void ReconcileHostChildren(StackPanel host, ObservableCollection<TranscriptItem> items)
    {
        var matches = host.Children.Count == items.Count;
        if (matches)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(host.Children[i].GetValue(HostedItemProperty), items[i]))
                    continue;

                matches = false;
                break;
            }
        }

        if (matches)
            return;

        ReleaseHostChildren(host);

        foreach (var item in items)
            host.Children.Add(CreateItemHost(item));
    }

    // Tears down a cached host's children (disposing visibility bindings) so its controls can be
    // collected. Called when a turn leaves the viewport cache or an idle chat sheds its controls.
    internal static void ReleaseHost(StackPanel host)
    {
        if (host.GetValue(IsTrackedRealizedHostProperty))
        {
            host.ClearValue(IsTrackedRealizedHostProperty);
            Interlocked.Decrement(ref _activeRealizedHostCount);
        }

        ReleaseHostChildren(host);
    }

    private static void ReleaseHostChildren(StackPanel host)
    {
        var children = host.Children.ToArray();
        host.Children.Clear();
        foreach (var child in children)
            ReleaseItemHost(child);
    }

    private static void TrackRealizedHost(StackPanel host)
    {
        if (host.GetValue(IsTrackedRealizedHostProperty))
            return;

        host.SetValue(IsTrackedRealizedHostProperty, true);
        var active = Interlocked.Increment(ref _activeRealizedHostCount);
        while (true)
        {
            var peak = Volatile.Read(ref _peakActiveRealizedHostCount);
            if (active <= peak
                || Interlocked.CompareExchange(ref _peakActiveRealizedHostCount, active, peak) == peak)
            {
                return;
            }
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Runtime transcript hosts bind to internal TranscriptItem properties; this desktop app is not trimmed and binding avoids leak-prone view-capturing event handlers.")]
    private Control CreateItemHost(TranscriptItem item)
    {
        Interlocked.Increment(ref _itemHostCreateCount);

        // Build the item's view directly from its matching DataTemplate (resolved off this
        // control's place in the visual tree) and hold the concrete view -- NOT a ContentPresenter
        // whose Content is the data item. A ContentPresenter re-inflates its templated child every
        // time it leaves and re-enters the visual tree, which happens on every chat switch; that
        // re-parses all markdown and rebuilds the whole subtree. Holding the built view directly
        // means a switch-away/switch-back within the same layout root re-parents the SAME instances,
        // so retained markdown (StrataMarkdown.RetainContentOnDetach) and the rest of the subtree are
        // reused, not rebuilt.
        Control host;
        var template = this.FindDataTemplate(item);
        if (template?.Build(item) is { } view)
        {
            view.DataContext = item;
            host = view;
        }
        else
        {
            host = new ContentPresenter
            {
                Content = item,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        host.SetValue(HostedItemProperty, item);

        var binding = host.Bind(
            IsVisibleProperty,
            new Binding
            {
                Path = nameof(TranscriptItem.IsItemVisible),
                Source = item,
                Mode = BindingMode.OneWay
            });
        host.SetValue(ItemVisibilityBindingProperty, binding);

        return host;
    }

    private static void ReleaseItemHost(Control host)
    {
        var markdownControls = host is StrataMarkdown markdown
            ? [markdown]
            : host.GetVisualDescendants().OfType<StrataMarkdown>().ToArray();
        for (var index = markdownControls.Length - 1; index >= 0; index--)
            markdownControls[index].ReleaseRetainedContent();

        host.GetValue(ItemVisibilityBindingProperty)?.Dispose();
        host.ClearValue(ItemVisibilityBindingProperty);
        host.ClearValue(HostedItemProperty);
        host.ClearValue(IsVisibleProperty);

        if (host is ContentPresenter presenter)
            presenter.Content = null;
        else
            host.DataContext = null;
    }

    private static TranscriptItem GetTranscriptItem(object? value)
    {
        return value as TranscriptItem
               ?? throw new InvalidOperationException("Expected TranscriptItem in transcript collection change event.");
    }

    private static void VerifyUiThread()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("TranscriptTurnControl item hosts must be updated on the UI thread.");
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

/// <summary>
/// The transcript's <see cref="ItemsControl"/>. Exposes a single automation LEAF (reports no
/// children) so that an active UI Automation client which repeatedly walks the tree while the
/// transcript streams and rebuilds can never descend into the churning turn/message controls.
/// <para>
/// Pruning each <see cref="TranscriptTurnControl"/> to a leaf is not sufficient on its own: an
/// <c>ItemsControl</c>'s default <c>ItemsControlAutomationPeer</c> creates per-item wrapper peers
/// that enumerate the item container's visual children directly, so a UIA walk still materialises
/// managed <c>AutomationPeer</c>s plus Win32 <c>AutomationNode</c>s for the turn containers and
/// their message content. Avalonia (12.0.5) never releases those peers/nodes once the controls are
/// recycled on the next rebuild — they stay pinned by native UIA COM wrappers and the static
/// <c>AutomationPeer → AutomationNode</c> <c>ConditionalWeakTable</c> — so they accumulate without
/// bound, each pinning a whole detached message subtree and its render-thread composition visuals
/// until the UI/render thread is starved (animations break, the nav menu stops compositing,
/// everything slows). Because this container instance is STABLE across rebuilds (only its Items
/// change), giving it a leaf peer means the walk only ever touches long-lived controls and creates
/// zero per-cycle automation churn. The container keeps its <c>AutomationProperties</c> Name/HelpText
/// so the transcript is still announced to assistive tech as a labelled region.
/// </para>
/// </summary>
public sealed class TranscriptItemsControl : ItemsControl
{
    private const double RealizationCacheViewportMultiplier = 0.75d;
    internal const int RetainedHostCacheLimit = 128;
    internal static readonly TimeSpan ScrollIdleRealizationDelay = TimeSpan.FromMilliseconds(90);
    private ScrollViewer? _scrollViewer;
    private readonly DispatcherTimer _scrollIdleTimer;
    private readonly LinkedList<TranscriptTurnControl> _retainedHostLru = [];
    private readonly Dictionary<TranscriptTurnControl, LinkedListNode<TranscriptTurnControl>> _retainedHostNodes = [];
    private Control? _registeredAnchorControl;
    private bool _isAttachedToVisualTree;
    private bool _anchorUpdateQueued;
    private bool _viewportUpdateQueued;

    // Inherit the base ItemsControl control theme/template. Avalonia resolves a templated control's
    // ControlTheme by its concrete style key, so without this the subclass would have no template,
    // no ItemsPresenter, and the transcript would render blank.
    protected override System.Type StyleKeyOverride => typeof(ItemsControl);

    public TranscriptItemsControl()
    {
        ContainerPrepared += OnContainerPrepared;
        ContainerClearing += OnContainerClearing;
        _scrollIdleTimer = new DispatcherTimer
        {
            Interval = ScrollIdleRealizationDelay,
        };
        _scrollIdleTimer.Tick += OnScrollIdleTimerTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        AttachScrollViewer();
        QueueViewportRealizationUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToVisualTree = false;
        _scrollIdleTimer.Stop();
        SetRegisteredAnchor(null);
        DetachScrollViewer();
        base.OnDetachedFromVisualTree(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
        => new LeafAutomationPeer(this);

    private void AttachScrollViewer()
    {
        var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (ReferenceEquals(_scrollViewer, scrollViewer))
            return;

        DetachScrollViewer();
        _scrollViewer = scrollViewer;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewportChanged;
            _scrollViewer.SizeChanged += OnScrollViewportSizeChanged;
        }
    }

    private void DetachScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollViewportChanged;
            _scrollViewer.SizeChanged -= OnScrollViewportSizeChanged;
            _scrollViewer = null;
        }

        _anchorUpdateQueued = false;
        _viewportUpdateQueued = false;
    }

    private void OnScrollViewportChanged(object? sender, ScrollChangedEventArgs e)
    {
        QueueAnchorUpdate();
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }

    private void OnScrollViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueAnchorUpdate();
        QueueViewportRealizationUpdate();
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        QueueAnchorUpdate();
        QueueViewportRealizationUpdate();
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        var control = e.Container as TranscriptTurnControl
            ?? e.Container.GetVisualDescendants().OfType<TranscriptTurnControl>().FirstOrDefault();
        if (control is not null)
        {
            if (ReferenceEquals(_registeredAnchorControl, e.Container))
                SetRegisteredAnchor(null);
            RemoveRetainedHost(control, releaseHost: true);
        }
    }

    private void OnScrollIdleTimerTick(object? sender, EventArgs e)
    {
        _scrollIdleTimer.Stop();
        QueueViewportRealizationUpdate();
    }

    internal void RealizeCurrentViewportNow()
    {
        _scrollIdleTimer.Stop();
        UpdateViewportRealization();
    }

    private void QueueAnchorUpdate()
    {
        if (_anchorUpdateQueued)
            return;

        _anchorUpdateQueued = true;
        Dispatcher.UIThread.Post(UpdateRegisteredAnchor, DispatcherPriority.Loaded);
    }

    private void UpdateRegisteredAnchor()
    {
        _anchorUpdateQueued = false;
        SetRegisteredAnchor(FindLeadingVisibleContainer());
    }

    private void QueueViewportRealizationUpdate()
    {
        if (_viewportUpdateQueued)
            return;

        _viewportUpdateQueued = true;
        Dispatcher.UIThread.Post(UpdateViewportRealization, DispatcherPriority.Loaded);
    }

    private void UpdateViewportRealization()
    {
        _viewportUpdateQueued = false;
        var scrollViewer = _scrollViewer;
        if (scrollViewer is null || !_isAttachedToVisualTree)
            return;

        var viewportHeight = scrollViewer.Viewport.Height;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            QueueViewportRealizationUpdate();
            return;
        }

        SetRegisteredAnchor(FindLeadingVisibleContainer());
        var cachePixels = viewportHeight * RealizationCacheViewportMultiplier;
        foreach (var control in EnumerateTurnControls())
        {
            var point = control.TranslatePoint(default, scrollViewer);
            var isActive = point is not null
                && point.Value.Y + control.Bounds.Height >= -cachePixels
                && point.Value.Y <= viewportHeight + cachePixels;

            var wasActive = control.IsViewportActive;
            if (isActive)
                RemoveRetainedHost(control, releaseHost: false);

            control.SetViewportActive(isActive, retainHostWhenInactive: !isActive);
            if (!isActive && wasActive && control.HasRetainedViewportHost)
                RetainHost(control);
        }

        TrimRetainedHosts();
    }

    private Control? FindLeadingVisibleContainer()
    {
        var scrollViewer = _scrollViewer;
        if (scrollViewer is null || !_isAttachedToVisualTree)
            return null;

        var viewportHeight = scrollViewer.Viewport.Height;
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0)
            return null;

        foreach (var control in EnumerateTurnControls())
        {
            var point = control.TranslatePoint(default, scrollViewer);
            if (point is null
                || point.Value.Y + control.Bounds.Height < 0
                || point.Value.Y > viewportHeight)
            {
                continue;
            }

            return (Control?)control.FindAncestorOfType<ContentPresenter>() ?? control;
        }

        return null;
    }

    private IEnumerable<TranscriptTurnControl> EnumerateTurnControls()
    {
        var itemsHost = ItemsPanelRoot;
        return itemsHost is null
            ? Enumerable.Empty<TranscriptTurnControl>()
            : itemsHost.GetVisualDescendants().OfType<TranscriptTurnControl>();
    }

    private void SetRegisteredAnchor(Control? control)
    {
        if (ReferenceEquals(_registeredAnchorControl, control))
            return;

        if (_scrollViewer is not null && _registeredAnchorControl is not null)
            _scrollViewer.UnregisterAnchorCandidate(_registeredAnchorControl);

        _registeredAnchorControl = control;
        if (_scrollViewer is not null && _registeredAnchorControl is not null)
            _scrollViewer.RegisterAnchorCandidate(_registeredAnchorControl);
    }

    private void RetainHost(TranscriptTurnControl control)
    {
        RemoveRetainedHost(control, releaseHost: false);
        var node = _retainedHostLru.AddLast(control);
        _retainedHostNodes.Add(control, node);
    }

    private void RemoveRetainedHost(TranscriptTurnControl control, bool releaseHost)
    {
        if (_retainedHostNodes.Remove(control, out var node))
            _retainedHostLru.Remove(node);

        if (releaseHost)
            control.ReleaseCachedViewportHost();
    }

    private void TrimRetainedHosts()
    {
        while (_retainedHostLru.Count > RetainedHostCacheLimit)
        {
            var node = _retainedHostLru.First!;
            _retainedHostLru.RemoveFirst();
            _retainedHostNodes.Remove(node.Value);
            node.Value.ReleaseCachedViewportHost();
        }
    }

    private sealed class LeafAutomationPeer : ControlAutomationPeer
    {
        public LeafAutomationPeer(Control owner) : base(owner)
        {
        }

        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => [];
    }
}
