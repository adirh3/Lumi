using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        var browseBtn = this.FindControl<Button>("BrowseFolderButton");
        if (browseBtn is not null)
            browseBtn.Click += OnBrowseFolderClick;
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
}
