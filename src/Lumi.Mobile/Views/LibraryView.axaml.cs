using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Long-press a library row for its actions — the touch stand-in for a right click.</summary>
    private void OnLibraryRowHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started
            || sender is not Control control
            || TouchScrollClickGuard.WasDragged(control))
        {
            return;
        }

        if (control.DataContext is not LibraryEntryViewModel entry)
            return;

        if (DataContext is LibraryViewModel library)
            library.OpenRowActionsCommand.Execute(entry);

        e.Handled = true;
    }
}
