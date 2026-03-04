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
using Lumi.Localization;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    private StrataChatShell? _chatShell;
    private StrataChatComposer? _activeComposer;
    private StrataChatComposer? _welcomeComposer;
    private Panel? _dropOverlay;

    private ChatViewModel? _subscribedVm;

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
        _welcomeComposer = this.FindControl<StrataChatComposer>("WelcomeComposer");
        _activeComposer = this.FindControl<StrataChatComposer>("ActiveComposer");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(StrataFileAttachment.OpenRequestedEvent, OnFileAttachmentOpenRequested);
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
        var composer = _subscribedVm?.IsChatVisible == true ? _activeComposer : _welcomeComposer;
        composer?.FocusInput();
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
        Dispatcher.UIThread.Post(() =>
        {
            _chatShell?.ResetAutoScroll();
            // Jump instantly to the last message so the chat opens at the bottom
            var count = (_subscribedVm?.TranscriptItems.Count ?? 0);
            if (count > 0)
                _chatShell?.ScrollToIndex(count - 1, ScrollToAlignment.End);
            else
                _chatShell?.ScrollToEnd();
            // Focus after layout + render passes have completed
            Dispatcher.UIThread.Post(FocusComposer, DispatcherPriority.Input);
        }, DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.CurrentChat) && _subscribedVm?.CurrentChat is not null)
            _chatShell?.ResetAutoScroll();
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
}
