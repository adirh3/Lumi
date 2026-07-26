using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Views;

/// <summary>
/// The git "file changes" island content: a grouped, filterable index of the working-tree changes
/// (project repository plus every submodule that has changes). Selecting a row raises the view
/// model's <see cref="ViewModels.GitChangesViewModel.FileActivated"/> callback; the hosting panel
/// swaps in the diff and can restore this view — including its scroll offset — on the way back.
/// </summary>
public partial class GitChangesView : UserControl
{
    private ScrollViewer? _scroller;

    public GitChangesView()
    {
        InitializeComponent();
        _scroller = this.FindControl<ScrollViewer>("GitChangesScroller");
    }

    /// <summary>Vertical scroll offset, so drilling into a file and coming back keeps the position.</summary>
    public double ScrollOffset
    {
        get => _scroller?.Offset.Y ?? 0;
        set
        {
            if (_scroller is not null)
                _scroller.Offset = _scroller.Offset.WithY(value);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
