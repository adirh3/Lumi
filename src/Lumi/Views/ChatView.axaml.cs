using System;
using System.ComponentModel;
using System.IO;
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
    private StrataChatShell? _chatShell;
    private StrataChatTranscript? _transcript;
    private StrataChatComposer? _composer;
    private Panel? _composerSpacer;
    private Panel? _dropOverlay;

    private ChatViewModel? _subscribedVm;
    private Border? _worktreeHighlight;
    private Button? _localToggleBtn;
    private Button? _worktreeToggleBtn;

    private static readonly string ClipboardImagesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lumi", "clipboard-images");

    public ChatView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _transcript = this.FindControl<StrataChatTranscript>("Transcript");
        _composer = this.FindControl<StrataChatComposer>("Composer");
        _composerSpacer = this.FindControl<Panel>("ComposerSpacer");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");

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
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
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

    // ── Scroll management ────────────────────────────────

    private void OnScrollToEndRequested() => _chatShell?.ScrollToEnd();

    private void OnUserMessageSent()
    {
        _chatShell?.ResetAutoScroll();
        _chatShell?.ScrollToEnd();
        Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private void OnTranscriptRebuilt()
    {
        _chatShell?.ResetAutoScroll();
        var count = _subscribedVm?.TranscriptTurns.Count ?? 0;
        if (count > 0)
            _transcript?.PrepareScrollToIndex(count - 1, ScrollToAlignment.End);

        Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat) && _subscribedVm?.CurrentChat is not null)
            _chatShell?.ResetAutoScroll();

        if (e.PropertyName == nameof(ChatViewModel.IsWorktreeMode))
            UpdateWorktreeToggleHighlight();
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
        foreach (var item in turn.Items)
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
