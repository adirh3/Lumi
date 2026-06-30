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

    /// <summary>
    /// The realized item-host (a StackPanel of ContentPresenters) for this turn, cached on the
    /// stable turn object so switching away from and back to a chat reuses the already-built and
    /// already-parsed transcript controls instead of rebuilding them. Owned by the turn; adopted
    /// by whichever <see cref="TranscriptTurnControl"/> currently renders it, and released by
    /// <see cref="ReleaseRealizedHost"/> when the turn leaves the mounted window.
    /// </summary>
    internal StackPanel? RealizedItemsHost { get; set; }

    public int IndexOf(TranscriptItem item) => Items.IndexOf(item);

    public bool Remove(TranscriptItem item) => Items.Remove(item);

    /// <summary>Tears down and drops the cached realized host so its controls can be collected.</summary>
    internal void ReleaseRealizedHost()
    {
        if (RealizedItemsHost is null)
            return;

        TranscriptTurnControl.ReleaseHost(RealizedItemsHost);
        RealizedItemsHost = null;
    }
}

public readonly record struct TranscriptTurnControlDiagnosticsSnapshot(
    int ControlCreateCount,
    int ItemHostCreateCount);

public sealed class TranscriptTurnControl : UserControl
{
    private TranscriptTurn? _turn;
    private StackPanel? _host;
    private bool _isAttachedToVisualTree;
    private bool _isSubscribedToTurnItems;
    private bool _realizationPending;
    private static int _controlCreateCount;
    private static int _itemHostCreateCount;

    // Matches TranscriptPagingOptions.EstimatedPixelsPerWeightUnit so a placeholder reserves roughly
    // the same height the pager assumes for an unmeasured turn.
    private const double PlaceholderPixelsPerWeightUnit = 56d;

    public static readonly StyledProperty<TranscriptTurn?> TurnProperty =
        AvaloniaProperty.Register<TranscriptTurnControl, TranscriptTurn?>(nameof(Turn));

    private static readonly AttachedProperty<IDisposable?> ItemVisibilityBindingProperty =
        AvaloniaProperty.RegisterAttached<TranscriptTurnControl, Control, IDisposable?>("ItemVisibilityBinding");

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

        VerifyUiThread();
        UnsubscribeFromTurnItems(oldTurn);
        ReleaseAdoptedHost();

        _turn = newTurn;

        SubscribeToTurnItems(newTurn);
        if (newTurn is not null && Bounds.Height > 0)
            newTurn.MeasuredHeight = Bounds.Height;

        AdoptHost();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
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
                    ClearItemHost(host.Children[e.OldStartingIndex]);
                    host.Children.RemoveAt(e.OldStartingIndex);
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
                    ClearItemHost(host.Children[index]);
                    host.Children[index] = CreateItemHost(GetTranscriptItem(e.NewItems[i]));
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

        // Defer the heavy realization (host build for a fresh turn, or visual-tree re-measure for a
        // retained one) so a chat switch doesn't measure every mounted turn in one synchronous layout
        // pass — the long freeze the user feels. Reserve the turn's known height meanwhile so the
        // scrollbar and scroll anchor stay correct, then let the scheduler realize bottom-first in
        // small frame-budgeted batches.
        ReservePlaceholderHeight();
        _realizationPending = true;
        TranscriptRealizationScheduler.Instance.Request(this);
    }

    // Performs the deferred heavy work for this turn. Invoked by the realization scheduler (or
    // directly when an immediate realize is required, e.g. scrolling to a searched turn).
    internal void RealizePendingHost()
    {
        VerifyUiThread();
        _realizationPending = false;

        if (!_isAttachedToVisualTree || _turn is null)
            return;

        var host = _turn.RealizedItemsHost;
        if (host is null)
        {
            host = new StackPanel
            {
                Spacing = TranscriptLayoutMetrics.TurnSpacing
            };

            foreach (var item in _turn.Items)
                host.Children.Add(CreateItemHost(item));

            _turn.RealizedItemsHost = host;
        }
        else
        {
            // Defensive: detach from any stale owner before re-parenting (one-parent rule).
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
        _host = null;
    }

    private void RebuildHostChildren()
    {
        VerifyUiThread();
        if (!_isAttachedToVisualTree || _host is null || _turn is null)
            return;

        for (var i = _host.Children.Count - 1; i >= 0; i--)
            ClearItemHost(_host.Children[i]);
        _host.Children.Clear();

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

        for (var i = host.Children.Count - 1; i >= 0; i--)
            ClearItemHost(host.Children[i]);
        host.Children.Clear();

        foreach (var item in items)
            host.Children.Add(CreateItemHost(item));
    }

    // Tears down a cached host's children (disposing visibility bindings) so its controls
    // can be collected. Called when the owning turn is evicted from the mounted window.
    internal static void ReleaseHost(StackPanel host)
    {
        for (var i = host.Children.Count - 1; i >= 0; i--)
            ClearItemHost(host.Children[i]);
        host.Children.Clear();
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
        // means a switch-away/switch-back re-parents the SAME instances, so retained markdown
        // (StrataMarkdown.RetainContentOnDetach) and the rest of the subtree are reused, not rebuilt.
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

    private static void ClearItemHost(Control host)
    {
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
