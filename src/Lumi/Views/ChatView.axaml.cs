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
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using StrataTheme.Animation;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    private const double TranscriptKeyboardScrollStep = 48;

    public static readonly StyledProperty<bool> ShowInternalTitleProperty =
        AvaloniaProperty.Register<ChatView, bool>(nameof(ShowInternalTitle), true);

    public static readonly StyledProperty<bool> UseShellChromeProperty =
        AvaloniaProperty.Register<ChatView, bool>(nameof(UseShellChrome), true);

    private StrataChatShell? _chatShell;
    private StrataChatComposer? _composer;
    private Button? _backgroundActivityButton;
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
    private TranscriptPagingDirection _pendingTranscriptPagingDirection;
    private bool _hasTranscriptViewportOffset;
    private double _lastTranscriptViewportOffset;
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

    /// <summary>Space kept above and below a search hit brought into view.</summary>
    private const double SearchHitContext = 40;

    // ── Reply jump state ──
    private SelectableTextBlock? _replyHighlightedTextBlock;
    private StrataChatMessage? _replyFlashMessage;
    private DispatcherTimer? _replyFlashTimer;

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

    private void OnInteractiveRequest(object? sender, InteractiveRequestEventArgs request)
    {
        var messageId = request.Source is Control control ? control.DataContext switch
        {
            AssistantMessageItem item => item.MessageId,
            InputRequestItem item => item.Message.Id,
            _ => (Guid?)null
        } : null;
        request.Handled = true;
        // A local preview, such as a fence quoted in the user's own message, has nothing to save or send.
        if (messageId is null)
            return;
        if (_subscribedVm is null)
        {
            request.Completion = Task.FromResult(new InteractiveActionResult(false, "That answer is no longer open."));
            return;
        }
        request.Completion = _subscribedVm.RequestInteractionAsync(
            messageId.Value, request.Definition.Id, request.StateJson, request.Action);
    }

    private async void OnInteractionSourceRequested(object? sender, RoutedEventArgs args)
    {
        if (_subscribedVm?.CurrentChat is not { } chat
            || args.Source is not Control { DataContext: InteractionReceiptItem item })
            return;
        args.Handled = true;
        var sourceId = item.Receipt.SourceMessageId;
        if (!chat.Messages.Any(message => message.Id == sourceId))
        {
            _subscribedVm.StatusText = InteractiveLabels.ForLanguage(Loc.Culture.Name)["sourceUnavailable"];
            return;
        }
        var sourceTurn = _subscribedVm.TranscriptTurns.FirstOrDefault(turn => turn.EnumerateItems().Any(child =>
            child is AssistantMessageItem assistant && assistant.MessageId == sourceId));
        if (sourceTurn is null) return;
        var sourceItem = sourceTurn.EnumerateItems().First(child =>
            child is AssistantMessageItem assistant && assistant.MessageId == sourceId);
        var revealed = sourceTurn.RevealFoldedItem(sourceItem);
        var realized = await EnsureTurnRealizedAsync(sourceTurn);
        if (revealed && realized is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await RealizeFoldedItemAsync(realized, sourceItem);
        }
        var target = realized?.GetVisualDescendants().OfType<StrataInteractiveBlock>()
            .FirstOrDefault(control => control.Definition?.Id == item.Receipt.BlockId);
        if (target is null || _transcriptScrollViewer is null) return;
        await RevealWithinFoldAsync(target);
        if (target.TranslatePoint(default, _transcriptScrollViewer) is { } position)
        {
            _chatShell?.PreserveViewport();
            _chatShell?.ScrollToVerticalOffset(
                Math.Max(0, _chatShell.VerticalOffset + position.Y - _chatShell.TopChromeInset - 48));
        }
        target.Focus();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        ApplyShellChrome();
        InitializeHeader();
        _composer = this.FindControl<StrataChatComposer>("Composer");
        _backgroundActivityButton = this.FindControl<Button>("BackgroundActivityButton");
        if (_backgroundActivityButton?.Flyout is { } activityFlyout)
            activityFlyout.Opened += OnBackgroundActivityFlyoutOpened;
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
                if (e.Property == IsVisibleProperty && codingStrip.IsVisible
                    && _subscribedVm?.AreAnimationsEnabled != false)
                    SlideFadeEntrance.Play(codingStrip, offsetY: 6);
            };
        }

        // Follow-up chips rise in one after another when a reply's suggestions arrive. The motion runs on
        // the render thread, so it still plays while opening a chat keeps the UI thread busy. Suggestions
        // are applied one at a time, so it starts once the set is complete, still before the next frame.
        var suggestionRow = this.FindControl<Panel>("SuggestionChipsRow");
        if (suggestionRow is not null)
        {
            suggestionRow.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && suggestionRow.IsVisible
                    && _subscribedVm?.AreAnimationsEnabled != false)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (suggestionRow.IsVisible)
                            PlayFollowUpEntrance(suggestionRow);
                    }, DispatcherPriority.Normal);
                }
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
        AddHandler(StrataFileAttachment.PreviewRequestedEvent, OnFileAttachmentPreviewRequested);
        AddHandler(StrataChatMessage.CopyRequestedEvent, OnCopyMessageRequested);
        AddHandler(StrataChatMessage.ForkRequestedEvent, OnForkRequested);
        AddHandler(StrataChatMessage.ReplyRequestedEvent, OnReplyRequested);
        AddHandler(StrataChatMessage.ShareRequestedEvent, OnSharePromptRequested);
        AddHandler(StrataInteractiveBlock.RequestedEvent, OnInteractiveRequest);
        AddHandler(StrataActionReceipt.SourceRequestedEvent, OnInteractionSourceRequested);
        AddHandler(KeyDownEvent, OnLinkedChatKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnEmptyComposerScrollKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnTranscriptScrollKeyDown, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnComposerReplyKeyDown, RoutingStrategies.Bubble);
        SizeChanged += OnChatViewSizeChanged;

        // The pending-reply card rises into the composer like the other composer chrome.
        var replyCard = this.FindControl<Border>("ComposerReplyCard");
        if (replyCard is not null)
        {
            replyCard.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && replyCard.IsVisible
                    && _subscribedVm?.AreAnimationsEnabled != false)
                    SlideFadeEntrance.Play(replyCard, offsetY: 6);
            };
        }

        // So does the paused bar, when a chat pauses or a paused chat opens. It moves on the render thread,
        // as opening a chat keeps the UI thread busy.
        var pauseBar = this.FindControl<Border>("ChatPauseStatusPanel");
        if (pauseBar is not null)
        {
            pauseBar.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && pauseBar.IsVisible
                    && _subscribedVm?.AreAnimationsEnabled != false)
                    SlideFadeEntrance.PlayComposited(pauseBar, offsetY: 6);
            };
        }

        // Voice typing's cards rise into the composer as they take over from one another.
        foreach (var name in new[] { "VoiceSetupCard", "VoiceDownloadCard", "VoiceProblemCard" })
        {
            if (this.FindControl<Border>(name) is not { } voiceCard)
                continue;
            voiceCard.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && voiceCard.IsVisible
                    && _subscribedVm?.AreAnimationsEnabled != false)
                    SlideFadeEntrance.Play(voiceCard, offsetY: 6);
            };
        }

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

        InitializeLaunchpad();
    }

    private void ApplyShellChrome()
    {
        _chatShell?.Classes.Set("flat-window", !UseShellChrome);
    }

    private static void PlayFollowUpEntrance(Panel row)
    {
        var delay = TimeSpan.Zero;
        foreach (var chip in row.Children)
        {
            if (!chip.IsVisible)
                continue;
            SlideFadeEntrance.PlayComposited(chip, offsetY: 8, TimeSpan.FromMilliseconds(280), delay);
            delay += TimeSpan.FromMilliseconds(60);
        }
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
        ResetSearchState();
        ResetUserMessageNavigation();
        _viewportEvaluationRequested = false;
        ResetTranscriptPagingInputState();
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
            vm.FocusComposerAtEndRequested += FocusComposerAtEnd;
            vm.WorkspaceJumpToTurnRequested += OnWorkspaceJumpToTurnRequested;
            vm.ReplySourceJumpRequested += OnReplySourceJumpRequested;
            vm.SubagentRunShowRequested += OnSubagentRunShowRequested;
            SubscribeToMountedTurns(vm.MountedTranscriptTurns);
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
            QueueInitialTranscriptTailSyncIfNeeded(vm);
            // Match the transcript's materialize state to the new surface: if it's already loading or
            // about to realize, start hidden so it composes in rather than flashing placeholders.
            SetTranscriptMaterialized(!vm.IsChatSurfaceLoading);
        }

        QueueWorktreeToggleHighlightUpdate();
        UpdateLaunchpadActivation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachUserMessageNavigation(TopLevel.GetTopLevel(this));
        Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
        UpdateLaunchpadActivation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        AttachUserMessageNavigation(null);
        DetachTranscriptScrollViewer();
        UnsubscribeMountedTurns();
        UnsubscribeFromViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    public void FocusComposer()
    {
        _composer?.FocusInput();
    }

    private void FocusComposerAtEnd() => _composer?.FocusInputAtEnd();

    private async void OnBackgroundActivityFlyoutOpened(object? sender, EventArgs e)
    {
        if (_subscribedVm is { } vm)
            await vm.RefreshBackgroundActivityAsync();
    }

    /// <summary>An agent's run opening in the Workspace takes over from the transient activity list.</summary>
    private void OnSubagentRunShowRequested() => _backgroundActivityButton?.Flyout?.Hide();

    private void UnsubscribeFromViewModel()
    {
        DeactivateLaunchpad();
        _backgroundActivityButton?.Flyout?.Hide();
        if (_subscribedVm is null) return;
        _subscribedVm.StopVoiceIfRecording();
        _subscribedVm.ScrollToEndRequested -= OnScrollToEndRequested;
        _subscribedVm.UserMessageSent -= OnUserMessageSent;
        _subscribedVm.TranscriptRebuilt -= OnTranscriptRebuilt;
        _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm.AttachFilesRequested -= OnAttachFilesRequested;
        _subscribedVm.ClipboardPasteRequested -= OnClipboardPasteRequested;
        _subscribedVm.CopyToClipboardRequested -= OnCopyToClipboardRequested;
        _subscribedVm.FocusComposerRequested -= FocusComposer;
        _subscribedVm.FocusComposerAtEndRequested -= FocusComposerAtEnd;
        _subscribedVm.WorkspaceJumpToTurnRequested -= OnWorkspaceJumpToTurnRequested;
        _subscribedVm.ReplySourceJumpRequested -= OnReplySourceJumpRequested;
        _subscribedVm.SubagentRunShowRequested -= OnSubagentRunShowRequested;
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

        if (_subscribedVm.MaintainsStableTranscriptMembership)
            return;

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
            || !_heightSubscribedTurns.Contains(turn))
            return;

        _observedTurnHeights.TryGetValue(turn.StableId, out var previousHeight);
        _observedTurnHeights[turn.StableId] = turn.MeasuredHeight;

        if (_chatShell is null
            || _transcriptScrollViewer is null
            || _subscribedVm is null
            || _isApplyingTranscriptMutation)
            return;

        var delta = turn.MeasuredHeight - previousHeight;
        if (Math.Abs(delta) < 0.5)
            return;

        if (_subscribedVm is { IsBusy: true }
            || (_chatShell.IsFollowingTail && !_subscribedVm.HasUnmountedTranscriptTail))
        {
            if (!_subscribedVm.MaintainsStableTranscriptMembership)
                QueueTranscriptViewportEvaluation();
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        var control = FindRealizedTurnControl(turn.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        if (control.IsViewportManaged && _transcriptScrollViewer.CurrentAnchor is not null)
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

        ClearTranscriptScrollbarDrag();
        TryRestoreFollowTailAtActualBottom(_pendingTranscriptPagingDirection);
        QueueTranscriptViewportEvaluation();
    }

    private void ClearTranscriptScrollbarDrag()
    {
        _isTranscriptScrollbarDragging = false;
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

    private void OnEmptyComposerScrollKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || e.KeyModifiers != KeyModifiers.None
            || e.Key is not (Key.Up or Key.Down)
            || _chatShell is null
            || _transcriptScrollViewer is null)
        {
            return;
        }

        var sourceControl = e.Source as Control;
        var input = sourceControl as TextBox
            ?? sourceControl?.FindAncestorOfType<TextBox>();
        if (input?.Name != "PART_Input"
            || !ReferenceEquals(input.FindAncestorOfType<StrataChatComposer>(), _composer)
            || !string.IsNullOrEmpty(input.Text))
        {
            return;
        }

        ScrollTranscriptByKeyboard(e);
    }

    private void OnTranscriptScrollKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || e.KeyModifiers != KeyModifiers.None
            || e.Key is not (Key.Up or Key.Down)
            || _transcriptScrollViewer is null
            || e.Source is not Control sourceControl)
        {
            return;
        }

        var direction = e.Key == Key.Up
            ? TranscriptPagingDirection.TowardOlder
            : TranscriptPagingDirection.TowardNewer;
        var isWithinTranscript = ReferenceEquals(sourceControl, _transcriptScrollViewer)
            || sourceControl.GetVisualAncestors().Contains(_transcriptScrollViewer);
        if (!isWithinTranscript
            || sourceControl is TextBox
            || sourceControl.FindAncestorOfType<TextBox>() is not null
            || IsTranscriptScrollbarInteraction(sourceControl)
            || CanNestedScrollViewerConsume(sourceControl, direction))
        {
            return;
        }

        ScrollTranscriptByKeyboard(e);
    }

    private void ScrollTranscriptByKeyboard(KeyEventArgs e)
    {
        if (_chatShell is null || _transcriptScrollViewer is null)
            return;

        var maxOffset = Math.Max(0, _transcriptScrollViewer.Extent.Height - _transcriptScrollViewer.Viewport.Height);
        if (maxOffset <= ChatScrollPolicy.FractionalEpsilon)
            return;

        var direction = e.Key == Key.Up
            ? TranscriptPagingDirection.TowardOlder
            : TranscriptPagingDirection.TowardNewer;
        var delta = direction == TranscriptPagingDirection.TowardOlder
            ? -TranscriptKeyboardScrollStep
            : TranscriptKeyboardScrollStep;
        var targetOffset = Math.Clamp(_transcriptScrollViewer.Offset.Y + delta, 0, maxOffset);

        _chatShell.PreserveViewport();
        _transcriptScrollViewer.Offset = _transcriptScrollViewer.Offset.WithY(targetOffset);
        RecordTranscriptPagingDirection(direction);
        e.Handled = true;
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

        _pendingTranscriptPagingDirection = direction;
        TryRestoreFollowTailAtActualBottom(direction);
        if (!_isTranscriptScrollbarDragging)
            QueueTranscriptViewportEvaluation();
    }

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
        if (_subscribedVm is null || _chatShell is null)
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

            if (chatReferenceChanged)
            {
                _backgroundActivityButton?.Flyout?.Hide();
                ResetUserMessageNavigation();
            }

            if (chatReferenceChanged && currentChat is not null)
            {
                _chatShell?.EnterFollowTailMode();
                SyncTranscriptPinnedState();
            }
        }

        if (e.PropertyName == nameof(ChatViewModel.HasBackgroundActivity)
            && _subscribedVm?.HasBackgroundActivity != true)
        {
            _backgroundActivityButton?.Flyout?.Hide();
        }

        if (e.PropertyName == nameof(ChatViewModel.IsWelcomeVisible))
            UpdateLaunchpadActivation();

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
        if (_subscribedVm is null || _chatShell is null)
            return;

        ObserveTranscriptScrollbarPagingDirection(e.VerticalOffset);

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
        if (_subscribedVm?.MaintainsStableTranscriptMembership == true)
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
                var mutation = _subscribedVm.EnsureMountedTranscriptCoverage(
                    _chatShell.ViewportHeight,
                    _chatShell.ExtentHeight);

                if (!mutation.HasChanges)
                {
                    mutation = _subscribedVm.UpdateTranscriptViewport(
                        _chatShell.VerticalOffset,
                        _chatShell.ViewportHeight,
                        _chatShell.ExtentHeight,
                        isFollowingActualTail,
                        _chatShell.IsPinnedToBottom && isActualTailMounted,
                        _chatShell.CurrentDistanceFromBottom,
                        pagingDirection);
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

        if (_subscribedVm.MaintainsStableTranscriptMembership)
        {
            _chatShell.NotifyTranscriptLayoutChanged();
            return;
        }

        var isActualTailMounted = !_subscribedVm.HasUnmountedTranscriptTail;
        if (!isActualTailMounted && _chatShell.IsFollowingTail)
            _chatShell.PreserveViewport();

        var anchor = _chatShell.IsFollowingTail && isActualTailMounted ? null : CaptureAnchor();
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
    /// Scrolls the transcript to the turn an activity row in the Workspace panel points at. Far turns
    /// remain height-only placeholders until requested; scroll the stable item into view, realize it,
    /// then place it just below the header.
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

        var offset = Math.Max(0, _chatShell.VerticalOffset + point.Value.Y - _chatShell.TopChromeInset - 64);
        _chatShell.PreserveViewport();
        _chatShell.ScrollToVerticalOffset(offset);
    }

    /// <param name="settleContent">When false, return as soon as the turn's control is realized, without
    /// bringing it into view or waiting for its markdown to finish rebuilding. For callers that position
    /// the turn themselves and only need its top-level controls.</param>
    private async Task<TranscriptTurnControl?> EnsureTurnRealizedAsync(TranscriptTurn turn, bool settleContent = true)
    {
        if (_subscribedVm is null || _transcript is null)
            return null;

        _chatShell?.PreserveViewport();
        var index = _subscribedVm.MountedTranscriptTurns.IndexOf(turn);
        if (index < 0)
        {
            var wasApplyingMutation = _isApplyingTranscriptMutation;
            _isApplyingTranscriptMutation = true;
            try
            {
                if (!_subscribedVm.MountTranscriptPageContainingTurn(turn))
                    return null;

                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                index = _subscribedVm.MountedTranscriptTurns.IndexOf(turn);
                if (index < 0)
                    return null;

                _transcript.ScrollIntoView(index);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            }
            finally
            {
                _isApplyingTranscriptMutation = wasApplyingMutation;
                if (!wasApplyingMutation && _viewportEvaluationRequested)
                    QueueTranscriptViewportEvaluation();
            }
        }
        else
        {
            _transcript.ScrollIntoView(index);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }

        var control = FindRealizedTurnControl(turn.StableId);
        if (control is null)
            return null;

        if (!settleContent)
        {
            control.SetViewportActive(true);
            TranscriptRealizationScheduler.Instance.FlushControl(control);
            return control;
        }

        ((Control?)control.GetVisualParent() ?? control).BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        control.SetViewportActive(true);
        TranscriptRealizationScheduler.Instance.FlushControl(control);

        // Markdown realization posts its parse/rebuild work at Loaded priority. Background runs only
        // after that queue drains, so callers can safely inspect or highlight the realized subtree.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return control;
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
            if (vm.TryImportPastedPrompt(clipboardText))
                return;
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

            // Office apps also copy an image of selected text; prefer the text representation.
            if (!string.IsNullOrEmpty(clipboardText))
            {
                _composer?.InsertTextAtSelection(clipboardText);
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
    {
        try
        {
            await SetClipboardTextAsync(text);
        }
        catch (Exception error)
        {
            Logger.TryGet(LogEventLevel.Error, "Clipboard")?.Log(
                this, "Could not copy text: {Error}", error.Message);
        }
    }

    private Task SetClipboardTextAsync(string text, ClipboardCopyPayload? payload = null) =>
        SetClipboardDataAsync(ChatClipboardData.CreateText(text), payload);

    private async Task SetClipboardDataAsync(DataTransfer data, ClipboardCopyPayload? payload = null)
    {
        using (data)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException("The system clipboard is unavailable.");
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
    }

    private static bool HasCopyContext(ClipboardCopyPayload payload)
        => payload.AttachmentPaths.Count > 0 || payload.SkillNames.Count > 0 || payload.Sources.Count > 0;

    private Task SetClipboardFormattedAsync(
        string markdown, StrataCopyFormat format, ClipboardCopyPayload? payload = null, Control? renderedContent = null)
        => SetClipboardDataAsync(format switch
        {
            StrataCopyFormat.RichText => ChatClipboardData.CreateRichText(markdown, renderedContent),
            StrataCopyFormat.Markdown => ChatClipboardData.CreateMarkdown(markdown),
            _ => ChatClipboardData.CreateText(MarkdownClipboardFormatter.ToPlainText(markdown))
        }, payload);

    private void OnCopyMessageRequested(object? sender, StrataCopyRequestedEventArgs e)
    {
        if (e.Source is not StrataChatMessage message)
            return;

        e.Handled = true;

        if (e.IsSelection)
            return;

        var payload = BuildMessageCopyPayload(message.DataContext, message.Content);
        if (payload is null)
            return;

        var renderedMarkdown = message.GetVisualDescendants().OfType<StrataMarkdown>()
            .FirstOrDefault(markdown => string.Equals(markdown.Markdown?.Trim(), payload.Text, StringComparison.Ordinal));
        var portable = payload with
        {
            Text = renderedMarkdown?.GetCopyMarkdown() ?? MarkdownClipboardFormatter.ToPortableMarkdown(
                payload.Text, (message.DataContext as AssistantMessageItem)?.InteractionStates)
        };
        var text = FormatClipboardText(portable);
        if (string.IsNullOrWhiteSpace(text))
            return;

        e.ClipboardWriteTask = SetClipboardFormattedAsync(text, e.Format, portable, message);
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

    private void OnSharePromptRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Source is StrataChatMessage { DataContext: AssistantMessageItem { CanSharePrompt: true } assistant }
            && DataContext is ChatViewModel vm)
            vm.RequestSharePrompt(assistant.MessageId);
    }

    // ── Reply to (meta-row action, context menu, or selection pill on assistant messages) ───

    private void OnReplyRequested(object? sender, StrataReplyRequestedEventArgs e)
    {
        e.Handled = true;

        if (e.Source is not StrataChatMessage { DataContext: AssistantMessageItem assistant }
            || DataContext is not ChatViewModel vm)
        {
            return;
        }

        // A whole-message reply quotes the message's own markdown, not the extracted visual text.
        vm.BeginReply(
            assistant.MessageId,
            e.IsSelection ? e.Text : assistant.Content,
            e.IsSelection,
            assistant.Author);
    }

    /// <summary>Esc in the composer drops a pending reply (after autocomplete and edit mode had their turn).</summary>
    private void OnComposerReplyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape
            || e.KeyModifiers != KeyModifiers.None
            || _subscribedVm is not { HasPendingReply: true, IsEditingMessage: false } vm
            || _composer is null
            || e.Source is not Visual source
            || !(ReferenceEquals(source, _composer) || _composer.IsVisualAncestorOf(source)))
        {
            return;
        }

        e.Handled = true;
        vm.CancelReplyCommand.Execute(null);
    }

    /// <summary>
    /// Scrolls to the message a reply quotes. A selected excerpt is re-selected in place so the eye
    /// lands on the exact words; the message itself briefly glows either way.
    /// </summary>
    private async void OnReplySourceJumpRequested(MessageReply reply)
    {
        if (_subscribedVm is null || _chatShell is null || _transcriptScrollViewer is null)
            return;

        TranscriptTurn? sourceTurn = null;
        AssistantMessageItem? sourceItem = null;
        foreach (var turn in _subscribedVm.TranscriptTurns)
        {
            sourceItem = turn.EnumerateItems()
                .OfType<AssistantMessageItem>()
                .FirstOrDefault(item => item.MessageId == reply.MessageId);
            if (sourceItem is not null)
            {
                sourceTurn = turn;
                break;
            }
        }

        if (sourceTurn is null || sourceItem is null)
            return;

        // A reply to a progress update lands inside the folded work, so open it first.
        var revealed = sourceTurn.RevealFoldedItem(sourceItem);
        var turnControl = await EnsureTurnRealizedAsync(sourceTurn);
        if (revealed && turnControl is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await RealizeFoldedItemAsync(turnControl, sourceItem);
        }
        var message = turnControl is not null && FindTranscriptItemView(turnControl, sourceItem) is { } itemView
            ? itemView as StrataChatMessage ?? itemView.GetVisualDescendants().OfType<StrataChatMessage>().FirstOrDefault()
            : null;
        if (message is null || _transcriptScrollViewer is null)
            return;

        Control target = message;
        if (reply.IsSelection && SelectReplyQuote(message, reply.Quote) is { } quotedText)
            target = quotedText;

        await RevealWithinFoldAsync(target);
        if (_transcriptScrollViewer is null || _chatShell is null)
            return;

        // Land the quote in the upper part of the viewport, like jumping to a search hit.
        if (target.TranslatePoint(default, _transcriptScrollViewer) is { } position)
        {
            var viewportInset = Math.Max(64, _transcriptScrollViewer.Viewport.Height * 0.25);
            _chatShell.PreserveViewport();
            _chatShell.ScrollToVerticalOffset(Math.Max(0, _chatShell.VerticalOffset + position.Y - viewportInset));
        }

        FlashMessage(message);
    }

    /// <summary>
    /// Selects the quoted excerpt inside <paramref name="message"/>'s rendered text. The quote was
    /// captured from one text block's selection and then normalized, so each block's text is
    /// normalized the same way (with a position map back to the raw text) before matching.
    /// </summary>
    private SelectableTextBlock? SelectReplyQuote(StrataChatMessage message, string quote)
    {
        var needle = quote.EndsWith('…') ? quote[..^1].TrimEnd() : quote;
        if (needle.Length == 0)
            return null;

        ClearReplyQuoteHighlight();
        foreach (var textBlock in message.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (!textBlock.IsEffectivelyVisible)
                continue;

            var raw = textBlock.Inlines is { Count: > 0 } inlines ? inlines.Text : textBlock.Text;
            if (string.IsNullOrEmpty(raw))
                continue;

            var normalized = new StringBuilder(raw.Length);
            var rawIndexes = new List<int>(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                if (MessageReplyFormatter.IsRenderingArtifact(raw[i]))
                    continue;

                normalized.Append(raw[i]);
                rawIndexes.Add(i);
            }

            var start = normalized.ToString().IndexOf(needle, StringComparison.Ordinal);
            if (start < 0)
                continue;

            textBlock.SelectionStart = rawIndexes[start];
            textBlock.SelectionEnd = rawIndexes[start + needle.Length - 1] + 1;
            _replyHighlightedTextBlock = textBlock;
            return textBlock;
        }

        return null;
    }

    private void ClearReplyQuoteHighlight()
    {
        // Once the user clicks into that text, its selection is theirs rather than the jump's.
        if (_replyHighlightedTextBlock is { IsFocused: false } textBlock)
        {
            textBlock.SelectionStart = 0;
            textBlock.SelectionEnd = 0;
        }

        _replyHighlightedTextBlock = null;
    }

    /// <summary>
    /// Briefly glows a jumped-to message (a reply source or a user message reached by keyboard). Any
    /// reply-quote highlight goes with the glow: left in place it would linger as a selection that the
    /// message's copy and reply actions would act on.
    /// </summary>
    private void FlashMessage(StrataChatMessage message)
    {
        _replyFlashTimer?.Stop();
        _replyFlashMessage?.Classes.Remove("reply-flash");

        _replyFlashMessage = message;
        message.Classes.Add("reply-flash");

        if (_replyFlashTimer is null)
        {
            _replyFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _replyFlashTimer.Tick += (_, _) =>
            {
                _replyFlashTimer.Stop();
                _replyFlashMessage?.Classes.Remove("reply-flash");
                _replyFlashMessage = null;
                ClearReplyQuoteHighlight();
            };
        }

        _replyFlashTimer.Start();
    }

    private static ClipboardCopyPayload? BuildMessageCopyPayload(object? dataContext, object? content)
    {
        return dataContext switch
        {
            InteractionReceiptItem receipt => CreatePayload(receipt.Receipt.Detail, [], [], []),
            InputRequestItem input => CreatePayload(input.Message.ToolOutput ?? input.Definition.Title, [], [], []),
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

    private void OnFileAttachmentPreviewRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is StrataFileAttachment { DataContext: FileAttachmentItem item }
            && DataContext is ChatViewModel viewModel)
        {
            viewModel.OpenFilePreview(item.FilePath);
            e.Handled = true;
        }
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

        // Search ALL transcript turns (including unmounted/off-screen) and the work folded into them
        foreach (var turn in _subscribedVm.TranscriptTurns)
        {
            foreach (var item in turn.EnumerateItems())
            {
                var content = item switch
                {
                    UserMessageItem u => u.Content,
                    InteractionReceiptItem receipt => receipt.Receipt.Title + "\n" + receipt.Receipt.Summary + "\n" + receipt.Receipt.Detail,
                    InputRequestItem input => input.Definition.Title + "\n" + input.Definition.Description,
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

        var revealed = hit.Turn.RevealFoldedItem(hit.Item);
        if (await EnsureTurnRealizedAsync(hit.Turn) is not { } turnControl)
            return;
        if (revealed)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await RealizeFoldedItemAsync(turnControl, hit.Item);
        }

        if (HighlightHit(hit) is not { } stb)
            return;

        // Room around the hit keeps it clear of the floating header's soft edge, with context.
        var context = new Rect(0, -SearchHitContext, stb.Bounds.Width, stb.Bounds.Height + 2 * SearchHitContext);
        await RevealWithinFoldAsync(stb, context);
        if (ReferenceEquals(_highlightedStb, stb))
            stb.BringIntoView(context);
    }

    /// <summary>Selects the hit's text and returns the text block that shows it.</summary>
    private SelectableTextBlock? HighlightHit(SearchHit hit)
    {
        var query = hit.Query;
        if (string.IsNullOrEmpty(query) || _transcript is null) return null;

        // Find the visual for this item: a directly-built host in its turn, or a view inside an
        // opened history row.
        var itemVisual = FindTranscriptItemView(_transcript, hit.Item);
        if (itemVisual is null) return null;

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
                    return stb;
                }

                occurrencesSeen++;
                searchFrom = idx + query.Length;
            }
        }

        return null;
    }

    /// <summary>
    /// The view of a transcript item. Turn items are directly-built hosts carrying the HostedItem
    /// attached property; items inside an opened history row are presented by its item list.
    /// </summary>
    private static Control? FindTranscriptItemView(Visual root, TranscriptItem item)
    {
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is Control control
                && (ReferenceEquals(TranscriptTurnControl.GetHostedItem(control), item)
                    || control is ContentPresenter { Content: var content } && ReferenceEquals(content, item)))
            {
                return control;
            }
        }

        return null;
    }

    /// <summary>
    /// Opened work shows its steps from the top a few at a time, so a step further along may not be
    /// built yet: the row is shown through it first, and its view exists afterwards.
    /// </summary>
    private static async Task RealizeFoldedItemAsync(Visual turnView, TranscriptItem item)
    {
        var steps = turnView.GetVisualDescendants().OfType<ProgressiveItemsControl>()
            .FirstOrDefault(list => list.Source?.Contains(item) == true);
        if (steps is null)
            return;

        await steps.ShowThroughAsync(item);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Folded work scrolls inside its own bounded area, and that area keeps a request to bring a target
    /// into view whenever it has to scroll for it. So a target there is first scrolled into that area's
    /// view; asking the transcript to show it afterwards, or measuring against the transcript, then
    /// lands where the reader will see it.
    /// </summary>
    private static async Task RevealWithinFoldAsync(Control target, Rect? targetRect = null)
    {
        if (target.FindAncestorOfType<StrataTurnSummary>() is null)
            return;

        if (targetRect is { } rect)
            target.BringIntoView(rect);
        else
            target.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
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
