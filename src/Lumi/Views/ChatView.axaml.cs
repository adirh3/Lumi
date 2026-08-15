using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    public static readonly StyledProperty<bool> ShowInternalTitleProperty =
        AvaloniaProperty.Register<ChatView, bool>(nameof(ShowInternalTitle), true);

    public static readonly StyledProperty<bool> UseShellChromeProperty =
        AvaloniaProperty.Register<ChatView, bool>(nameof(UseShellChrome), true);

    private StrataChatShell? _chatShell;
    private StrataChatComposer? _composer;
    private Panel? _composerSpacer;
    private Panel? _dropOverlay;
    private TranscriptItemsControl? _transcript;
    private ScrollViewer? _transcriptScrollViewer;
    private ScrollViewer? _loadingPreviewScrollViewer;
    private TranscriptItemsControl? _loadingPreviewTranscript;
    private Border? _transcriptTopSpacer;
    private Border? _transcriptBottomSpacer;
    private Border? _focusedHistoryBanner;

    // Transcript "materialize" reveal (replaces the old opaque loading slab): while a chat loads or
    // its turns are still realizing, the transcript is hidden instantly so the turn-growth + scroll
    // re-pin settle is never seen; once settled it gently fades and rises back into place, so the
    // load gap shows Lumi's real presence-lit surface and the content reads as composing in.
    private Transitions? _transcriptRevealTransitions;
    private static readonly Avalonia.Media.Transformation.TransformOperations _transcriptHiddenTransform =
        Avalonia.Media.Transformation.TransformOperations.Parse("translateY(10px)");
    private static readonly Avalonia.Media.Transformation.TransformOperations _transcriptShownTransform =
        Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");

    private ChatViewModel? _subscribedVm;
    private Chat? _lastObservedCurrentChat;
    private ObservableCollection<TranscriptTurn>? _subscribedMountedTurns;
    private Border? _worktreeHighlight;
    private Button? _localToggleBtn;
    private Button? _worktreeToggleBtn;
    private bool _worktreeHighlightUpdateQueued;
    private bool _isApplyingTranscriptMutation;
    private bool _isInitiatingTranscriptMutation;
    private bool _viewportEvaluationQueued;
    private bool _viewportEvaluationRequested;
    private bool _isTranscriptScrollbarDragging;
    private Control? _transcriptScrollbarCaptureSource;
    private TranscriptPagingDirection _pendingTranscriptPagingDirection;
    private bool _hasTranscriptViewportOffset;
    private double _lastTranscriptViewportOffset;
    private int _initialTranscriptTailSyncVersion;
    private int _resizeRestoreVersion;
    private int _tailRecoveryVersion;
    private int _loadingPreviewSyncVersion;
    private bool _ownsTranscriptRealization;
    private int _externalAnchorRestoreVersion;
    private double _deferredScrollbarDragCompensation;
    private const int FocusedHistoryContextRadius = 2;
    private readonly ObservableCollection<TranscriptTurn> _focusedHistoryTurns = [];
    private bool _isFocusedHistoryActive;
    private bool _wasFollowingTailBeforeFocusedHistory;
    private ScrollAnchorState? _anchorBeforeFocusedHistory;
    private int _focusedHistoryVersion;
    private ScrollAnchorState? _pendingTranscriptRebuildAnchor;
    private bool _pendingTranscriptRebuildWasFollowingTail;

    // ── Ctrl+F search state ──
    private Border? _searchBar;
    private TextBox? _searchInput;
    private TextBlock? _searchMatchCounter;
    private readonly List<SearchHit> _searchHits = [];
    private int _currentHitIndex = -1;
    private SelectableTextBlock? _highlightedStb;
    private System.Threading.CancellationTokenSource? _searchDebounce;

    /// <summary>A match against a TranscriptItem's raw content, with the occurrence index within that item.</summary>
    private sealed record SearchHit(TranscriptTurn Turn, TranscriptItem Item, int OccurrenceInItem, string Query);

    private static readonly string ClipboardImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "clipboard-images");
    private static readonly DataFormat<string> LumiChatContextClipboardFormat =
        DataFormat.CreateStringApplicationFormat("lumi-chat-context-v1");

    private sealed record ClipboardCopyPayload(
        string Text,
        List<string> AttachmentPaths,
        List<string> SkillNames,
        List<string> Sources);

    [JsonSerializable(typeof(ClipboardCopyPayload))]
    private partial class ClipboardJsonContext : JsonSerializerContext;

    private sealed record ScrollAnchorState(string StableId, double ViewportY, long ScrollGeneration);

    public ChatView()
    {
        InitializeComponent();
    }

    public bool ShowInternalTitle
    {
        get => GetValue(ShowInternalTitleProperty);
        set => SetValue(ShowInternalTitleProperty, value);
    }

    public bool UseShellChrome
    {
        get => GetValue(UseShellChromeProperty);
        set => SetValue(UseShellChromeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UseShellChromeProperty)
            ApplyShellChrome();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        ApplyShellChrome();
        _composer = this.FindControl<StrataChatComposer>("Composer");
        if (_composer is not null)
            _composer.ClipboardPasteInterceptFormats = new DataFormat[] { LumiChatContextClipboardFormat, DataFormat.Text };
        _composerSpacer = this.FindControl<Panel>("ComposerSpacer");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");
        _transcript = this.FindControl<TranscriptItemsControl>("Transcript");
        _loadingPreviewScrollViewer = this.FindControl<ScrollViewer>("LoadingPreviewScrollViewer");
        _loadingPreviewTranscript = this.FindControl<TranscriptItemsControl>("LoadingPreviewTranscript");
        _transcriptTopSpacer = this.FindControl<Border>("TranscriptTopSpacer");
        _transcriptBottomSpacer = this.FindControl<Border>("TranscriptBottomSpacer");
        _focusedHistoryBanner = this.FindControl<Border>("FocusedHistoryBanner");
        if (_transcript is not null)
            _transcript.LocalTurnHeightChanged += OnLocalTurnHeightChanged;
        ApplyAgentAutomationLandmarks();

        // Slide-up animation for coding strip
        var codingStrip = this.FindControl<Border>("CodingStrip");
        if (codingStrip is not null)
        {
            codingStrip.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && codingStrip.IsVisible)
                    PlaySlideUpAnimation(codingStrip);
            };
        }

        // Keep the shell spacer height in sync with the real composer container
        var composerContainer = this.FindControl<StackPanel>("ComposerContainer");
        if (composerContainer is not null && _composerSpacer is not null)
        {
            composerContainer.SizeChanged += (_, _) =>
                _composerSpacer.Height = composerContainer.Bounds.Height;
        }

        // Worktree toggle sliding highlight
        _worktreeHighlight = this.FindControl<Border>("WorktreeToggleHighlight");
        _localToggleBtn = this.FindControl<Button>("LocalToggleBtn");
        _worktreeToggleBtn = this.FindControl<Button>("WorktreeToggleBtn");

        var togglePanel = this.FindControl<StackPanel>("WorktreeTogglePanel");
        if (togglePanel is not null)
            togglePanel.SizeChanged += (_, _) => QueueWorktreeToggleHighlightUpdate();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(StrataFileAttachment.OpenRequestedEvent, OnFileAttachmentOpenRequested);
        AddHandler(StrataChatMessage.CopyRequestedEvent, OnCopyMessageRequested);
        AddHandler(StrataChatMessage.CopyTurnRequestedEvent, OnCopyTurnRequested);
        AddHandler(StrataChatMessage.ForkRequestedEvent, OnForkRequested);
        AddHandler(KeyDownEvent, OnLinkedChatKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        SizeChanged += OnChatViewSizeChanged;

        // ── Search bar controls ──
        _searchBar = this.FindControl<Border>("SearchBar");
        _searchInput = this.FindControl<TextBox>("SearchInput");
        _searchMatchCounter = this.FindControl<TextBlock>("SearchMatchCounter");

        var searchPrevBtn = this.FindControl<Button>("SearchPrevBtn");
        var searchNextBtn = this.FindControl<Button>("SearchNextBtn");
        var searchCloseBtn = this.FindControl<Button>("SearchCloseBtn");
        var exitFocusedHistoryButton = this.FindControl<Button>("ExitFocusedHistoryButton");

        if (_searchInput is not null)
        {
            _searchInput.TextChanged += (_, _) => OnSearchQueryChanged();
            _searchInput.KeyDown += OnSearchInputKeyDown;
        }

        if (searchPrevBtn is not null) searchPrevBtn.Click += (_, _) => NavigateSearchMatch(-1);
        if (searchNextBtn is not null) searchNextBtn.Click += (_, _) => NavigateSearchMatch(1);
        if (searchCloseBtn is not null) searchCloseBtn.Click += (_, _) => CloseSearch();
        if (exitFocusedHistoryButton is not null)
            exitFocusedHistoryButton.Click += async (_, _) => await ExitFocusedHistoryAsync(restoreViewport: true);
    }

    private void ApplyShellChrome()
    {
        _chatShell?.Classes.Set("flat-window", !UseShellChrome);
    }

    private void ApplyAgentAutomationLandmarks()
    {
        if (_chatShell is not null)
        {
            AutomationProperties.SetName(_chatShell, "ChatShell - main chat surface");
            AutomationProperties.SetHelpText(_chatShell, "Contains the header, transcript, and composer for the active Lumi chat.");
        }

        if (_composer is not null)
        {
            AutomationProperties.SetName(_composer, "Composer - type and send chat prompts");
            AutomationProperties.SetHelpText(_composer, "Primary text input for Lumi chat prompts. Use this for new messages.");
        }

        if (_transcript is not null)
        {
            AutomationProperties.SetName(_transcript, "Transcript - mounted chat turns");
            AutomationProperties.SetHelpText(_transcript, "Virtualized transcript items rendered from ChatViewModel.MountedTranscriptTurns.");
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeMountedTurns();
        UnsubscribeFromViewModel();
        ResetFocusedHistory();
        ResetSearchState();
        _viewportEvaluationRequested = false;
        ResetTranscriptPagingInputState();
        _resizeRestoreVersion++;
        _tailRecoveryVersion++;
        _loadingPreviewSyncVersion++;
        _lastObservedCurrentChat = null;

        if (DataContext is ChatViewModel vm)
        {
            _subscribedVm = vm;
            _lastObservedCurrentChat = vm.CurrentChat;
            vm.ScrollToEndRequested += OnScrollToEndRequested;
            vm.UserMessageSent += OnUserMessageSent;
            vm.TranscriptRebuilding += OnTranscriptRebuilding;
            vm.TranscriptRebuilt += OnTranscriptRebuilt;
            vm.LoadingTranscriptPreviewReady += OnLoadingTranscriptPreviewReady;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.AttachFilesRequested += OnAttachFilesRequested;
            vm.ClipboardPasteRequested += OnClipboardPasteRequested;
            vm.CopyToClipboardRequested += OnCopyToClipboardRequested;
            vm.FocusComposerRequested += FocusComposer;
            vm.FocusComposerAtEndRequested += FocusComposerAtEnd;
            vm.WorkspaceJumpToTurnRequested += OnWorkspaceJumpToTurnRequested;
            SubscribeToMountedTurns(vm.MountedTranscriptTurns);
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
            if (vm.MarkLoadingTranscriptPreviewPresented())
                QueueLoadingTranscriptPreviewTailSync();
            QueueInitialTranscriptTailSyncIfNeeded(vm);
            // Match the transcript's materialize state to the new surface: if it's already loading or
            // about to realize, start hidden so it composes in rather than flashing placeholders.
            SetTranscriptMaterialized(!vm.IsChatSurfaceLoading);
        }

        QueueWorktreeToggleHighlightUpdate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachTranscriptScrollViewer();
        UnsubscribeMountedTurns();
        UnsubscribeFromViewModel();
        _subscribedVm?.StopVoiceIfRecording();
        base.OnDetachedFromVisualTree(e);
    }

    public void FocusComposer()
    {
        _composer?.FocusInput();
    }

    private void FocusComposerAtEnd() => _composer?.FocusInputAtEnd();

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedVm is null) return;
        _subscribedVm.ScrollToEndRequested -= OnScrollToEndRequested;
        _subscribedVm.UserMessageSent -= OnUserMessageSent;
        _subscribedVm.TranscriptRebuilding -= OnTranscriptRebuilding;
        _subscribedVm.TranscriptRebuilt -= OnTranscriptRebuilt;
        _subscribedVm.LoadingTranscriptPreviewReady -= OnLoadingTranscriptPreviewReady;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm.AttachFilesRequested -= OnAttachFilesRequested;
        _subscribedVm.ClipboardPasteRequested -= OnClipboardPasteRequested;
        _subscribedVm.CopyToClipboardRequested -= OnCopyToClipboardRequested;
        _subscribedVm.FocusComposerRequested -= FocusComposer;
        _subscribedVm.FocusComposerAtEndRequested -= FocusComposerAtEnd;
        _subscribedVm.WorkspaceJumpToTurnRequested -= OnWorkspaceJumpToTurnRequested;
        // Clear the realizing gate so a view detach mid-open can't leave the overlay stuck up on the VM:
        // a suspended OpenTranscriptAtLatestAsync won't reach its gate-clearing finally once _subscribedVm
        // is null / the sync version has been bumped below.
        EndTranscriptRealization();
        _subscribedVm.TryClearLoadingTranscriptPreview();
        _subscribedVm = null;
        _lastObservedCurrentChat = null;
        _initialTranscriptTailSyncVersion++;
        _resizeRestoreVersion++;
        _tailRecoveryVersion++;
        _loadingPreviewSyncVersion++;
        _pendingTranscriptRebuildAnchor = null;
        _pendingTranscriptRebuildWasFollowingTail = false;
    }

    private void OnLoadingTranscriptPreviewReady()
    {
        if (_subscribedVm?.MarkLoadingTranscriptPreviewPresented() != true)
            return;

        QueueLoadingTranscriptPreviewTailSync();
    }

    private void QueueLoadingTranscriptPreviewTailSync()
    {
        var version = ++_loadingPreviewSyncVersion;
        Dispatcher.UIThread.Post(
            () => _ = ScrollLoadingTranscriptPreviewToEndAsync(version),
            DispatcherPriority.Loaded);
    }

    private async Task ScrollLoadingTranscriptPreviewToEndAsync(int version)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            if (version != _loadingPreviewSyncVersion
                || _subscribedVm is null
                || _loadingPreviewScrollViewer is null
                || _loadingPreviewTranscript is null)
            {
                return;
            }

            var maximum = Math.Max(
                0,
                _loadingPreviewScrollViewer.Extent.Height - _loadingPreviewScrollViewer.Viewport.Height);
            _loadingPreviewScrollViewer.Offset = _loadingPreviewScrollViewer.Offset.WithY(maximum);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            if (version != _loadingPreviewSyncVersion || _subscribedVm is null)
                return;
            _loadingPreviewTranscript.RealizeCurrentViewportNow();
            if (maximum > 0)
                return;
        }
    }

    private void SubscribeToMountedTurns(ObservableCollection<TranscriptTurn> mountedTurns)
    {
        UnsubscribeMountedTurns();
        _subscribedMountedTurns = mountedTurns;
        _subscribedMountedTurns.CollectionChanged += OnMountedTurnsChanged;
        if (!_isFocusedHistoryActive && _transcript is not null)
            _transcript.ItemsSource = mountedTurns;
    }

    private void UnsubscribeMountedTurns()
    {
        if (_subscribedMountedTurns is null)
            return;
        _subscribedMountedTurns.CollectionChanged -= OnMountedTurnsChanged;
        _subscribedMountedTurns.CollectionChanged -= OnMountedTurnsChanged;
        if (!_isFocusedHistoryActive
            && _transcript is not null
            && ReferenceEquals(_transcript.ItemsSource, _subscribedMountedTurns))
        {
            _transcript.ItemsSource = null;
        }
        _subscribedMountedTurns = null;
    }

    private void EnsureTranscriptScrollViewer()
    {
        if (_transcriptScrollViewer is not null || _chatShell is null)
            return;

        _transcriptScrollViewer = _chatShell.TranscriptScrollViewer;
        if (_transcriptScrollViewer is null)
        {
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
            return;
        }

        _chatShell.TranscriptViewportChanged += OnTranscriptViewportChanged;
        _chatShell.JumpToLatestRequested += OnJumpToLatestRequested;
        _transcriptScrollViewer.SizeChanged += OnTranscriptViewportSizeChanged;
        _transcriptScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnTranscriptPagingWheel, RoutingStrategies.Bubble, handledEventsToo: true);
        _transcriptScrollViewer.AddHandler(InputElement.KeyDownEvent, OnTranscriptPagingKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _transcriptScrollViewer.AddHandler(InputElement.ScrollGestureEvent, OnTranscriptPagingScrollGesture, RoutingStrategies.Bubble, handledEventsToo: true);
        _transcriptScrollViewer.AddHandler(InputElement.PointerPressedEvent, OnTranscriptScrollViewerPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        _transcriptScrollViewer.AddHandler(InputElement.PointerReleasedEvent, OnTranscriptScrollViewerPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void DetachTranscriptScrollViewer()
    {
        if (_transcriptScrollViewer is null)
            return;

        if (_chatShell is not null)
        {
            _chatShell.TranscriptViewportChanged -= OnTranscriptViewportChanged;
            _chatShell.JumpToLatestRequested -= OnJumpToLatestRequested;
        }
        _transcriptScrollViewer.SizeChanged -= OnTranscriptViewportSizeChanged;
        _transcriptScrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnTranscriptPagingWheel);
        _transcriptScrollViewer.RemoveHandler(InputElement.KeyDownEvent, OnTranscriptPagingKeyDown);
        _transcriptScrollViewer.RemoveHandler(InputElement.ScrollGestureEvent, OnTranscriptPagingScrollGesture);
        _transcriptScrollViewer.RemoveHandler(InputElement.PointerPressedEvent, OnTranscriptScrollViewerPointerPressed);
        _transcriptScrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnTranscriptScrollViewerPointerReleased);
        _transcriptScrollViewer = null;
        ClearTranscriptScrollbarDrag();
        ResetTranscriptPagingInputState();
    }

    private void OnMountedTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueExternalPrependAnchorRestore(e);
        QueueViewportRecoveryAfterMountedTurnsChanged();
    }

    // Turn add/remove churn (typing indicator, tool-group cleanup, summary collapse)
    // changes the live scroll extent without producing a height delta on an existing turn.
    private void QueueViewportRecoveryAfterMountedTurnsChanged()
    {
        _chatShell?.NotifyTranscriptLayoutChanged();

        if (_chatShell is null
            || _subscribedVm is null
            || _subscribedVm.CurrentChat is null
            || _subscribedVm.IsLoadingChat)
        {
            return;
        }

        if (_subscribedVm.MaintainsStableTranscriptMembership)
            return;

        if (_isApplyingTranscriptMutation)
        {
            _viewportEvaluationRequested = true;
            return;
        }

        QueueTranscriptViewportEvaluation();
    }

    private void OnLocalTurnHeightChanged(object? sender, TranscriptLocalHeightChangedEventArgs e)
        => ApplyLocalTurnHeightChange(e.Control, e.PreviousHeight, e.NewHeight);

    private void ApplyLocalTurnHeightChange(
        TranscriptTurnControl control,
        double previousHeight,
        double newHeight)
    {
        if (!double.IsFinite(previousHeight)
            || !double.IsFinite(newHeight)
            || previousHeight <= 0
            || newHeight <= 0)
        {
            return;
        }

        var delta = newHeight - previousHeight;
        if (Math.Abs(delta) < 0.5)
            return;

        if (_chatShell is null
            || _transcriptScrollViewer is null
            || _subscribedVm is null
            || _isApplyingTranscriptMutation)
        {
            return;
        }

        if (_chatShell.IsFollowingTail && !_subscribedVm.HasUnmountedTranscriptTail)
        {
            if (!_subscribedVm.MaintainsStableTranscriptMembership)
                QueueTranscriptViewportEvaluation();
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        var point = control.TranslatePoint(default, _transcriptScrollViewer);
        if (point is null)
            return;

        // Compensate when the turn was fully above the viewport before this local resize. A short
        // placeholder can realize into a tall turn that now crosses the viewport boundary; checking
        // the new Bounds would misclassify that transition and let every item below jump by delta.
        if (point.Value.Y + previousHeight > 0)
            return;

        if (_isTranscriptScrollbarDragging)
        {
            _deferredScrollbarDragCompensation += delta;
            return;
        }

        _chatShell.CompensateForContentAbove(delta);
    }

    private void QueueExternalPrependAnchorRestore(NotifyCollectionChangedEventArgs e)
    {
        if (_isInitiatingTranscriptMutation
            || _isApplyingTranscriptMutation
            || e.Action != NotifyCollectionChangedAction.Add
            || e.NewStartingIndex != 0
            || e.NewItems is null
            || e.NewItems.Count == 0
            || _subscribedVm is null
            || _subscribedVm.IsLoadingChat)
        {
            return;
        }

        var addedIds = e.NewItems
            .Cast<TranscriptTurn>()
            .Select(static turn => turn.StableId)
            .ToHashSet(StringComparer.Ordinal);
        var anchor = CaptureAnchor(addedIds);
        var restoreVersion = ++_externalAnchorRestoreVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (restoreVersion == _externalAnchorRestoreVersion)
                RestoreAnchor(anchor, "external-prepend");
        }, DispatcherPriority.Loaded);
    }

    private void OnScrollToEndRequested() => _chatShell?.NotifyTranscriptContentChanged();

    private void OnTranscriptScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var captureSource = FindScrollbarInteractionControl(e.Source);
        if (captureSource is null)
            return;

        _isTranscriptScrollbarDragging = true;
        _deferredScrollbarDragCompensation = 0;
        if (_transcriptScrollViewer is not null)
        {
            _lastTranscriptViewportOffset = _transcriptScrollViewer.Offset.Y;
            _hasTranscriptViewportOffset = true;
        }
        SetTranscriptScrollbarCaptureSource(captureSource);
    }

    private void OnTranscriptScrollViewerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndTranscriptScrollbarDrag();
    }

    private void OnTranscriptScrollbarPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndTranscriptScrollbarDrag();
    }

    private void EndTranscriptScrollbarDrag()
    {
        if (!_isTranscriptScrollbarDragging)
            return;

        var deferredCompensation = _deferredScrollbarDragCompensation;
        _deferredScrollbarDragCompensation = 0;
        ClearTranscriptScrollbarDrag();
        if (Math.Abs(deferredCompensation) >= 0.5
            && _chatShell is not null
            && !_chatShell.IsFollowingTail)
        {
            _chatShell.CompensateForContentAbove(deferredCompensation);
        }

        TryRestoreFollowTailAtActualBottom(_pendingTranscriptPagingDirection);
        QueueTranscriptViewportEvaluation();
    }

    private void ClearTranscriptScrollbarDrag()
    {
        _isTranscriptScrollbarDragging = false;
        _deferredScrollbarDragCompensation = 0;
        SetTranscriptScrollbarCaptureSource(null);
    }

    private void OnTranscriptPagingWheel(object? sender, PointerWheelEventArgs e)
    {
        var direction = e.Delta.Y switch
        {
            > ChatScrollPolicy.FractionalEpsilon => TranscriptPagingDirection.TowardOlder,
            < -ChatScrollPolicy.FractionalEpsilon => TranscriptPagingDirection.TowardNewer,
            _ => TranscriptPagingDirection.None,
        };

        if (!CanNestedScrollViewerConsume(e.Source, direction))
            RecordTranscriptPagingDirection(direction);
    }

    private void OnTranscriptPagingKeyDown(object? sender, KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.PageUp or Key.Up or Key.Home => TranscriptPagingDirection.TowardOlder,
            Key.PageDown or Key.Down or Key.End => TranscriptPagingDirection.TowardNewer,
            _ => TranscriptPagingDirection.None,
        };

        if (e.Key is Key.PageUp or Key.PageDown)
        {
            if (!CanNestedScrollViewerConsume(e.Source, direction))
                RecordTranscriptPagingDirection(direction);
            return;
        }

        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End
            && IsTranscriptScrollbarInteraction(e.Source))
        {
            RecordTranscriptPagingDirection(direction);
        }
    }

    private void OnTranscriptPagingScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        var direction = e.Delta.Y switch
        {
            < -ChatScrollPolicy.FractionalEpsilon => TranscriptPagingDirection.TowardOlder,
            > ChatScrollPolicy.FractionalEpsilon => TranscriptPagingDirection.TowardNewer,
            _ => TranscriptPagingDirection.None,
        };

        if (!CanNestedScrollViewerConsume(e.Source, direction))
            RecordTranscriptPagingDirection(direction);
    }

    private bool CanNestedScrollViewerConsume(object? source, TranscriptPagingDirection direction)
    {
        if (direction == TranscriptPagingDirection.None || source is not Control control)
            return false;

        for (Visual? current = control; current is not null; current = current.GetVisualParent())
        {
            if (current is not ScrollViewer nestedScrollViewer)
                continue;

            if (ReferenceEquals(nestedScrollViewer, _transcriptScrollViewer))
                return false;

            var maxOffset = Math.Max(0, nestedScrollViewer.Extent.Height - nestedScrollViewer.Viewport.Height);
            var canConsume = direction switch
            {
                TranscriptPagingDirection.TowardOlder =>
                    nestedScrollViewer.Offset.Y > ChatScrollPolicy.FractionalEpsilon,
                TranscriptPagingDirection.TowardNewer =>
                    maxOffset - nestedScrollViewer.Offset.Y > ChatScrollPolicy.FractionalEpsilon,
                _ => false,
            };
            if (canConsume)
                return true;
        }

        return false;
    }

    private bool IsTranscriptScrollbarInteraction(object? source)
    {
        var interactionControl = FindScrollbarInteractionControl(source);
        var owningScrollViewer = interactionControl?.FindAncestorOfType<ScrollViewer>();
        return ReferenceEquals(owningScrollViewer, _transcriptScrollViewer);
    }

    private void RecordTranscriptPagingDirection(TranscriptPagingDirection direction)
    {
        if (direction == TranscriptPagingDirection.None)
            return;

        _transcript?.DeferViewportRealizationUntilScrollIdle();
        _pendingTranscriptPagingDirection = direction;
        TryRestoreFollowTailAtActualBottom(direction);
        if (!_isTranscriptScrollbarDragging)
            QueueTranscriptViewportEvaluation();
    }

