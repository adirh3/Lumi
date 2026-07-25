using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();

        LibraryList.DoubleTapped += OnRowDoubleTapped;
    }

    /// <summary>Selection is the ListBox's job; a double tap is the shortcut that opens the artifact.</summary>
    private static void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;

        var row = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (row?.DataContext is not LibraryItemViewModel item)
            return;

        item.OpenCommand.Execute(null);
        e.Handled = true;
    }
}
