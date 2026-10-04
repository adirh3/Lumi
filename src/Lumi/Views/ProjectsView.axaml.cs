using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Lumi.ViewModels;
using Lumi.Views.Management;
using System.Linq;

namespace Lumi.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("ProjectSaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "ProjectDetailScroll", nameof(ProjectsViewModel.SelectedProject));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ManagementPage.IsSaveGesture(e)
            && DataContext is ProjectsViewModel { IsEditing: true } vm
            && vm.CanSave)
        {
            vm.SaveProjectCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is ProjectsViewModel page
            && ManagementPage.TryHandleEscape(e, page.IsEditing, page.IsConfirmingDelete,
                page.HasUnsavedChanges || page.IsNewProject,
                () => page.CancelDeleteCommand.Execute(null), () => page.CloseDetailCommand.Execute(null)))
            return;

        base.OnKeyDown(e);
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select project folder"
        });

        if (folders.Count > 0 && DataContext is ProjectsViewModel vm)
        {
            vm.EditWorkingDirectory = folders[0].Path.LocalPath;
        }
    }

    private async void OnBrowseAdditionalFoldersClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Add project context folders"
        });

        if (folders.Count > 0 && DataContext is ProjectsViewModel vm)
        {
            vm.AddAdditionalContextDirectories(folders.Select(folder => folder.Path.LocalPath));
        }
    }
}