#if DEBUG
    internal void RequestOlderHistoryForDiagnostics()
        => RecordTranscriptPagingDirection(TranscriptPagingDirection.TowardOlder);

    internal void DriveOlderHistoryStepForDiagnostics()
    {
        if (_transcriptScrollViewer is not null && _transcriptScrollViewer.Offset.Y > 0)
            _transcriptScrollViewer.Offset = _transcriptScrollViewer.Offset.WithY(0);

        RequestOlderHistoryForDiagnostics();
    }

    internal async Task<object> SearchForDiagnosticsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query is required.", nameof(query));
        if (_searchInput is null || _transcript is null || _subscribedVm is null)
            throw new InvalidOperationException("The active chat search surface is unavailable.");

        OpenSearch();
        _searchInput.Text = query;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        ExecuteSearch();

        if (_searchHits.Count == 0)
        {
            return new
            {
                matchCount = 0,
                focusedHistory = false,
                displayTurnCount = _transcript.ItemCount,
                mainMountedTurnCount = _subscribedVm.MountedTranscriptTurns.Count
            };
        }

        var hit = _searchHits[0];
        var control = await EnsureTurnRealizedAsync(hit.Turn);
        if (control is not null)
            HighlightHit(hit);

        return new
        {
            matchCount = _searchHits.Count,
            focusedHistory = _isFocusedHistoryActive,
            displayTurnCount = _transcript.ItemCount,
            mainMountedTurnCount = _subscribedVm.MountedTranscriptTurns.Count,
            targetStableId = hit.Turn.StableId,
            targetVisible = control is not null
        };
    }
