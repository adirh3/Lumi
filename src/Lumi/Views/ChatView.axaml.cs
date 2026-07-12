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
    private ItemsControl? _transcript;
    private ScrollViewer? _transcriptScrollViewer;

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
    private bool _viewportEvaluationQueued;
    private bool _viewportEvaluationRequested;
    private bool _isTranscriptScrollbarDragging;
    private Control? _transcriptScrollbarCaptureSource;
    private int _initialTranscriptTailSyncVersion;
    private int _resizeRestoreVersion;
    private int _tailRecoveryVersion;
    private readonly Dictionary<string, double> _observedTurnHeights = new(StringComparer.Ordinal);
    private readonly HashSet<TranscriptTurn> _heightSubscribedTurns = new();

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
        _transcript = this.FindControl<ItemsControl>("Transcript");
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
        SizeChanged += OnChatViewSizeChanged;

        // ── Search bar controls ──
        _searchBar = this.FindControl<Border>("SearchBar");
        _searchInput = this.FindControl<TextBox>("SearchInput");
        _searchMatchCounter = this.FindControl<TextBlock>("SearchMatchCounter");

        var searchPrevBtn = this.FindControl<Button>("SearchPrevBtn");
        var searchNextBtn = this.FindControl<Button>("SearchNextBtn");
        var searchCloseBtn = this.FindControl<Button>("SearchCloseBtn");

        if (_searchInput is not null)
        {
            _searchInput.TextChanged += (_, _) => OnSearchQueryChanged();
            _searchInput.KeyDown += OnSearchInputKeyDown;
        }

        if (searchPrevBtn is not null) searchPrevBtn.Click += (_, _) => NavigateSearchMatch(-1);
        if (searchNextBtn is not null) searchNextBtn.Click += (_, _) => NavigateSearchMatch(1);
        if (searchCloseBtn is not null) searchCloseBtn.Click += (_, _) => CloseSearch();
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
        UnsubscribeFromViewModel();
        ResetSearchState();
        _viewportEvaluationRequested = false;
        _resizeRestoreVersion++;
        _tailRecoveryVersion++;
        _lastObservedCurrentChat = null;

        if (DataContext is ChatViewModel vm)
        {
            _subscribedVm = vm;
            _lastObservedCurrentChat = vm.CurrentChat;
            vm.ScrollToEndRequested += OnScrollToEndRequested;
            vm.UserMessageSent += OnUserMessageSent;
            vm.TranscriptRebuilt += OnTranscriptRebuilt;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.AttachFilesRequested += OnAttachFilesRequested;
            vm.ClipboardPasteRequested += OnClipboardPasteRequested;
            vm.CopyToClipboardRequested += OnCopyToClipboardRequested;
            vm.FocusComposerRequested += FocusComposer;
            vm.WorkspaceJumpToTurnRequested += OnWorkspaceJumpToTurnRequested;
            SubscribeToMountedTurns(vm.MountedTranscriptTurns);
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
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

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedVm is null) return;
        _subscribedVm.ScrollToEndRequested -= OnScrollToEndRequested;
        _subscribedVm.UserMessageSent -= OnUserMessageSent;
        _subscribedVm.TranscriptRebuilt -= OnTranscriptRebuilt;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm.AttachFilesRequested -= OnAttachFilesRequested;
        _subscribedVm.ClipboardPasteRequested -= OnClipboardPasteRequested;
        _subscribedVm.CopyToClipboardRequested -= OnCopyToClipboardRequested;
        _subscribedVm.FocusComposerRequested -= FocusComposer;
        _subscribedVm.WorkspaceJumpToTurnRequested -= OnWorkspaceJumpToTurnRequested;
        // Clear the realizing gate so a view detach mid-open can't leave the overlay stuck up on the VM:
        // a suspended OpenTranscriptAtLatestAsync won't reach its gate-clearing finally once _subscribedVm
        // is null / the sync version has been bumped below.
        _subscribedVm.IsTranscriptRealizing = false;
        _subscribedVm = null;
        _lastObservedCurrentChat = null;
        _initialTranscriptTailSyncVersion++;
        _resizeRestoreVersion++;
        _tailRecoveryVersion++;
    }

    private void SubscribeToMountedTurns(ObservableCollection<TranscriptTurn> mountedTurns)
    {
        UnsubscribeMountedTurns();
        _subscribedMountedTurns = mountedTurns;
        _subscribedMountedTurns.CollectionChanged += OnMountedTurnsChanged;
        foreach (var turn in _subscribedMountedTurns)
            SubscribeToTurnHeight(turn);
    }

    private void UnsubscribeMountedTurns()
    {
        if (_subscribedMountedTurns is null)
            return;

        _subscribedMountedTurns.CollectionChanged -= OnMountedTurnsChanged;
        UnsubscribeAllTurnHeights();
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
        _transcriptScrollViewer.RemoveHandler(InputElement.PointerPressedEvent, OnTranscriptScrollViewerPointerPressed);
        _transcriptScrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnTranscriptScrollViewerPointerReleased);
        _transcriptScrollViewer = null;
        ClearTranscriptScrollbarDrag();
    }

    private void OnMountedTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Reset fires with OldItems=null — use tracked set to unsubscribe.
            UnsubscribeAllTurnHeights();

            if (_subscribedMountedTurns is not null)
            {
                foreach (var turn in _subscribedMountedTurns)
                    SubscribeToTurnHeight(turn);
            }

            QueueViewportRecoveryAfterMountedTurnsChanged();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (TranscriptTurn turn in e.OldItems)
            {
                turn.PropertyChanged -= OnMountedTurnPropertyChanged;
                _observedTurnHeights.Remove(turn.StableId);
                _heightSubscribedTurns.Remove(turn);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TranscriptTurn turn in e.NewItems)
                SubscribeToTurnHeight(turn);
        }

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

        if (_isApplyingTranscriptMutation)
        {
            _viewportEvaluationRequested = true;
            return;
        }

        QueueTranscriptViewportEvaluation();
    }

    private void UnsubscribeAllTurnHeights()
    {
        foreach (var turn in _heightSubscribedTurns)
            turn.PropertyChanged -= OnMountedTurnPropertyChanged;
        _heightSubscribedTurns.Clear();
        _observedTurnHeights.Clear();
    }

    private void SubscribeToTurnHeight(TranscriptTurn turn)
    {
        _observedTurnHeights[turn.StableId] = turn.MeasuredHeight;
        _heightSubscribedTurns.Add(turn);
        turn.PropertyChanged += OnMountedTurnPropertyChanged;
    }

    private void OnMountedTurnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TranscriptTurn.MeasuredHeight)
            || sender is not TranscriptTurn turn
            || _chatShell is null
            || _transcriptScrollViewer is null
            || _isApplyingTranscriptMutation)
            return;

        _observedTurnHeights.TryGetValue(turn.StableId, out var previousHeight);
        _observedTurnHeights[turn.StableId] = turn.MeasuredHeight;

        var delta = turn.MeasuredHeight - previousHeight;
        if (Math.Abs(delta) < 0.5)
            return;

        if (_subscribedVm is { IsBusy: true } || _chatShell.IsFollowingTail)
        {
            QueueTranscriptViewportEvaluation();
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        var control = FindRealizedTurnControl(turn.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        // Only compensate for turns fully above the viewport.
        if (point.Value.Y + control.Bounds.Height > 0)
            return;

        _chatShell.CompensateForContentAbove(delta);
    }

    private void OnScrollToEndRequested() => _chatShell?.NotifyTranscriptContentChanged();

    private void OnTranscriptScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var captureSource = FindScrollbarInteractionControl(e.Source);
        if (captureSource is null)
            return;

        _isTranscriptScrollbarDragging = true;
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

        ClearTranscriptScrollbarDrag();
        QueueTranscriptViewportEvaluation();
    }

    private void ClearTranscriptScrollbarDrag()
    {
        _isTranscriptScrollbarDragging = false;
        SetTranscriptScrollbarCaptureSource(null);
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
        if (_subscribedVm is null || _chatShell is null)
            return;

        _subscribedVm.UpdateTranscriptPinnedState(_chatShell.IsPinnedToBottom, _chatShell.CurrentDistanceFromBottom);
    }

    private void OnJumpToLatestRequested() => JumpToLatest(focusComposer: false);

    private void JumpToLatest(bool focusComposer)
    {
        _subscribedVm?.EnsureLatestTranscriptMounted();
        _chatShell?.JumpToLatest();
        Dispatcher.UIThread.Post(SyncTranscriptPinnedState, DispatcherPriority.Loaded);

        if (focusComposer)
            Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private void OnUserMessageSent()
    {
        JumpToLatest(focusComposer: true);
    }

    private async void OnTranscriptRebuilt()
    {
        // Only a load/switch-driven rebuild may raise the loading overlay. RebuildTranscript also fires
        // on incidental in-place rebuilds of the visible chat (stream completion attaching web sources,
        // settings toggles like ShowReasoning/ShowToolCalls, edit/resend) where IsLoadingChat is false —
        // those must NOT flash the full-surface overlay or absorb clicks while the transcript re-realizes.
        // During a genuine load IsLoadingChat is already true here (RebuildTranscript runs inside
        // LoadChatAsync before its finally clears the flag), so raising the gate synchronously keeps the
        // overlay continuously up from load → realization with no blank frame in between.
        if (_subscribedVm is { IsLoadingChat: true })
            _subscribedVm.IsTranscriptRealizing = true;

        var syncVersion = ++_initialTranscriptTailSyncVersion;
        await OpenTranscriptAtLatestAsync(focusComposer: true, searchAfterOpen: true, syncVersion);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat))
        {
            var currentChat = _subscribedVm?.CurrentChat;
            var chatReferenceChanged = !ReferenceEquals(currentChat, _lastObservedCurrentChat);
            _lastObservedCurrentChat = currentChat;

            if (chatReferenceChanged && currentChat is not null)
                _chatShell?.EnterFollowTailMode();
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

        viewModel.IsTranscriptRealizing = true;
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
            viewModel.InitializeMountedTranscript(chatShell.ViewportHeight);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;

            viewModel.EnsureMountedTranscriptCoverage(chatShell.ViewportHeight, chatShell.ExtentHeight);
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

            // Keep the loading overlay up (and absorbing clicks) until the deferred, frame-budgeted
            // realization of the mounted turns has finished, then make a final authoritative re-pin to
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
                _subscribedVm.IsTranscriptRealizing = false;
        }
    }

    private async Task WaitForTranscriptRealizationAsync(StrataChatShell chatShell, ChatViewModel viewModel, int syncVersion)
    {
        var scheduler = TranscriptRealizationScheduler.Instance;

        // Bounded so the overlay can never get stuck if work keeps arriving (e.g. opening a chat that
        // is actively streaming). The UI thread is never blocked: we yield at Background priority,
        // interleaving with the scheduler's own drain and the re-pin posts.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        // Reveal the transcript only once it has STOPPED growing, not merely when the realization queue
        // empties. The scheduler dequeues a turn one frame before its (retained) subtree finishes
        // measuring, so HasPendingWork hits 0 a beat before the layout settles; if we revealed then, the
        // final growth would re-pin the scroll AFTER the overlay cleared — a visible jump to the bottom.
        // Instead we keep snapping to the bottom every frame (so all of the growth happens UNDER the
        // overlay) and wait for the scroll extent to hold steady for a couple of frames before revealing.
        // A streaming chat grows continuously, so for it we only wait for the queue to drain.
        var requireExtentStability = !viewModel.IsBusy;
        var lastExtent = double.NaN;
        var stableFrames = 0;
        const int requiredStableFrames = 2;

        while (DateTime.UtcNow < deadline)
        {
            chatShell.NotifyTranscriptLayoutChanged();

            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            if (syncVersion != _initialTranscriptTailSyncVersion || !ReferenceEquals(viewModel, _subscribedVm))
                return;

            var extent = chatShell.ExtentHeight;
            var extentStable = !double.IsNaN(lastExtent) && Math.Abs(extent - lastExtent) < 0.5;
            lastExtent = extent;

            var settled = !scheduler.HasPendingWork && (!requireExtentStability || extentStable);
            if (settled)
            {
                if (++stableFrames >= requiredStableFrames)
                    break;
            }
            else
            {
                stableFrames = 0;
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
        if (_subscribedVm is null)
            return;

        _subscribedVm.UpdateTranscriptPinnedState(e.IsPinnedToBottom, e.DistanceFromBottom);

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

        if (e.IsPinnedToBottom)
            return;

        QueueTranscriptViewportEvaluation();
    }

    private void QueueTranscriptViewportEvaluation()
    {
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
                    || _subscribedVm is null
                    || _chatShell is null
                    || _transcriptScrollViewer is null)
                    return;

                var anchor = _chatShell.IsFollowingTail ? null : CaptureAnchor();
                var mutation = _subscribedVm.EnsureMountedTranscriptCoverage(
                    _chatShell.ViewportHeight,
                    _chatShell.ExtentHeight);

                if (!mutation.HasChanges)
                {
                    mutation = _subscribedVm.UpdateTranscriptViewport(
                        _chatShell.VerticalOffset,
                        _chatShell.ViewportHeight,
                        _chatShell.ExtentHeight,
                        _chatShell.IsPinnedToBottom,
                        _chatShell.CurrentDistanceFromBottom);
                }

                if (!mutation.HasChanges)
                {
                    if (!_viewportEvaluationRequested)
                        return;

                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    continue;
                }

                await CompleteTranscriptMutationAsync(anchor, mutation);

                if (mutation.Kind is not (TranscriptWindowMutationKind.Prepend or TranscriptWindowMutationKind.EnsureCoverage)
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

        var anchor = _chatShell.IsFollowingTail ? null : CaptureAnchor();
        var mutation = _subscribedVm.EnsureMountedTranscriptCoverage(_chatShell.ViewportHeight, _chatShell.ExtentHeight);
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

        if (_chatShell.IsFollowingTail)
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
        var mutation = viewModel.EnsureLatestTranscriptMountedIfAdjacentTailGap();
        if (!mutation.HasChanges)
            return;

        await CompleteTranscriptMutationAsync(anchor, mutation);
    }

    private ScrollAnchorState? CaptureAnchor()
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
    /// Scrolls the transcript to the turn an activity row in the Workspace panel points at. Mirrors the
    /// in-chat search navigation: mount the target's page, force it to realize (mounted turns are lazy
    /// height placeholders until flushed), settle at Background priority, then scroll so the turn lands
    /// just below the header.
    /// </summary>
    private async void OnWorkspaceJumpToTurnRequested(string stableId)
    {
        if (_subscribedVm is null || _chatShell is null || _transcriptScrollViewer is null)
            return;

        var turn = _subscribedVm.TranscriptTurns.FirstOrDefault(t => t.StableId == stableId);
        if (turn is null)
            return;

        _subscribedVm.MountTranscriptPageContainingTurn(turn);

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (FindRealizedTurnControl(stableId) is { } control)
                    TranscriptRealizationScheduler.Instance.FlushControl(control);
            },
            DispatcherPriority.Loaded);

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        var target = FindRealizedTurnControl(stableId);
        var point = target?.TranslatePoint(default, _transcriptScrollViewer);
        if (target is null || point is null)
            return;

        var offset = Math.Max(0, _chatShell.VerticalOffset + point.Value.Y - 64);
        _chatShell.PreserveViewport();
        _chatShell.ScrollToVerticalOffset(offset);
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

            using var bitmap = await dataTransfer.TryGetBitmapAsync();
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;

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

        // Ensure the turn's page is mounted
        _subscribedVm.MountTranscriptPageContainingTurn(hit.Turn);

        // Mounted turns realize lazily under a frame budget, so the freshly mounted target may still
        // be a height-only placeholder. Wait for its control to attach, force that turn to realize now
        // (its markdown then re-parses at Loaded priority), then wait once more so the realized item
        // views are parsed/laid out before we scroll to and highlight the match.
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (FindRealizedTurnControl(hit.Turn.StableId) is { } control)
                    TranscriptRealizationScheduler.Instance.FlushControl(control);
            },
            DispatcherPriority.Loaded);

        // The forced realization above causes StrataMarkdown to post its text rebuild at Loaded
        // priority. Waiting again at Loaded is the same priority as that rebuild, so whether the
        // SelectableTextBlocks exist when HighlightHit runs depends on dispatcher FIFO ordering — a race
        // that silently drops the highlight. Wait at the strictly-lower Background priority instead, which
        // is guaranteed to run only after the entire Loaded queue (including the reparse) has drained.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

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
