using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;

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
    private ItemsControl? _list;

    public GitChangesView()
    {
        InitializeComponent();
        _scroller = this.FindControl<ScrollViewer>("GitChangesScroller");
        _list = this.FindControl<ItemsControl>("GitChangesList");
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

    /// <summary>
    /// Scrolls just enough to show the selected file — after stepping through diffs with previous/next,
    /// the file the reader ended on may be far from where they left the list.
    /// </summary>
    public void RevealSelected()
    {
        if (_list is null || DataContext is not GitChangesViewModel changes)
            return;

        for (var i = 0; i < changes.Rows.Count; i++)
        {
            if (changes.Rows[i] is GitFileChangeViewModel { IsSelected: true })
            {
                _list.ScrollIntoView(i);
                return;
            }
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