#endif

    private void TryRestoreFollowTailAtActualBottom(TranscriptPagingDirection direction)
    {
        if (direction != TranscriptPagingDirection.TowardNewer
            || _subscribedVm is null
            || _chatShell is null
            || _subscribedVm.HasUnmountedTranscriptTail
            || _chatShell.CurrentDistanceFromBottom > ChatScrollPolicy.DefaultBottomTolerance)
        {
            return;
        }

        _chatShell.EnterFollowTailMode();
        SyncTranscriptPinnedState();
    }

    private void ObserveTranscriptScrollbarPagingDirection(double verticalOffset)
    {
        if (_isTranscriptScrollbarDragging && _hasTranscriptViewportOffset)
        {
            var delta = verticalOffset - _lastTranscriptViewportOffset;
            if (delta > ChatScrollPolicy.FractionalEpsilon)
                _pendingTranscriptPagingDirection = TranscriptPagingDirection.TowardNewer;
            else if (delta < -ChatScrollPolicy.FractionalEpsilon)
                _pendingTranscriptPagingDirection = TranscriptPagingDirection.TowardOlder;

            if (Math.Abs(delta) > ChatScrollPolicy.FractionalEpsilon)
                _deferredScrollbarDragCompensation = 0;
        }

        _lastTranscriptViewportOffset = verticalOffset;
        _hasTranscriptViewportOffset = true;
    }

    private void ResetTranscriptPagingInputState()
    {
        _pendingTranscriptPagingDirection = TranscriptPagingDirection.None;
        _hasTranscriptViewportOffset = false;
        _lastTranscriptViewportOffset = 0;
    }

    private void SetTranscriptScrollbarCaptureSource(Control? captureSource)
    {
        if (ReferenceEquals(_transcriptScrollbarCaptureSource, captureSource))
            return;

        _transcriptScrollbarCaptureSource?.RemoveHandler(
            InputElement.PointerCaptureLostEvent,
            OnTranscriptScrollbarPointerCaptureLost);
        _transcriptScrollbarCaptureSource = captureSource;
        _transcriptScrollbarCaptureSource?.AddHandler(
            InputElement.PointerCaptureLostEvent,
            OnTranscriptScrollbarPointerCaptureLost,
            RoutingStrategies.Direct,
            handledEventsToo: true);
    }

    private static Control? FindScrollbarInteractionControl(object? source)
    {
        if (source is not Control control)
            return null;

        if (control is Thumb or RepeatButton or Track or ScrollBar)
            return control;

        return control.FindAncestorOfType<Thumb>()
            ?? (Control?)control.FindAncestorOfType<RepeatButton>()
            ?? (Control?)control.FindAncestorOfType<Track>()
            ?? control.FindAncestorOfType<ScrollBar>();
    }

    private void SyncTranscriptPinnedState()
    {
        if (_isFocusedHistoryActive || _subscribedVm is null || _chatShell is null)
            return;

        var isActualTailMounted = !_subscribedVm.HasUnmountedTranscriptTail;
        if (!isActualTailMounted && _chatShell.IsFollowingTail)
            _chatShell.PreserveViewport();

        _subscribedVm.UpdateTranscriptScrollState(
            _chatShell.IsFollowingTail && isActualTailMounted,
            _chatShell.IsPinnedToBottom && isActualTailMounted,
            _chatShell.CurrentDistanceFromBottom);
    }

    private void OnJumpToLatestRequested() => JumpToLatest(focusComposer: false);

    private void JumpToLatest(bool focusComposer)
    {
        if (_isFocusedHistoryActive)
        {
            _ = ExitFocusedHistoryAndJumpToLatestAsync(focusComposer);
            return;
        }

        _subscribedVm?.EnsureLatestTranscriptMounted();
        _chatShell?.JumpToLatest();
        SyncTranscriptPinnedState();
        Dispatcher.UIThread.Post(SyncTranscriptPinnedState, DispatcherPriority.Loaded);

        if (focusComposer)
            Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private void OnUserMessageSent()
    {
        JumpToLatest(focusComposer: true);
    }

    private void OnTranscriptRebuilding()
    {
        if (_subscribedVm is null || _chatShell is null || _subscribedVm.IsLoadingChat)
        {
            _pendingTranscriptRebuildAnchor = null;
            _pendingTranscriptRebuildWasFollowingTail = false;
            return;
        }

        if (_isFocusedHistoryActive)
        {
            _pendingTranscriptRebuildAnchor = _anchorBeforeFocusedHistory;
            _pendingTranscriptRebuildWasFollowingTail = _wasFollowingTailBeforeFocusedHistory;
            ResetFocusedHistory();
            return;
        }

        _pendingTranscriptRebuildAnchor = CaptureAnchor();
        _pendingTranscriptRebuildWasFollowingTail = _chatShell.IsFollowingTail
            && !_subscribedVm.HasUnmountedTranscriptTail;
    }

    private async void OnTranscriptRebuilt()
    {
        if (_isFocusedHistoryActive)
            ResetFocusedHistory();

        // Only a load/switch-driven rebuild may raise the loading overlay. RebuildTranscript also fires
        // on incidental in-place rebuilds of the visible chat (stream completion attaching web sources,
        // settings toggles like ShowReasoning/ShowToolCalls, edit/resend) where IsLoadingChat is false —
        // those must NOT flash the full-surface overlay or absorb clicks while the transcript re-realizes.
        // During a genuine load IsLoadingChat is already true here (RebuildTranscript runs inside
        // LoadChatAsync before its finally clears the flag), so raising the gate synchronously keeps the
        // overlay continuously up from load → realization with no blank frame in between.
        var viewModel = _subscribedVm;
        if (viewModel is null)
            return;

        var isLoadingChat = viewModel.IsLoadingChat;
        if (isLoadingChat)
            BeginTranscriptRealization(viewModel);

        var syncVersion = ++_initialTranscriptTailSyncVersion;
        if (isLoadingChat)
        {
            await OpenTranscriptAtLatestAsync(focusComposer: true, searchAfterOpen: true, syncVersion);
            return;
        }

        var ready = await EnsureTranscriptScrollViewerReadyAsync();
        if (!ready
            || _chatShell is null
            || _subscribedVm is null
            || syncVersion != _initialTranscriptTailSyncVersion)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        if (syncVersion != _initialTranscriptTailSyncVersion)
            return;

        if (_pendingTranscriptRebuildWasFollowingTail)
        {
            _chatShell.JumpToLatest();
        }
        else if (_pendingTranscriptRebuildAnchor is { } anchor)
        {
            RestoreAnchor(
                anchor with { ScrollGeneration = _chatShell.ScrollGeneration },
                "transcript-rebuild");
        }

        _pendingTranscriptRebuildAnchor = null;
        _pendingTranscriptRebuildWasFollowingTail = false;
        if (_searchBar?.Classes.Contains("open") == true
            && !string.IsNullOrWhiteSpace(_searchInput?.Text))
        {
            ExecuteSearch();
        }

        _transcript?.RealizeCurrentViewportNow();
        SyncTranscriptPinnedState();
        QueueTranscriptViewportEvaluation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat))
        {
            var currentChat = _subscribedVm?.CurrentChat;
            var chatReferenceChanged = !ReferenceEquals(currentChat, _lastObservedCurrentChat);
            _lastObservedCurrentChat = currentChat;

            if (chatReferenceChanged && currentChat is not null)
            {
                _chatShell?.EnterFollowTailMode();
                SyncTranscriptPinnedState();
            }
        }

        if (e.PropertyName == nameof(ChatViewModel.IsChatSurfaceLoading))
            SetTranscriptMaterialized(!(_subscribedVm?.IsChatSurfaceLoading ?? false));

        if (e.PropertyName == nameof(ChatViewModel.IsWorktreeMode))
            QueueWorktreeToggleHighlightUpdate();

        if (e.PropertyName == nameof(ChatViewModel.IsBusy))
        {
            var busy = _subscribedVm?.IsBusy ?? false;
            if (!busy)
                QueueCompletedAssistantTailRecovery();
        }
    }

    /// <summary>
    /// Drives the transcript's load "materialize". On load/realize start the transcript is hidden
    /// instantly (transitions dropped) so the under-cover turn growth + scroll re-pin is never visible;
    /// once the surface is ready it fades and rises gently back into place. This replaces the old
    /// opaque loading slab — the load gap now shows the real translucent, presence-lit chat surface.
    /// </summary>
    private void SetTranscriptMaterialized(bool ready)
    {
        if (_transcript is null)
            return;

        if (ready)
        {
            if (_subscribedVm?.HasLoadingTranscriptPreview == true)
            {
                _transcript.Transitions = null;
                _transcript.Opacity = 1;
                _transcript.RenderTransform = _transcriptShownTransform;
                return;
            }

            _transcriptRevealTransitions ??= new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(240),
                    Easing = new CubicEaseOut(),
                },
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(320),
                    Easing = new CubicEaseOut(),
                },
            };
            _transcript.Transitions = _transcriptRevealTransitions;
            _transcript.Opacity = 1;
            _transcript.RenderTransform = _transcriptShownTransform;
        }
        else
        {
            // Instant hide: drop transitions so the clear can't animate and reveal the swap mid-fade.
            _transcript.Transitions = null;
            _transcript.Opacity = 0;
            _transcript.RenderTransform = _transcriptHiddenTransform;
        }
    }

    private void QueueInitialTranscriptTailSyncIfNeeded(ChatViewModel viewModel)
    {
        if (viewModel.CurrentChat is null || viewModel.MountedTranscriptTurns.Count == 0)
            return;

        BeginTranscriptRealization(viewModel);
        var syncVersion = ++_initialTranscriptTailSyncVersion;
        Dispatcher.UIThread.Post(
            () => _ = OpenTranscriptAtLatestAsync(focusComposer: false, searchAfterOpen: false, syncVersion),
            DispatcherPriority.Loaded);
    }

    private async Task OpenTranscriptAtLatestAsync(bool focusComposer, bool searchAfterOpen, int syncVersion)
    {
        if (_subscribedVm is null || _chatShell is null)
            return;

        try
        {
            var ready = await EnsureTranscriptScrollViewerReadyAsync();
            if (!ready || _subscribedVm is null || _chatShell is null || syncVersion != _initialTranscriptTailSyncVersion)
                return;

            var chatShell = _chatShell;
            var viewModel = _subscribedVm;
            if (viewModel.CurrentChat is null)
                return;

            chatShell.RequestInitialBottom();
            var shouldPrewarmBoundedTail = viewModel.TryClaimInitialTranscriptTailPrewarm();
            var initialMutation = RunTranscriptMutation(() =>
                viewModel.InitializeMountedTranscript(chatShell.ViewportHeight));
            shouldPrewarmBoundedTail |= initialMutation.Kind == TranscriptWindowMutationKind.Reset;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;

            RunTranscriptMutation(() =>
                viewModel.EnsureMountedTranscriptCoverage(chatShell.ViewportHeight, chatShell.ExtentHeight));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;

            chatShell.NotifyTranscriptLayoutChanged();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;

            SyncTranscriptPinnedState();
            if (focusComposer)
                FocusComposer();
            QueueTranscriptViewportEvaluation();

            if (searchAfterOpen && !string.IsNullOrWhiteSpace(_searchInput?.Text))
                ExecuteSearch();

            if (_transcript is TranscriptItemsControl transcript)
            {
                transcript.RealizeCurrentViewportNow();
                if (shouldPrewarmBoundedTail)
                    transcript.QueueUnmeasuredMountedPrewarm();
            }

            // Keep the loading overlay up (and absorbing clicks) until the deferred, frame-budgeted
            // realization of the viewport-active tail turns has finished, then make a final authoritative re-pin to
            // the now fully-measured bottom. Without this the overlay would clear while turns are still
            // height-only placeholders → the user sees a blank/jumping transcript, and because the
            // bottom turn grows after the initial pin the scroll otherwise settles part-way up.
            await WaitForTranscriptRealizationAsync(chatShell, viewModel, syncVersion);
        }
        finally
        {
            // Only the newest open clears the gate; a superseded open leaves it set for whichever open
            // replaced it (that one clears it once its own realization completes).
            if (syncVersion == _initialTranscriptTailSyncVersion && _subscribedVm is not null)
                EndTranscriptRealization();
        }
    }

    private void BeginTranscriptRealization(ChatViewModel viewModel)
    {
        if (_ownsTranscriptRealization)
            return;

        _ownsTranscriptRealization = true;
        viewModel.BeginTranscriptRealization();
    }

    private void EndTranscriptRealization()
    {
        if (!_ownsTranscriptRealization || _subscribedVm is null)
            return;

        _ownsTranscriptRealization = false;
        _subscribedVm.EndTranscriptRealization();
    }

    private async Task WaitForTranscriptRealizationAsync(StrataChatShell chatShell, ChatViewModel viewModel, int syncVersion)
    {
        var scheduler = TranscriptRealizationScheduler.Instance;

        var isStaticHistory = !viewModel.IsBusy;
        var deadline = DateTime.UtcNow + (isStaticHistory
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromSeconds(2));
        var quietPeriod = isStaticHistory
            ? TimeSpan.FromMilliseconds(160)
            : TimeSpan.FromMilliseconds(48);

        // Reveal only after a wall-clock quiet window, not merely a couple of fast dispatcher frames.
        // Markdown and retained subtrees can finish measuring after the scheduler queue becomes empty;
        // hiding the preview earlier exposes those late height changes as a jump. Streaming chats use a
        // shorter quiet period because their extent is expected to keep changing.
        var requireExtentStability = isStaticHistory;
        var lastExtent = double.NaN;
        DateTime? quietSince = null;

        while (DateTime.UtcNow < deadline)
        {
            chatShell.NotifyTranscriptLayoutChanged();

            await Task.Delay(16);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;

            var extent = chatShell.ExtentHeight;
            var extentStable = !double.IsNaN(lastExtent) && Math.Abs(extent - lastExtent) < 0.5;
            lastExtent = extent;

            var settled = !scheduler.HasPendingWork && (!requireExtentStability || extentStable);
            if (settled)
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince.Value >= quietPeriod)
                    break;
            }
            else
            {
                quietSince = null;
            }
        }

        if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
            return;

        // Final authoritative pin to the now fully-measured bottom before the overlay clears.
        if (chatShell.IsFollowingTail)
        {
            chatShell.NotifyTranscriptLayoutChanged();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;
        }

        SyncTranscriptPinnedState();
    }

    private void OnTranscriptViewportChanged(object? sender, StrataTranscriptViewportChangedEventArgs e)
    {
        if (_subscribedVm is null || _chatShell is null)
            return;

        ObserveTranscriptScrollbarPagingDirection(e.VerticalOffset);
        if (_isFocusedHistoryActive)
            return;

        var isActualTailMounted = !_subscribedVm.HasUnmountedTranscriptTail;
        if (!isActualTailMounted && _chatShell.IsFollowingTail)
            _chatShell.PreserveViewport();

        _subscribedVm.UpdateTranscriptScrollState(
            _chatShell.IsFollowingTail && isActualTailMounted,
            e.IsPinnedToBottom && isActualTailMounted,
            e.DistanceFromBottom);

        if (_subscribedVm.MaintainsStableTranscriptMembership)
            return;

        if (_isTranscriptScrollbarDragging)
        {
            _viewportEvaluationRequested = true;
            return;
        }

        if (_isApplyingTranscriptMutation)
        {
            _viewportEvaluationRequested = true;
            return;
        }

        if (e.IsPinnedToBottom && isActualTailMounted)
            return;

        QueueTranscriptViewportEvaluation();
    }

    private void QueueTranscriptViewportEvaluation()
    {
        if (_isFocusedHistoryActive
            || _subscribedVm?.MaintainsStableTranscriptMembership == true)
        {
            _viewportEvaluationRequested = false;
            return;
        }

        _viewportEvaluationRequested = true;
        if (_isTranscriptScrollbarDragging)
            return;

        if (_viewportEvaluationQueued)
            return;

        _viewportEvaluationQueued = true;
        Dispatcher.UIThread.Post(() => _ = EvaluateTranscriptViewportAsync(), DispatcherPriority.Loaded);
    }

    private async Task EvaluateTranscriptViewportAsync()
    {
        try
        {
            for (var round = 0; round < 8; round++)
            {
                _viewportEvaluationRequested = false;

                if (_isTranscriptScrollbarDragging
                    || _isApplyingTranscriptMutation
                    || _isFocusedHistoryActive
                    || _subscribedVm is null
                    || _chatShell is null
                    || _transcriptScrollViewer is null)
                    return;

                var isActualTailMounted = !_subscribedVm.HasUnmountedTranscriptTail;
                if (!isActualTailMounted && _chatShell.IsFollowingTail)
                    _chatShell.PreserveViewport();

                var isFollowingActualTail = _chatShell.IsFollowingTail && isActualTailMounted;
                var scrollGeneration = _chatShell.ScrollGeneration;
                var pagingDirection = _pendingTranscriptPagingDirection;
                _pendingTranscriptPagingDirection = TranscriptPagingDirection.None;
                var anchor = isFollowingActualTail ? null : CaptureAnchor();
                var mutation = RunTranscriptMutation(() =>
                    _subscribedVm.EnsureMountedTranscriptCoverage(
                        _chatShell.ViewportHeight,
                        _chatShell.ExtentHeight));

                if (!mutation.HasChanges)
                {
                    mutation = RunTranscriptMutation(() =>
                        _subscribedVm.UpdateTranscriptViewport(
                            _chatShell.VerticalOffset,
                            _chatShell.ViewportHeight,
                            _chatShell.ExtentHeight,
                            isFollowingActualTail,
                            _chatShell.IsPinnedToBottom && isActualTailMounted,
                            _chatShell.CurrentDistanceFromBottom,
                            pagingDirection));
                }

                if (!mutation.HasChanges)
                {
                    if (!_viewportEvaluationRequested)
                        return;

                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    continue;
                }

                await CompleteTranscriptMutationAsync(anchor, mutation);

                if ((mutation.Kind is TranscriptWindowMutationKind.Prepend or TranscriptWindowMutationKind.Append)
                    && _chatShell.ScrollGeneration == scrollGeneration)
                {
                    // Mounted-turn collection changes request another evaluation while the mutation is
                    // applying. If the user did not provide new scroll input, that request only reflects
                    // our own layout shift and must not reverse the window on the opposite edge.
                    _viewportEvaluationRequested = false;
                }

                if (mutation.Kind != TranscriptWindowMutationKind.EnsureCoverage
                    && !_viewportEvaluationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            _viewportEvaluationQueued = false;
            if (_viewportEvaluationRequested && !_isTranscriptScrollbarDragging)
                QueueTranscriptViewportEvaluation();
        }
    }

    private async void OnTranscriptViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isApplyingTranscriptMutation || _subscribedVm is null || _chatShell is null)
            return;

        if (_isFocusedHistoryActive)
        {
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        if (_subscribedVm.MaintainsStableTranscriptMembership)
        {
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        var isActualTailMounted = !_subscribedVm.HasUnmountedTranscriptTail;
        if (!isActualTailMounted && _chatShell.IsFollowingTail)
            _chatShell.PreserveViewport();

        var anchor = _chatShell.IsFollowingTail && isActualTailMounted ? null : CaptureAnchor();
        var mutation = RunTranscriptMutation(() =>
            _subscribedVm.EnsureMountedTranscriptCoverage(_chatShell.ViewportHeight, _chatShell.ExtentHeight));
        if (mutation.HasChanges)
            await CompleteTranscriptMutationAsync(anchor, mutation);

        _chatShell.NotifyTranscriptLayoutChanged();
    }

    private void OnChatViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_chatShell is null || _isApplyingTranscriptMutation)
            return;

        if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 0.5)
            return;

        var isActualTailMounted = !(_subscribedVm?.HasUnmountedTranscriptTail ?? false);
        if (!isActualTailMounted && _chatShell.IsFollowingTail)
            _chatShell.PreserveViewport();

        if (_chatShell.IsFollowingTail && isActualTailMounted)
        {
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        var anchor = CaptureAnchor();
        var restoreVersion = ++_resizeRestoreVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (restoreVersion == _resizeRestoreVersion)
                RestoreAnchor(anchor, "resize");
        }, DispatcherPriority.Loaded);
    }

    private async Task<bool> EnsureTranscriptScrollViewerReadyAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            EnsureTranscriptScrollViewer();
            if (_transcriptScrollViewer is not null && _chatShell is not null)
                return true;

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }

        return false;
    }

    private T RunTranscriptMutation<T>(Func<T> action)
    {
        var wasInitiating = _isInitiatingTranscriptMutation;
        _isInitiatingTranscriptMutation = true;
        try
        {
            return action();
        }
        finally
        {
            _isInitiatingTranscriptMutation = wasInitiating;
        }
    }

    private async Task CompleteTranscriptMutationAsync(ScrollAnchorState? anchor, TranscriptWindowMutation mutation)
    {
        _isApplyingTranscriptMutation = true;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (mutation.RequiresAnchorRestore)
                RestoreAnchor(anchor, mutation.Kind switch
                {
                    TranscriptWindowMutationKind.Prepend => "prepend",
                    TranscriptWindowMutationKind.Append => "append",
                    TranscriptWindowMutationKind.TailRestore => "tail-restore",
                    _ => "cleanup"
                });

            SyncTranscriptPinnedState();
            _chatShell?.NotifyTranscriptLayoutChanged();
        }
        finally
        {
            _isApplyingTranscriptMutation = false;
            if (_viewportEvaluationRequested)
                QueueTranscriptViewportEvaluation();
        }
    }

    private void QueueCompletedAssistantTailRecovery()
    {
        if (_subscribedVm is not { CurrentChat: not null, IsBusy: false, IsLoadingChat: false }
            || _chatShell is null)
        {
            return;
        }

        var recoveryVersion = ++_tailRecoveryVersion;
        Dispatcher.UIThread.Post(
            () => _ = RecoverCompletedAssistantTailAsync(recoveryVersion),
            DispatcherPriority.Loaded);
    }

    private async Task RecoverCompletedAssistantTailAsync(int recoveryVersion)
    {
        for (var attempt = 0; attempt < 4 && _isApplyingTranscriptMutation; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            if (recoveryVersion != _tailRecoveryVersion)
                return;
        }

        if (recoveryVersion != _tailRecoveryVersion
            || _isApplyingTranscriptMutation
            || _subscribedVm is not { CurrentChat: not null, IsBusy: false, IsLoadingChat: false } viewModel
            || _chatShell is null)
        {
            return;
        }

        // While the user is following the tail, the just-completed assistant turn must end up
        // mounted and visible. The paging controller mounts a streamed tail turn only when the
        // distance-based IsPinnedToBottom is true, but that flips false transiently as a turn grows
        // past its placeholder height (StrataChatShell re-pins on the next layout pass). A turn
        // appended during that window is never mounted, so the response stays invisible until a chat
        // switch rebuilds the transcript. Force-mount the latest tail and snap to the end — the
        // completion-time counterpart to the EnsureLatestMounted done on user-send. Gate on
        // IsFollowingTail (intent) rather than IsPinnedToBottom (distance) so the transient-unpinned
        // window is still covered; a deliberate scroll-away keeps the anchored, non-disruptive path.
        if (_chatShell.IsFollowingTail)
        {
            if (viewModel.EnsureLatestTranscriptMounted())
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            if (recoveryVersion != _tailRecoveryVersion)
                return;

            _chatShell.NotifyTranscriptLayoutChanged();
            SyncTranscriptPinnedState();
            return;
        }

        var anchor = CaptureAnchor();
        var mutation = RunTranscriptMutation(viewModel.EnsureLatestTranscriptMountedIfAdjacentTailGap);
        if (!mutation.HasChanges)
            return;

        await CompleteTranscriptMutationAsync(anchor, mutation);
    }

    private ScrollAnchorState? CaptureAnchor(IReadOnlySet<string>? excludedStableIds = null)
    {
        if (_transcriptScrollViewer is null)
            return null;

        foreach (var control in EnumerateRealizedTurnControls())
        {
            var point = control.TranslatePoint(default, _transcriptScrollViewer);
            if (point is null)
                continue;

            if (point.Value.Y + control.Bounds.Height < 0)
                continue;

            if (control.Turn is null)
                continue;
            if (excludedStableIds?.Contains(control.Turn.StableId) == true)
                continue;

            return new ScrollAnchorState(
                control.Turn.StableId,
                point.Value.Y,
                _chatShell?.ScrollGeneration ?? 0);
        }

        return null;
    }

    private void RestoreAnchor(ScrollAnchorState? anchor, string reason)
    {
            if (anchor is null
                || _chatShell is null
                || _transcriptScrollViewer is null
                || anchor.ScrollGeneration != _chatShell.ScrollGeneration)
            return;

        var control = FindRealizedTurnControl(anchor.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        var delta = point.Value.Y - anchor.ViewportY;
        if (Math.Abs(delta) < 0.5)
            return;

        var beforeOffset = _chatShell.VerticalOffset;
        if (_chatShell.TryScrollToVerticalOffset(beforeOffset + delta, anchor.ScrollGeneration))
            _subscribedVm?.RecordTranscriptScrollCompensation(reason, beforeOffset, _chatShell.VerticalOffset);
    }

    private TranscriptTurnControl? FindRealizedTurnControl(string stableId)
    {
        return EnumerateRealizedTurnControls().FirstOrDefault(control => control.Turn?.StableId == stableId);
    }

    private IEnumerable<TranscriptTurnControl> EnumerateRealizedTurnControls()
    {
        var itemsHost = _transcript?.ItemsPanelRoot;
        return itemsHost is null
            ? Enumerable.Empty<TranscriptTurnControl>()
            : itemsHost.GetVisualDescendants().OfType<TranscriptTurnControl>();
    }

    /// <summary>
    /// Scrolls to the turn an activity row points at. A turn outside the progressively admitted suffix
    /// opens in an explicit bounded context instead of mounting the entire unknown target-to-tail gap.
    /// </summary>
    private async void OnWorkspaceJumpToTurnRequested(string stableId)
    {
        if (_subscribedVm is null || _chatShell is null || _transcriptScrollViewer is null)
            return;

        var turn = _subscribedVm.TranscriptTurns.FirstOrDefault(t => t.StableId == stableId);
        if (turn is null)
            return;

        var target = await EnsureTurnRealizedAsync(turn);
        var point = target?.TranslatePoint(default, _transcriptScrollViewer);
        if (target is null || point is null)
            return;

        var offset = Math.Max(0, _chatShell.VerticalOffset + point.Value.Y - 64);
        _chatShell.PreserveViewport();
        _chatShell.ScrollToVerticalOffset(offset);
    }

    private async Task<TranscriptTurnControl?> EnsureTurnRealizedAsync(TranscriptTurn turn)
    {
        if (_subscribedVm is null || _transcript is null)
            return null;

        var index = _subscribedVm.MountedTranscriptTurns.IndexOf(turn);
        if (index < 0)
            return await ShowFocusedHistoryAsync(turn);

        if (_isFocusedHistoryActive)
            await ExitFocusedHistoryAsync(restoreViewport: false);

        if (_subscribedVm is null || _transcript is null)
            return null;

        _chatShell?.PreserveViewport();
        index = _subscribedVm.MountedTranscriptTurns.IndexOf(turn);
        if (index < 0)
            return null;

        _transcript.ScrollIntoView(index);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var control = FindRealizedTurnControl(turn.StableId);
        if (control is null)
            return null;

        ((Control?)control.GetVisualParent() ?? control).BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        control.SetViewportActive(true);
        TranscriptRealizationScheduler.Instance.FlushControl(control);

        // Markdown realization posts its parse/rebuild work at Loaded priority. Background runs only
        // after that queue drains, so callers can safely inspect or highlight the realized subtree.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return control;
    }

    private async Task<TranscriptTurnControl?> ShowFocusedHistoryAsync(TranscriptTurn target)
    {
        if (_subscribedVm is null || _transcript is null || _chatShell is null)
            return null;

        var targetIndex = _subscribedVm.TranscriptTurns.IndexOf(target);
        if (targetIndex < 0)
            return null;

        if (!_isFocusedHistoryActive)
        {
            _anchorBeforeFocusedHistory = CaptureAnchor();
            var isActualTailMounted = !_subscribedVm.HasUnmountedTranscriptTail;
            _wasFollowingTailBeforeFocusedHistory = isActualTailMounted
                && (_chatShell.IsFollowingTail
                    || _chatShell.CurrentDistanceFromBottom <= ChatScrollPolicy.DefaultBottomTolerance);
        }

        _isFocusedHistoryActive = true;
        var version = ++_focusedHistoryVersion;
        _viewportEvaluationRequested = false;
        _chatShell.PreserveViewport();

        var start = Math.Max(0, targetIndex - FocusedHistoryContextRadius);
        var end = Math.Min(
            _subscribedVm.TranscriptTurns.Count - 1,
            targetIndex + FocusedHistoryContextRadius);
        _focusedHistoryTurns.Clear();
        for (var index = start; index <= end; index++)
            _focusedHistoryTurns.Add(_subscribedVm.TranscriptTurns[index]);

        _transcript.ItemsSource = _focusedHistoryTurns;
        SetFocusedHistoryVisualState(isActive: true);

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        if (version != _focusedHistoryVersion || !_isFocusedHistoryActive || _transcript is null)
            return null;

        var focusedIndex = _focusedHistoryTurns.IndexOf(target);
        if (focusedIndex < 0)
            return null;

        _transcript.ScrollIntoView(focusedIndex);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);

        var control = FindRealizedTurnControl(target.StableId);
        if (control is null)
            return null;

        ((Control?)control.GetVisualParent() ?? control).BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        control.SetViewportActive(true);
        TranscriptRealizationScheduler.Instance.FlushControl(control);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        return control;
    }

    private async Task ExitFocusedHistoryAsync(bool restoreViewport)
    {
        if (!_isFocusedHistoryActive)
            return;

        var anchor = _anchorBeforeFocusedHistory;
        var wasFollowingTail = _wasFollowingTailBeforeFocusedHistory;
        _isFocusedHistoryActive = false;
        var exitVersion = ++_focusedHistoryVersion;

        if (_transcript is not null)
            _transcript.ItemsSource = _subscribedMountedTurns;
        SetFocusedHistoryVisualState(isActive: false);
        _focusedHistoryTurns.Clear();

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        if (_isFocusedHistoryActive || _chatShell is null)
            return;

        if (restoreViewport)
        {
            if (wasFollowingTail)
            {
                _subscribedVm?.EnsureLatestTranscriptMounted();
                _chatShell.JumpToLatest();
            }
            else if (anchor is not null)
            {
                RestoreAnchor(
                    anchor with { ScrollGeneration = _chatShell.ScrollGeneration },
                    "focused-history-exit");
            }

            _transcript?.RealizeCurrentViewportNow();
            await WaitForFocusedHistoryExitLayoutAsync(exitVersion);
            if (exitVersion != _focusedHistoryVersion
                || _isFocusedHistoryActive
                || _chatShell is null)
            {
                return;
            }

            if (wasFollowingTail)
            {
                _chatShell.JumpToLatest();
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            }
            else if (anchor is not null)
            {
                RestoreAnchor(
                    anchor with { ScrollGeneration = _chatShell.ScrollGeneration },
                    "focused-history-exit-final");
            }
        }

        _anchorBeforeFocusedHistory = null;
        _wasFollowingTailBeforeFocusedHistory = false;
        SyncTranscriptPinnedState();
        QueueTranscriptViewportEvaluation();
    }

    private async Task WaitForFocusedHistoryExitLayoutAsync(int exitVersion)
    {
        if (_chatShell is null)
            return;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var lastExtent = double.NaN;
        var stableFrames = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(16);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            if (exitVersion != _focusedHistoryVersion || _isFocusedHistoryActive || _chatShell is null)
                return;

            var extent = _chatShell.ExtentHeight;
            var extentStable = !double.IsNaN(lastExtent) && Math.Abs(extent - lastExtent) < 0.5;
            lastExtent = extent;
            if (!TranscriptRealizationScheduler.Instance.HasPendingWork && extentStable)
            {
                stableFrames++;
                if (stableFrames >= 3)
                    return;
            }
            else
            {
                stableFrames = 0;
            }
        }
    }

    private async Task ExitFocusedHistoryAndJumpToLatestAsync(bool focusComposer)
    {
        await ExitFocusedHistoryAsync(restoreViewport: false);
        if (_subscribedVm is null || _chatShell is null)
            return;

        _subscribedVm.EnsureLatestTranscriptMounted();
        _chatShell.JumpToLatest();
        SyncTranscriptPinnedState();
        Dispatcher.UIThread.Post(SyncTranscriptPinnedState, DispatcherPriority.Loaded);

        if (focusComposer)
            Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private void ResetFocusedHistory()
    {
        _isFocusedHistoryActive = false;
        _focusedHistoryVersion++;
        _focusedHistoryTurns.Clear();
        _anchorBeforeFocusedHistory = null;
        _wasFollowingTailBeforeFocusedHistory = false;
        if (_transcript is not null)
            _transcript.ItemsSource = _subscribedMountedTurns;
        SetFocusedHistoryVisualState(isActive: false);
    }

    private void SetFocusedHistoryVisualState(bool isActive)
    {
        if (_focusedHistoryBanner is not null)
            _focusedHistoryBanner.IsVisible = isActive;
        if (_transcriptTopSpacer is not null)
            _transcriptTopSpacer.IsVisible = !isActive;
        if (_transcriptBottomSpacer is not null)
            _transcriptBottomSpacer.IsVisible = !isActive;
    }

    // ── File picker (requires View-level StorageProvider) ──

    private async void OnAttachFilesRequested()
    {
        if (DataContext is not ChatViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.FilePicker_AttachFiles,
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
                vm.AddAttachment(path);
        }

        if (files.Count > 0)
            FocusComposer();
    }

    // ── Clipboard image paste (requires View-level Clipboard) ──

    private async void OnClipboardPasteRequested()
    {
        if (DataContext is not ChatViewModel vm) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            var dataTransfer = await clipboard.TryGetDataAsync();
            if (dataTransfer is null) return;

            if (await TryPasteLumiChatContextAsync(vm, dataTransfer))
            {
                FocusComposer();
                return;
            }

            var clipboardText = await ClipboardExtensions.TryGetTextAsync(clipboard);
            if (TryPasteFormattedChatContext(vm, clipboardText))
            {
                FocusComposer();
                return;
            }

            if (await TryPasteClipboardFilesAsync(vm, dataTransfer))
            {
                FocusComposer();
                return;
            }

            // Skia can't decode macOS clipboard TIFF; supply the AppKit transcoder on macOS so those
            // (e.g. screenshots) still paste. Null elsewhere — the built-in decode path is used as before.
            Func<byte[], byte[]?>? nativeImageToPng = null;
            if (OperatingSystem.IsMacOS())
                nativeImageToPng = Services.MacOsNative.TryConvertImageToPng;

            using var bitmap = await ClipboardImage.TryGetImageAsync(dataTransfer, nativeImageToPng);
            if (bitmap is not null)
            {
                Directory.CreateDirectory(ClipboardImagesDir);
                var fileName = $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
                var filePath = Path.Combine(ClipboardImagesDir, fileName);
                bitmap.Save(filePath);

                vm.AddAttachment(filePath);
                FocusComposer();
                return;
            }

            if (!string.IsNullOrEmpty(clipboardText))
            {
                _composer?.InsertTextAtSelection(clipboardText);
                FocusComposer();
            }
        }
        catch
        {
            // Ignore transient clipboard failures.
        }
    }

    private async Task<bool> TryPasteLumiChatContextAsync(ChatViewModel vm, IAsyncDataTransfer dataTransfer)
    {
        var json = await dataTransfer.TryGetValueAsync(LumiChatContextClipboardFormat);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        ClipboardCopyPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(json, ClipboardJsonContext.Default.ClipboardCopyPayload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null)
            return false;

        if (!string.IsNullOrEmpty(payload.Text))
            _composer?.InsertTextAtSelection(payload.Text);

        foreach (var path in payload.AttachmentPaths.Where(static p => File.Exists(p) || Directory.Exists(p)))
            vm.AddAttachment(path);

        foreach (var skillName in payload.SkillNames.Where(static s => !string.IsNullOrWhiteSpace(s)))
            vm.AddSkillByName(skillName);

        return true;
    }

    private bool TryPasteFormattedChatContext(ChatViewModel vm, string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
            return false;

        if (!TryParseFormattedClipboardPayload(vm, clipboardText, out var payload))
            return false;

        if (!string.IsNullOrEmpty(payload.Text))
            _composer?.InsertTextAtSelection(payload.Text);

        foreach (var path in payload.AttachmentPaths)
            vm.AddAttachment(path);

        foreach (var skillName in payload.SkillNames)
            vm.AddSkillByName(skillName);

        return true;
    }

    private static bool TryParseFormattedClipboardPayload(
        ChatViewModel vm,
        string clipboardText,
        out ClipboardCopyPayload payload)
    {
        payload = new ClipboardCopyPayload(string.Empty, [], [], []);

        var normalized = clipboardText.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var promptLines = new List<string>();
        var attachmentPaths = new List<string>();
        var skillNames = new List<string>();
        var sources = new List<string>();
        var section = ClipboardTextSection.None;
        var sawSection = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (IsClipboardSection(trimmed, out var nextSection))
            {
                section = nextSection;
                sawSection = true;
                continue;
            }

            if (!sawSection)
            {
                promptLines.Add(rawLine);
                continue;
            }

            if (trimmed.Length == 0 || !trimmed.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var value = trimmed[2..].Trim();
            if (value.Length == 0)
                continue;

            switch (section)
            {
                case ClipboardTextSection.Files:
                    if (File.Exists(value) || Directory.Exists(value))
                        attachmentPaths.Add(value);
                    break;
                case ClipboardTextSection.UsedSkills:
                    if (vm.FindSkillReferenceByName(value) is not null)
                        skillNames.Add(value);
                    break;
                case ClipboardTextSection.Sources:
                    sources.Add(value);
                    break;
            }
        }

        if (attachmentPaths.Count == 0 && skillNames.Count == 0)
            return false;

        payload = new ClipboardCopyPayload(
            string.Join('\n', promptLines).Trim(),
            DistinctNonEmpty(attachmentPaths),
            DistinctNonEmpty(skillNames),
            DistinctNonEmpty(sources));
        return true;
    }

    private static bool IsClipboardSection(string line, out ClipboardTextSection section)
    {
        section = line.ToLowerInvariant() switch
        {
            "files:" => ClipboardTextSection.Files,
            "used skills:" => ClipboardTextSection.UsedSkills,
            "sources:" => ClipboardTextSection.Sources,
            _ => ClipboardTextSection.None
        };

        return section != ClipboardTextSection.None;
    }

    private enum ClipboardTextSection
    {
        None,
        Files,
        UsedSkills,
        Sources
    }

    private static async Task<bool> TryPasteClipboardFilesAsync(ChatViewModel vm, IAsyncDataTransfer dataTransfer)
    {
        var files = await dataTransfer.TryGetFilesAsync();
        if (files is null || files.Length == 0)
            return false;

        var added = false;
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (!File.Exists(path) && !Directory.Exists(path))
                continue;

            vm.AddAttachment(path);
            added = true;
        }

        return added;
    }

    // ── Copy to clipboard (ViewModel raises event, View handles clipboard API) ──

    private async void OnCopyToClipboardRequested(string text)
        => await SetClipboardTextAsync(text);

    private async Task SetClipboardTextAsync(string text, ClipboardCopyPayload? payload = null)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.CreateText(text));
            if (payload is not null && HasCopyContext(payload))
            {
                data.Add(Avalonia.Input.DataTransferItem.Create(
                    LumiChatContextClipboardFormat,
                    JsonSerializer.Serialize(payload, ClipboardJsonContext.Default.ClipboardCopyPayload)));

                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider is not null)
                {
                    foreach (var path in payload.AttachmentPaths.Where(static p => File.Exists(p) || Directory.Exists(p)))
                    {
                        IStorageItem? storageItem;
                        if (File.Exists(path))
                            storageItem = await storageProvider.TryGetFileFromPathAsync(path);
                        else
                            storageItem = await storageProvider.TryGetFolderFromPathAsync(path);

                        if (storageItem is not null)
                            data.Add(Avalonia.Input.DataTransferItem.CreateFile(storageItem));
                    }
                }
            }
            await clipboard.SetDataAsync(data);
        }
        catch { /* ignore */ }
    }

    private static bool HasCopyContext(ClipboardCopyPayload payload)
        => payload.AttachmentPaths.Count > 0 || payload.SkillNames.Count > 0 || payload.Sources.Count > 0;

    private async void OnCopyMessageRequested(object? sender, StrataCopyRequestedEventArgs e)
    {
        if (e.Source is not StrataChatMessage message)
            return;

        e.Handled = true;

        if (e.Format != StrataCopyFormat.Text)
            return;

        if (e.IsSelection && !string.IsNullOrEmpty(e.Text))
        {
            await SetClipboardTextAsync(e.Text);
            return;
        }

        var payload = BuildMessageCopyPayload(message.DataContext, message.Content);
        if (payload is null)
            return;

        var text = FormatClipboardText(payload);
        if (string.IsNullOrWhiteSpace(text))
            return;

        await SetClipboardTextAsync(text, payload);
    }

    // ── Fork from here (context menu on any transcript message) ───

    private void OnForkRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        if (e.Source is not StrataChatMessage message)
            return;

        // The message's DataContext is the transcript item, which carries the underlying
        // ChatMessage id — the point the new branch is cut at.
        var messageId = message.DataContext switch
        {
            UserMessageItem user => user.Message.Id,
            AssistantMessageItem assistant => assistant.MessageId,
            _ => (Guid?)null
        };

        if (messageId is Guid id && DataContext is ChatViewModel vm)
            vm.RequestForkFromMessage(id);
    }

    // ── Copy turn (context menu on assistant messages) ───

    private async void OnCopyTurnRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        // Walk up from the event source to find the parent TranscriptTurnControl
        TranscriptTurnControl? turn = null;
        if (e.Source is Avalonia.Visual visual)
        {
            var current = visual.GetVisualParent();
            while (current is not null)
            {
                if (current is TranscriptTurnControl ttc) { turn = ttc; break; }
                current = (current as Avalonia.Visual)?.GetVisualParent();
            }
        }

        if (turn is null) return;

        var payload = BuildTurnCopyPayload(turn.Items ?? Enumerable.Empty<TranscriptItem>());
        if (payload is null) return;

        var text = FormatClipboardText(payload);
        if (string.IsNullOrWhiteSpace(text)) return;

        await SetClipboardTextAsync(text, payload);
    }

    private static ClipboardCopyPayload? BuildMessageCopyPayload(object? dataContext, object? content)
    {
        return dataContext switch
        {
            UserMessageItem user => CreatePayload(
                user.Content,
                user.Attachments.Select(static a => a.FilePath),
                user.Skills.Select(static s => s.Name),
                []),
            AssistantMessageItem assistant => CreatePayload(
                assistant.Content,
                assistant.FileAttachments.Select(static a => a.FilePath),
                assistant.Skills.Select(static s => s.Name),
                assistant.Sources.Select(static s => string.IsNullOrWhiteSpace(s.Url) ? s.Title : $"{s.Title} - {s.Url}")),
            _ => CreatePayload(
                ChatContentExtractor.ExtractText(content).Trim(),
                [],
                [],
                [])
        };
    }

    private static ClipboardCopyPayload? BuildTurnCopyPayload(IEnumerable<TranscriptItem> items)
    {
        var textParts = new List<string>();
        var attachmentPaths = new List<string>();
        var skillNames = new List<string>();
        var sources = new List<string>();

        foreach (var item in items)
        {
            if (item is not AssistantMessageItem assistant)
                continue;

            if (!string.IsNullOrWhiteSpace(assistant.Content))
                textParts.Add(assistant.Content.Trim());

            attachmentPaths.AddRange(assistant.FileAttachments.Select(static a => a.FilePath));
            skillNames.AddRange(assistant.Skills.Select(static s => s.Name));
            sources.AddRange(assistant.Sources.Select(static s =>
                string.IsNullOrWhiteSpace(s.Url) ? s.Title : $"{s.Title} - {s.Url}"));
        }

        return CreatePayload(
            string.Join($"{Environment.NewLine}{Environment.NewLine}", textParts),
            attachmentPaths,
            skillNames,
            sources);
    }

    private static ClipboardCopyPayload? CreatePayload(
        string? text,
        IEnumerable<string> attachmentPaths,
        IEnumerable<string> skillNames,
        IEnumerable<string> sources)
    {
        var payload = new ClipboardCopyPayload(
            text?.Trim() ?? string.Empty,
            DistinctNonEmpty(attachmentPaths),
            DistinctNonEmpty(skillNames),
            DistinctNonEmpty(sources));

        return string.IsNullOrWhiteSpace(payload.Text) && !HasCopyContext(payload)
            ? null
            : payload;
    }

    private static List<string> DistinctNonEmpty(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FormatClipboardText(ClipboardCopyPayload payload)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(payload.Text))
            sb.Append(payload.Text.Trim());

        AppendClipboardSection(sb, "Files", payload.AttachmentPaths);
        AppendClipboardSection(sb, "Used skills", payload.SkillNames);
        AppendClipboardSection(sb, "Sources", payload.Sources);

        return sb.ToString();
    }

    private static void AppendClipboardSection(StringBuilder sb, string heading, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return;

        if (sb.Length > 0)
            sb.AppendLine().AppendLine();

        sb.AppendLine($"{heading}:");
        foreach (var value in values)
            sb.Append("- ").AppendLine(value);
    }

    // ── Drag & drop ──────────────────────────────────────

    private void OnFileAttachmentOpenRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is StrataFileAttachment { DataContext: FileAttachmentItem item })
            item.OpenCommand.Execute(null);
    }

    private static bool HasFiles(DragEventArgs e)
        => e.DataTransfer.Formats.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (HasFiles(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (_dropOverlay is not null) _dropOverlay.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
        if (DataContext is not ChatViewModel vm) return;

        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.File) is IStorageItem storageItem)
            {
                var path = storageItem.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                    vm.AddAttachment(path);
            }
        }

        FocusComposer();
    }

    private static async void PlaySlideUpAnimation(Control target)
    {
        target.Opacity = 0;
        target.RenderTransform = new Avalonia.Media.TranslateTransform(0, 6);

        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(250),
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Children =
            {
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 0.0), new Avalonia.Styling.Setter(Avalonia.Media.TranslateTransform.YProperty, 6.0) } },
                new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 1.0), new Avalonia.Styling.Setter(Avalonia.Media.TranslateTransform.YProperty, 0.0) } },
            }
        };

        try { await anim.RunAsync(target); } catch { }
        target.Opacity = 1;
        target.RenderTransform = null;
    }

    private void UpdateWorktreeToggleHighlight()
    {
        if (_worktreeHighlight is null || _localToggleBtn is null || _worktreeToggleBtn is null)
            return;

        var isWorktree = _subscribedVm?.IsWorktreeMode ?? false;
        var target = isWorktree ? _worktreeToggleBtn : _localToggleBtn;

        if (target.Bounds.Width <= 0) return;

        _worktreeHighlight.Width = target.Bounds.Width;
        _worktreeHighlight.Margin = new Thickness(target.Bounds.Left, 0, 0, 0);
    }

    private void QueueWorktreeToggleHighlightUpdate()
    {
        if (_worktreeHighlightUpdateQueued)
            return;

        _worktreeHighlightUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _worktreeHighlightUpdateQueued = false;
            UpdateWorktreeToggleHighlight();
        }, DispatcherPriority.Loaded);
    }

    // ── Ctrl+F in-chat search ────────────────────────────

    private void OnLinkedChatKeyDown(object? sender, KeyEventArgs e)
    {
        var source = e.Source as Control;
        var chip = source?.DataContext as LinkedChatChipItem
            ?? source?.GetVisualAncestors()
                .OfType<Control>()
                .Select(control => control.DataContext)
                .OfType<LinkedChatChipItem>()
                .FirstOrDefault();
        if (chip is null ||
            !IsOpenLinkedChatInNewWindowShortcut(e.Key, e.KeyModifiers, OperatingSystem.IsMacOS()))
            return;

        e.Handled = true;
        if (Application.Current is App app)
        {
            var owner = TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;
            app.OpenChatInNewWindow(chip.ChatId, owner);
        }
    }

    internal static bool IsOpenLinkedChatInNewWindowShortcut(
        Key key,
        KeyModifiers modifiers,
        bool isMac)
    {
        var primaryCommand = isMac ? KeyModifiers.Meta : KeyModifiers.Control;
        return key == Key.Enter
            && (modifiers & primaryCommand) != 0
            && (modifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) == 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        // Command modifier: Cmd on macOS, Ctrl on Windows/Linux (unchanged on Windows/Linux).
        var ctrl = OperatingSystem.IsMacOS()
            ? (e.KeyModifiers & KeyModifiers.Meta) != 0
            : (e.KeyModifiers & KeyModifiers.Control) != 0;

        if (ctrl && e.Key == Key.F)
        {
            OpenSearch();
            e.Handled = true;
        }
    }

    private void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
            case Key.Enter:
                FlushPendingSearch();
                NavigateSearchMatch(shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.F3:
                FlushPendingSearch();
                NavigateSearchMatch(shift ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>If a debounced search is pending, execute it immediately.</summary>
    private void FlushPendingSearch()
    {
        if (_searchDebounce is not null && !_searchDebounce.IsCancellationRequested)
        {
            _searchDebounce.Cancel();
            _searchDebounce.Dispose();
            _searchDebounce = null;
            ExecuteSearch();
        }
    }

    private void OpenSearch()
    {
        if (_searchBar is null || _searchInput is null) return;

        _searchBar.Classes.Add("open");

        Dispatcher.UIThread.Post(() =>
        {
            _searchInput.Focus();
            _searchInput.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void CloseSearch()
    {
        if (_searchBar is null) return;

        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        _searchBar.Classes.Remove("open");
        ResetSearchState();
        if (_isFocusedHistoryActive)
            _ = ExitFocusedHistoryAsync(restoreViewport: true);

        FocusComposer();
    }

    private void OnSearchQueryChanged()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new System.Threading.CancellationTokenSource();
        var token = _searchDebounce.Token;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(200, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            ExecuteSearch();
        });
    }

    private void ExecuteSearch()
    {
        var query = _searchInput?.Text;
        _searchHits.Clear();
        _currentHitIndex = -1;
        ClearSearchHighlight();

        if (string.IsNullOrWhiteSpace(query) || _subscribedVm is null)
        {
            UpdateSearchCounter();
            return;
        }

        // Search ALL transcript turns (including unmounted/off-screen)
        foreach (var turn in _subscribedVm.TranscriptTurns)
        {
            foreach (var item in turn.Items)
            {
                var content = item switch
                {
                    UserMessageItem u => u.Content,
                    JobWakeItem j => j.SearchText,
                    AssistantMessageItem a => a.Content,
                    ErrorMessageItem err => err.Content,
                    ReasoningItem r => r.Content,
                    _ => null
                };
                if (content is null) continue;

                // Count occurrences in the raw content (case-insensitive)
                var pos = 0;
                var occurrence = 0;
                while (pos < content.Length)
                {
                    var idx = content.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    _searchHits.Add(new SearchHit(turn, item, occurrence, query));
                    occurrence++;
                    pos = idx + query.Length;
                }
            }
        }

        if (_searchHits.Count > 0)
            _currentHitIndex = 0;

        UpdateSearchCounter();
    }

    private async void NavigateSearchMatch(int direction)
    {
        if (_searchHits.Count == 0) return;

        ClearSearchHighlight();
        _currentHitIndex = (_currentHitIndex + direction + _searchHits.Count) % _searchHits.Count;
        UpdateSearchCounter();

        var hit = _searchHits[_currentHitIndex];
        if (_subscribedVm is null) return;

        if (await EnsureTurnRealizedAsync(hit.Turn) is null)
            return;

        HighlightHit(hit);
    }

    private void HighlightHit(SearchHit hit)
    {
        var query = hit.Query;
        if (string.IsNullOrEmpty(query) || _transcript is null) return;

        // Find the visual for this item. Host children are directly-built item views carrying the
        // HostedItem attached property (no longer ContentPresenters whose Content is the item), so a
        // retained switch-back reuses the same instances; match on HostedItem to locate the hit.
        Control? itemVisual = null;
        foreach (var d in _transcript.GetVisualDescendants())
        {
            if (d is Control c && ReferenceEquals(TranscriptTurnControl.GetHostedItem(c), hit.Item))
            { itemVisual = c; break; }
        }
        if (itemVisual is null) return;

        // Walk SelectableTextBlocks inside, find the Nth occurrence
        var occurrencesSeen = 0;
        foreach (var d in itemVisual.GetVisualDescendants())
        {
            if (d is not SelectableTextBlock stb || !stb.IsVisible) continue;

            var text = ExtractStbText(stb, out var posMap);
            if (string.IsNullOrEmpty(text)) continue;

            var searchFrom = 0;
            while (searchFrom < text.Length)
            {
                var idx = text.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                if (occurrencesSeen == hit.OccurrenceInItem)
                {
                    var selStart = posMap is not null ? posMap[idx] : idx;
                    var selEnd = posMap is not null ? posMap[idx + query.Length - 1] + 1 : idx + query.Length;
                    stb.SelectionStart = selStart;
                    stb.SelectionEnd = selEnd;
                    _highlightedStb = stb;
                    stb.BringIntoView();
                    return;
                }

                occurrencesSeen++;
                searchFrom = idx + query.Length;
            }
        }
    }

    private static string? ExtractStbText(SelectableTextBlock stb, out List<int>? posMap)
    {
        posMap = null;
        var text = stb.Text;
        if (!string.IsNullOrEmpty(text)) return text;

        if (stb.Inlines is not { Count: > 0 }) return null;

        var rawSb = new System.Text.StringBuilder();
        foreach (var inline in stb.Inlines)
        {
            if (inline is Run run)
                rawSb.Append(run.Text ?? "");
            else if (inline is Avalonia.Controls.Documents.LineBreak)
                rawSb.Append('\n');
            else
                rawSb.Append('\uFFFC');
        }
        var rawText = rawSb.ToString();

        // Strip \u2005 inline code padding, build position map
        posMap = new List<int>(rawText.Length);
        var strippedSb = new System.Text.StringBuilder(rawText.Length);
        for (var i = 0; i < rawText.Length; i++)
        {
            if (rawText[i] != '\u2005')
            {
                posMap.Add(i);
                strippedSb.Append(rawText[i]);
            }
        }
        return strippedSb.ToString();
    }

    private void UpdateSearchCounter()
    {
        if (_searchMatchCounter is null) return;

        if (_searchHits.Count == 0)
        {
            var hasQuery = !string.IsNullOrWhiteSpace(_searchInput?.Text);
            _searchMatchCounter.Text = hasQuery ? "No results" : "";
        }
        else
        {
            _searchMatchCounter.Text = $"{_currentHitIndex + 1} of {_searchHits.Count}";
        }
    }

    private void ClearSearchHighlight()
    {
        if (_highlightedStb is not null)
        {
            _highlightedStb.SelectionStart = 0;
            _highlightedStb.SelectionEnd = 0;
            _highlightedStb = null;
        }
    }

    private void ResetSearchState()
    {
        ClearSearchHighlight();
        _searchHits.Clear();
        _currentHitIndex = -1;
        if (_searchMatchCounter is not null)
            _searchMatchCounter.Text = "";
    }
}
