using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>Hosts the share sheet. File and folder pickers need the window, so they live here.</summary>
public partial class ShareSheetView : UserControl
{
    public ShareSheetView()
    {
        AvaloniaXamlLoader.Load(this);

        // Land keyboard focus on Copy when the sheet opens, so Enter copies and Escape closes.
        if (this.FindControl<StrataDialog>("ShareDialog") is { } dialog)
        {
            dialog.PropertyChanged += (_, e) =>
            {
                if (e.Property == StrataDialog.IsDialogOpenProperty && dialog.IsDialogOpen)
                    Dispatcher.UIThread.Post(() => this.FindControl<Button>("ShareCopyButton")?.Focus(), DispatcherPriority.Loaded);
            };
        }
    }

    private async void OnSaveFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShareSheetViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Share_FilePickerTitle,
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = ".md",
            ShowOverwritePrompt = true,
            FileTypeChoices = new List<FilePickerFileType>
            {
                new(Loc.Capability_FormatPack) { Patterns = ["*" + CapabilityPackWriter.PackExtension, "*.md"] }
            }
        });

        if (file is not null)
            await vm.SaveToFileAsync(file);
    }

    private async void OnSaveFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShareSheetViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Share_FolderPickerTitle,
            AllowMultiple = false
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.SaveSkillToFolder(path);
    }
}
