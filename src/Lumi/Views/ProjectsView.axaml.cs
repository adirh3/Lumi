using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.ViewModels;
using Lumi.Views.Management;
using System.ComponentModel;
using System.Linq;

namespace Lumi.Views;

public partial class ProjectsView : UserControl
{
    private ProjectsViewModel? _viewModel;

    public ProjectsView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("ProjectSaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "ProjectDetailScroll", nameof(ProjectsViewModel.SelectedProject));
        ManagementPage.FocusNameWhenNew(this, "ProjectNameInput", nameof(ProjectsViewModel.IsNewProject),
            static dc => dc is ProjectsViewModel { IsNewProject: true });
        DataContextChanged += (_, _) => WatchViewModel();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void WatchViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ProjectsViewModel;
        if (_viewModel is null)
            return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RevealSandboxIfAsked();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectsViewModel.IsRevealingSandbox))
            RevealSandboxIfAsked();
    }

    /// <summary>A chat opened the project on its sandbox: scroll to it once the page has laid out the project.</summary>
    private void RevealSandboxIfAsked()
    {
        if (_viewModel is not { IsRevealingSandbox: true } viewModel)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!viewModel.IsRevealingSandbox)
                return;
            viewModel.IsRevealingSandbox = false;
            this.FindControl<Control>("ProjectSandboxPanel")?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private async void OnBrowseSandboxFoldersClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = true,
            Title = Loc.Projects_SandboxSelectFoldersTitle
        });

        if (folders.Count > 0 && DataContext is ProjectsViewModel vm)
            vm.AddSandboxFolders(folders.Select(folder => folder.Path.LocalPath));
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectsViewModel { CanSave: false })
            ManagementPage.AskForName(this, "ProjectNameInput");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ManagementPage.IsSaveGesture(e)
            && DataContext is ProjectsViewModel { IsEditing: true } vm)
        {
            ManagementPage.SaveOrAskForName(this, "ProjectNameInput", vm.CanSave, () => vm.SaveProjectCommand.Execute(null));
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
            Title = Loc.Projects_SelectFolderTitle
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
            Title = Loc.Projects_AddContextFoldersTitle
        });

        if (folders.Count > 0 && DataContext is ProjectsViewModel vm)
        {
            vm.AddAdditionalContextDirectories(folders.Select(folder => folder.Path.LocalPath));
        }
    }
}
