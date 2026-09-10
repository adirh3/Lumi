using Avalonia.Controls;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Views;

public partial class GitChangesView : UserControl
{
    public GitChangesView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdatePaneLayout();
        DataContextChanged += (_, _) => UpdatePaneLayout();
    }

    private void UpdatePaneLayout()
    {
        var wide = Bounds.Width >= 720;
        if (DataContext is MobileChatViewModel vm)
        {
            vm.IsGitWideLayout = wide;
            vm.IsGitShortLayout = Bounds.Height < 480;
        }
        var panes = this.FindControl<Grid>("GitPanes")!;
        panes.ColumnDefinitions[0].Width = wide ? new GridLength(280) : new GridLength(1, GridUnitType.Star);
        panes.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(this.FindControl<Grid>("GitDetailPane")!, wide ? 1 : 0);
    }

    private void OnGitFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MobileChatViewModel vm && e.AddedItems.Count > 0
            && e.AddedItems[0] is RemoteGitFile file && !ReferenceEquals(file, vm.SelectedGitFile))
        {
            vm.OpenGitFileCommand.Execute(file);
        }
    }
}
