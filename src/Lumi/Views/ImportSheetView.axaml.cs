using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.ViewModels;
using StrataTheme.Animation;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>Hosts the import sheet: file picking, drag and drop, and the stage transitions.</summary>
public partial class ImportSheetView : UserControl
{
    private ImportSheetViewModel? _viewModel;

    public ImportSheetView()
    {
        AvaloniaXamlLoader.Load(this);

        if (this.FindControl<Panel>("ImportSheetRoot") is { } root)
        {
            root.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            root.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            root.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            root.AddHandler(DragDrop.DropEvent, OnDrop);
        }

        // Paste is the fastest path in, so it gets focus when the sheet opens (Escape still closes).
        if (this.FindControl<StrataDialog>("ImportDialog") is { } dialog)
        {
            dialog.PropertyChanged += (_, e) =>
            {
                if (e.Property == StrataDialog.IsDialogOpenProperty && dialog.IsDialogOpen)
                    Dispatcher.UIThread.Post(() => this.FindControl<Button>("ImportPasteButton")?.Focus(), DispatcherPriority.Loaded);
            };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as ImportSheetViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        base.OnDataContextChanged(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImportSheetViewModel.Stage) || _viewModel is not { } viewModel)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var stageName = viewModel.Stage switch
            {
                ImportSheetStage.Review => "ImportReviewStage",
                ImportSheetStage.Done => "ImportDoneStage",
                _ => "ImportPickStage"
            };

            if (this.FindControl<Control>(stageName) is { } stage)
                SlideFadeEntrance.Play(stage, offsetY: 10);

            this.FindControl<Border>("ImportDoneBadge")?.Classes.Set("celebrate", viewModel.Stage == ImportSheetStage.Done);

            // Keep focus inside the sheet without arming a button: Enter never imports by accident.
            if (viewModel.Stage != ImportSheetStage.Pick)
                this.FindControl<StrataDialog>("ImportDialog")?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void OnChooseFileClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Import_FilePickerTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Import_FileTypeName) { Patterns = ["*.md", "*.json", "*.txt"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0)
            await _viewModel.LoadStorageItemAsync(files[0]);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var accepted = CapabilityDropSupport.CanAccept(e) && _viewModel is { IsDone: false };
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (_viewModel is not null)
            _viewModel.IsDropTargetActive = accepted;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = CapabilityDropSupport.CanAccept(e) && _viewModel is { IsDone: false }
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_viewModel is not null && sender is Control target && !CapabilityDropSupport.IsInside(target, e))
            _viewModel.IsDropTargetActive = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is not { IsDone: false } viewModel)
            return;

        viewModel.IsDropTargetActive = false;
        e.Handled = true;
        if (CapabilityDropSupport.GetItem(e) is { } item)
            await viewModel.LoadStorageItemAsync(item);
        else if (CapabilityDropSupport.GetText(e) is { } text)
            viewModel.LoadText(text, Loc.Import_FromDrop);
    }
}

/// <summary>Shared rules for dropping a capability file, skill folder or text onto Lumi.</summary>
internal static class CapabilityDropSupport
{
    public static bool CanAccept(DragEventArgs e)
        => e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text);

    public static IStorageItem? GetItem(DragEventArgs e)
        => e.DataTransfer.Contains(DataFormat.File) ? e.DataTransfer.TryGetFile() : null;

    public static string? GetText(DragEventArgs e)
        => e.DataTransfer.Contains(DataFormat.Text) && e.DataTransfer.TryGetText() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    /// <summary>
    /// Drag-leave also fires when the pointer moves between children of the target; only a leave that
    /// actually exits the target's bounds should clear the drop highlight.
    /// </summary>
    public static bool IsInside(Control target, DragEventArgs e)
    {
        var position = e.GetPosition(target);
        return position.X >= 0 && position.Y >= 0 && position.X < target.Bounds.Width && position.Y < target.Bounds.Height;
    }
}
