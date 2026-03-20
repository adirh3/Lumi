using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    private const double ScrollMutationDeltaThreshold = 0.05d;
    private const double LayoutShiftDeltaTolerance = 0.2d;

    private StrataChatShell? _chatShell;
    private StrataChatComposer? _composer;
    private Panel? _composerSpacer;
    private Panel? _dropOverlay;
    private ItemsControl? _transcript;
    private ScrollViewer? _transcriptScrollViewer;

    private ChatViewModel? _subscribedVm;
    private ObservableCollection<TranscriptTurn>? _subscribedMountedTurns;
    private Border? _worktreeHighlight;
    private Button? _localToggleBtn;
    private Button? _worktreeToggleBtn;
    private bool _isApplyingTranscriptMutation;
    private bool _resizeRestoreQueued;
    private bool _viewportEvaluationQueued;
    private bool _viewportEvaluationRequested;
    private ScrollAnchorState? _pendingResizeAnchor;
    private readonly Dictionary<string, double> _observedTurnHeights = new(StringComparer.Ordinal);

    private static readonly string ClipboardImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "clipboard-images");

    private sealed record ScrollAnchorState(string StableId, double ViewportY);

    public ChatView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _composer = this.FindControl<StrataChatComposer>("Composer");
        _composerSpacer = this.FindControl<Panel>("ComposerSpacer");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");
        _transcript = this.FindControl<ItemsControl>("Transcript");

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
            togglePanel.SizeChanged += (_, _) => UpdateWorktreeToggleHighlight();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(StrataFileAttachment.OpenRequestedEvent, OnFileAttachmentOpenRequested);
        AddHandler(StrataChatMessage.CopyTurnRequestedEvent, OnCopyTurnRequested);
        SizeChanged += OnChatViewSizeChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeFromViewModel();

        if (DataContext is ChatViewModel vm)
        {
            _subscribedVm = vm;
            vm.ScrollToEndRequested += OnScrollToEndRequested;
            vm.UserMessageSent += OnUserMessageSent;
            vm.TranscriptRebuilt += OnTranscriptRebuilt;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.AttachFilesRequested += OnAttachFilesRequested;
            vm.ClipboardPasteRequested += OnClipboardPasteRequested;
            vm.CopyToClipboardRequested += OnCopyToClipboardRequested;
            vm.FocusComposerRequested += FocusComposer;
            SubscribeToMountedTurns(vm.MountedTranscriptTurns);
            Dispatcher.UIThread.Post(EnsureTranscriptScrollViewer, DispatcherPriority.Loaded);
        }
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
        _subscribedVm = null;
    }

    private void SubscribeToMountedTurns(ObservableCollection<TranscriptTurn> mountedTurns)
    {
        UnsubscribeMountedTurns();
        _subscribedMountedTurns = mountedTurns;
        _subscribedMountedTurns.CollectionChanged += OnMountedTurnsChanged;
        foreach (var turn in _subscribedMountedTurns)
            SubscribeToTurn(turn);
    }

    private void UnsubscribeMountedTurns()
    {
        if (_subscribedMountedTurns is null)
            return;

        _subscribedMountedTurns.CollectionChanged -= OnMountedTurnsChanged;
        foreach (var turn in _subscribedMountedTurns)
            turn.PropertyChanged -= OnMountedTurnPropertyChanged;

        _observedTurnHeights.Clear();
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

        _transcriptScrollViewer.ScrollChanged += OnTranscriptScrollChanged;
        _transcriptScrollViewer.SizeChanged += OnTranscriptViewportSizeChanged;
    }

    private void DetachTranscriptScrollViewer()
    {
        if (_transcriptScrollViewer is null)
            return;

        _transcriptScrollViewer.ScrollChanged -= OnTranscriptScrollChanged;
        _transcriptScrollViewer.SizeChanged -= OnTranscriptViewportSizeChanged;
        _transcriptScrollViewer = null;
    }

    private void OnMountedTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TranscriptTurn turn in e.OldItems)
            {
                turn.PropertyChanged -= OnMountedTurnPropertyChanged;
                _observedTurnHeights.Remove(turn.StableId);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TranscriptTurn turn in e.NewItems)
                SubscribeToTurn(turn);
        }
    }

    private void SubscribeToTurn(TranscriptTurn turn)
    {
        _observedTurnHeights[turn.StableId] = turn.MeasuredHeight;
        turn.PropertyChanged += OnMountedTurnPropertyChanged;
    }

    // ── Scroll management ────────────────────────────────

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

        if (TurnHasStreamingContent(turn))
            return;

        var control = FindRealizedTurnControl(turn.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        if (_chatShell.IsPinnedToBottom)
            return;

        if (point.Value.Y + control.Bounds.Height > 0)
            return;

        var beforeOffset = _chatShell.VerticalOffset;
        _chatShell.ScrollToVerticalOffset(beforeOffset + delta);
        _subscribedVm?.RecordTranscriptScrollCompensation("height-change", beforeOffset, _chatShell.VerticalOffset);
    }

    private void OnScrollToEndRequested() => _chatShell?.ScrollToEnd();

    private void OnUserMessageSent()
    {
        _chatShell?.ResetAutoScroll();
        _subscribedVm?.EnsureLatestTranscriptMounted();
        _chatShell?.ScrollToEnd();
        Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private async void OnTranscriptRebuilt()
    {
        if (_subscribedVm is null || _chatShell is null)
            return;

        var ready = await EnsureTranscriptScrollViewerReadyAsync();
        if (!ready || _subscribedVm is null || _chatShell is null)
            return;

        var chatShell = _chatShell;
        var viewModel = _subscribedVm;

        chatShell.ResetAutoScroll();
        viewModel.InitializeMountedTranscript(chatShell.ViewportHeight);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        viewModel.EnsureMountedTranscriptCoverage(chatShell.ViewportHeight);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        chatShell.ScrollToEnd();
        viewModel.UpdateTranscriptPinnedState(chatShell.IsPinnedToBottom, chatShell.CurrentDistanceFromBottom);
        FocusComposer();

        // After ScrollToEnd settles, adjust if the user message is barely hidden.
        // Only for completed chats — don't fight auto-scroll during streaming.
        if (!viewModel.IsBusy)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            AdjustScrollAfterTurnCompleted();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat) && _subscribedVm?.CurrentChat is not null)
            _chatShell?.ResetAutoScroll();

        if (e.PropertyName == nameof(ChatViewModel.IsWorktreeMode))
            UpdateWorktreeToggleHighlight();

        // After a turn completes, nudge the scroll position so the user's
        // message isn't hidden just above the viewport.
        if (e.PropertyName == nameof(ChatViewModel.IsBusy) && _subscribedVm is { IsBusy: false })
            Dispatcher.UIThread.Post(AdjustScrollAfterTurnCompleted, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// When a short exchange (user message + few tool calls + response) barely
    /// exceeds the viewport, the streaming auto-scroll pushes the user message
    /// just above the visible area. Detect this and scroll back to show the
    /// full exchange.
    /// </summary>
    private void AdjustScrollAfterTurnCompleted()
    {
        if (_chatShell is null || _transcriptScrollViewer is null)
            return;

        var offset = _transcriptScrollViewer.Offset.Y;
        if (offset <= 0)
            return;

        // Only adjust when the transcript was auto-scrolled to the bottom
        // (user didn't scroll away during the turn).
        if (_chatShell.CurrentDistanceFromBottom > 20)
            return;

        // If the scroll offset from the top is small, the first user message
        // is likely just barely above the viewport. A user message bubble is
        // typically ~150px; with padding and spacing the first turn occupies
        // roughly ~200px. Use a generous threshold to cover varied layouts.
        if (offset <= 350)
            _chatShell.ScrollToVerticalOffset(0);
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isApplyingTranscriptMutation || _subscribedVm is null || _chatShell is null || _transcriptScrollViewer is null)
            return;

        _subscribedVm.UpdateTranscriptPinnedState(_chatShell.IsPinnedToBottom, _chatShell.CurrentDistanceFromBottom);

        if (Math.Abs(e.OffsetDelta.Y) < ScrollMutationDeltaThreshold)
            return;

        if (IsLikelyLayoutDrivenOffsetChange(e))
            return;

        QueueTranscriptViewportEvaluation();
    }

    private void QueueTranscriptViewportEvaluation()
    {
        _viewportEvaluationRequested = true;
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

                if (_isApplyingTranscriptMutation || _subscribedVm is null || _chatShell is null || _transcriptScrollViewer is null)
                    return;

                var anchor = CaptureAnchor();
                var mutation = _subscribedVm.UpdateTranscriptViewport(
                    _chatShell.VerticalOffset,
                    _chatShell.ViewportHeight,
                    _chatShell.ExtentHeight);

                if (!mutation.HasChanges)
                {
                    if (!_viewportEvaluationRequested)
                        return;

                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                    continue;
                }

                await CompleteTranscriptMutationAsync(anchor, mutation);

                if (mutation.Kind != TranscriptWindowMutationKind.Prepend && !_viewportEvaluationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            _viewportEvaluationQueued = false;
            if (_viewportEvaluationRequested)
                QueueTranscriptViewportEvaluation();
        }
    }

    private async void OnTranscriptViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isApplyingTranscriptMutation || _subscribedVm is null || _chatShell is null)
            return;

        var anchor = _chatShell.IsPinnedToBottom ? null : CaptureAnchor();
        var mutation = _subscribedVm.EnsureMountedTranscriptCoverage(_chatShell.ViewportHeight);
        if (mutation.HasChanges)
            await CompleteTranscriptMutationAsync(anchor, mutation);

        if (_chatShell.IsPinnedToBottom)
            Dispatcher.UIThread.Post(() => _chatShell?.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void OnChatViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_chatShell is null || _isApplyingTranscriptMutation)
            return;

        if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 0.5)
            return;

        if (_chatShell.IsPinnedToBottom)
        {
            Dispatcher.UIThread.Post(() => _chatShell?.ScrollToEnd(), DispatcherPriority.Loaded);
            return;
        }

        _pendingResizeAnchor ??= CaptureAnchor();
        if (_resizeRestoreQueued)
            return;

        _resizeRestoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _resizeRestoreQueued = false;
            var anchor = _pendingResizeAnchor;
            _pendingResizeAnchor = null;
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
                RestoreAnchor(anchor, mutation.Kind == TranscriptWindowMutationKind.Prepend ? "prepend" : "cleanup");

            if (_chatShell is not null && _subscribedVm is not null)
                _subscribedVm.UpdateTranscriptPinnedState(_chatShell.IsPinnedToBottom, _chatShell.CurrentDistanceFromBottom);
        }
        finally
        {
            _isApplyingTranscriptMutation = false;
        }
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

            return new ScrollAnchorState(control.Turn.StableId, point.Value.Y);
        }

        return null;
    }

    private void RestoreAnchor(ScrollAnchorState? anchor, string reason)
    {
        if (anchor is null || _chatShell is null || _transcriptScrollViewer is null)
            return;

        var control = FindRealizedTurnControl(anchor.StableId);
        var point = control?.TranslatePoint(default, _transcriptScrollViewer);
        if (control is null || point is null)
            return;

        var delta = point.Value.Y - anchor.ViewportY;
        if (Math.Abs(delta) < 0.5)
            return;

        var beforeOffset = _chatShell.VerticalOffset;
        _chatShell.ScrollToVerticalOffset(beforeOffset + delta);
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

    private static bool IsLikelyLayoutDrivenOffsetChange(ScrollChangedEventArgs e)
    {
        var extentDeltaY = Math.Abs(e.ExtentDelta.Y);
        if (extentDeltaY < ScrollMutationDeltaThreshold)
            return false;

        var viewportDeltaY = Math.Abs(e.ViewportDelta.Y);
        if (viewportDeltaY > ScrollMutationDeltaThreshold)
            return false;

        return Math.Abs(Math.Abs(e.OffsetDelta.Y) - extentDeltaY) <= LayoutShiftDeltaTolerance;
    }

    private static bool TurnHasStreamingContent(TranscriptTurn turn)
    {
        foreach (var item in turn.Items)
        {
            if (item is AssistantMessageItem { IsStreaming: true }
                or ReasoningItem { IsActive: true }
                or SubagentToolCallItem { IsActive: true })
            {
                return true;
            }
        }

        return false;
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

            using var bitmap = await dataTransfer.TryGetBitmapAsync();
            if (bitmap is null) return;

            Directory.CreateDirectory(ClipboardImagesDir);
            var fileName = $"clipboard-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png";
            var filePath = Path.Combine(ClipboardImagesDir, fileName);
            bitmap.Save(filePath);

            vm.AddAttachment(filePath);
            FocusComposer();
        }
        catch
        {
            // Ignore transient clipboard failures.
        }
    }

    // ── Copy to clipboard (ViewModel raises event, View handles clipboard API) ──

    private async void OnCopyToClipboardRequested(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch { /* ignore */ }
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

        var sb = new System.Text.StringBuilder();
        foreach (var item in turn.Items ?? Enumerable.Empty<TranscriptItem>())
        {
            if (item is AssistantMessageItem assistantMsg)
            {
                var text = assistantMsg.Content;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (sb.Length > 0) sb.Append(Environment.NewLine).Append(Environment.NewLine);
                sb.Append(text);
            }
        }

        if (sb.Length == 0) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(Avalonia.Input.DataTransferItem.CreateText(sb.ToString()));
            await clipboard.SetDataAsync(data);
        }
        catch { /* ignore */ }
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
}
