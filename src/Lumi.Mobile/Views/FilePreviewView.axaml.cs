using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Views;

/// <summary>
/// The preview pane's content: the file and its actions. The shell decides where the pane sits —
/// over the chat on one pane, beside it on two — and tells this view which, and what to keep clear of.
/// </summary>
public partial class FilePreviewView : UserControl
{
    /// <summary>Beside the chat the pane closes with ✕ at its corner; over it, Back leads to the chat.</summary>
    public static readonly StyledProperty<bool> IsBesideChatProperty =
        AvaloniaProperty.Register<FilePreviewView, bool>(nameof(IsBesideChat));

    /// <summary>The system bars, cutouts and gesture area this pane must keep its controls clear of.</summary>
    public static readonly StyledProperty<Thickness> InsetsProperty =
        AvaloniaProperty.Register<FilePreviewView, Thickness>(nameof(Insets));

    /// <summary>The floating actions' height and gap, so the end of a file scrolls clear of them.</summary>
    private const double ActionsClearance = 96;

    private static readonly TimeSpan ActionMessageDuration = TimeSpan.FromSeconds(4);

    private FilePreviewViewModel? _preview;
    private IDisposable? _messageTimer;

    public FilePreviewView()
    {
        InitializeComponent();
        ApplyChrome();
    }

    public bool IsBesideChat
    {
        get => GetValue(IsBesideChatProperty);
        set => SetValue(IsBesideChatProperty, value);
    }

    public Thickness Insets
    {
        get => GetValue(InsetsProperty);
        set => SetValue(InsetsProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsBesideChatProperty || change.Property == InsetsProperty)
            ApplyChrome();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_preview is not null)
            _preview.PropertyChanged -= OnPreviewPropertyChanged;
        _messageTimer?.Dispose();
        _messageTimer = null;
        _preview = DataContext as FilePreviewViewModel;
        if (_preview is not null)
            _preview.PropertyChanged += OnPreviewPropertyChanged;
        ResetScrollPositions();
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilePreviewViewModel.ActionMessage)
            || sender is not FilePreviewViewModel { ActionMessage: { Length: > 0 } message } preview)
            return;

        // A word about an action stays a moment, then gets out of the file's way.
        _messageTimer?.Dispose();
        _messageTimer = DispatcherTimer.RunOnce(() =>
        {
            if (preview.ActionMessage == message)
                preview.ActionMessage = null;
        }, ActionMessageDuration);
    }

    private void ApplyChrome()
    {
        var insets = Insets;
        this.FindControl<Button>("PreviewBackButton")!.IsVisible = !IsBesideChat;
        this.FindControl<Button>("PreviewCloseButton")!.IsVisible = IsBesideChat;
        this.FindControl<Border>("PreviewHeader")!.Padding =
            new Thickness(12 + insets.Left, 10 + insets.Top, 12 + insets.Right, 10);

        var bottom = ActionsClearance + insets.Bottom;
        this.FindControl<StackPanel>("PreviewMarkdownContent")!.Margin =
            new Thickness(20 + insets.Left, 14, 20 + insets.Right, bottom);
        this.FindControl<StackPanel>("PreviewCodeContent")!.Margin =
            new Thickness(16 + insets.Left, 14, 24 + insets.Right, bottom);
        this.FindControl<StackPanel>("PreviewTextContent")!.Margin =
            new Thickness(20 + insets.Left, 16, 20 + insets.Right, bottom);
        this.FindControl<ItemsControl>("PreviewCsvRows")!.Margin = new Thickness(0, 0, 0, bottom);
        this.FindControl<DockPanel>("PreviewCsvTable")!.Margin = new Thickness(insets.Left, 0, insets.Right, 0);
        this.FindControl<ItemsControl>("PreviewPdfPages")!.Margin =
            new Thickness(16 + insets.Left, 16, 16 + insets.Right, bottom);
        this.FindControl<StackPanel>("PreviewActionsHost")!.Margin =
            new Thickness(12 + insets.Left, 0, 12 + insets.Right, 16 + insets.Bottom);
    }

    /// <summary>A different file starts at its top.</summary>
    private void ResetScrollPositions()
    {
        foreach (var name in new[]
                 {
                     "PreviewMarkdownScroll", "PreviewCodeScroll", "PreviewTextScroll", "PreviewCsvScroll",
                     "PreviewCsvRowsScroll", "PreviewPdfScroll"
                 })
        {
            if (this.FindControl<ScrollViewer>(name) is { } scroll)
                scroll.Offset = default;
        }
    }

    private MobileShellViewModel? FindShell() =>
        this.FindAncestorOfType<MobileShellView>()?.DataContext as MobileShellViewModel;

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        FindShell()?.Chat.CloseFilePreviewCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Opens the file in another app, or keeps a copy when no app on the device opens it.</summary>
    private void OnOpenClick(object? sender, RoutedEventArgs e) =>
        _ = RunFileActionAsync("OpenFileFailed", async (preview, path, topLevel) =>
        {
            if (await MobilePlatformServices.ProducedFileOpener.TryOpenAsync(
                    path, preview.FileName, CancellationToken.None))
            {
                return;
            }

            await SaveCopyAsync(preview, path, topLevel, openAfterSaving: true);
        });

    private void OnShareClick(object? sender, RoutedEventArgs e) =>
        _ = RunFileActionAsync("FilePreviewShareFailed", async (preview, path, _) =>
        {
            if (!await MobilePlatformServices.ProducedFileOpener.TryShareAsync(
                    path, preview.FileName, CancellationToken.None))
            {
                preview.ActionMessage = Strings.Get("FilePreviewShareFailed");
            }
        });

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        _ = RunFileActionAsync("FilePreviewSaveFailed", (preview, path, topLevel) =>
            SaveCopyAsync(preview, path, topLevel, openAfterSaving: false));

    /// <summary>
    /// Runs an action on the file on this device, fetching it first if the preview had no reason to.
    /// A web app holds its reload until the action is done, as it always has for opening a file.
    /// </summary>
    private async Task RunFileActionAsync(
        string failureKey,
        Func<FilePreviewViewModel, string, TopLevel, Task> action)
    {
        if (DataContext is not FilePreviewViewModel { IsActionRunning: false, IsClosed: false } preview
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var shell = FindShell();
        preview.IsActionRunning = true;
        preview.ActionMessage = null;
        shell?.BeginPendingRemoteAction();
        try
        {
            var file = await preview.EnsureDownloadedAsync();
            if (preview.IsClosed)
                return;
            if (file.Path is not { } path)
            {
                preview.ActionMessage = file.Error ?? Strings.Get("DownloadFileFailed");
                return;
            }

            await action(preview, path, topLevel);
        }
        catch (OperationCanceledException) when (preview.IsClosed)
        {
        }
        catch (Exception ex)
        {
            if (!preview.IsClosed)
                preview.ActionMessage = Strings.Get(failureKey);
            Trace.TraceWarning($"[Mobile] File preview action failed for {preview.FileName}: {ex}");
        }
        finally
        {
            preview.IsActionRunning = false;
            shell?.EndPendingRemoteAction();
        }
    }

    private static async Task SaveCopyAsync(
        FilePreviewViewModel preview, string path, TopLevel topLevel, bool openAfterSaving)
    {
        using var destination = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = Strings.Get("SaveLumiFile"),
                SuggestedFileName = preview.FileName
            });
        if (destination is null)
            return;

        await ProducedFileExport.CopyAndVerifyAsync(
            path,
            destination.OpenWriteAsync,
            destination.OpenReadAsync,
            CancellationToken.None);

        if (!openAfterSaving)
            preview.ActionMessage = Strings.Get("FilePreviewSaved");
        else if (!await topLevel.Launcher.LaunchFileAsync(destination))
            preview.ActionMessage = Strings.Get("DownloadedFileCannotOpen");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _messageTimer?.Dispose();
        _messageTimer = null;
        base.OnDetachedFromVisualTree(e);
    }
}
