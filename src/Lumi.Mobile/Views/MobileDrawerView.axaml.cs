using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

public partial class MobileDrawerView : UserControl
{
    public MobileDrawerView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Long-press a chat row to open its action sheet. This is the touch equivalent of the
    /// right-click menu the row used to carry: a phone has no secondary button, so holding is the
    /// only gesture that can mean "tell me more about this thing" without stealing the plain tap.
    /// </summary>
    private void OnChatRowHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started
            || sender is not Control control
            || TouchScrollClickGuard.WasDragged(control))
        {
            return;
        }

        if (control.DataContext is not ChatListItemViewModel chat)
            return;

        if (DataContext is MobileShellViewModel shell)
            shell.OpenChatActionsCommand.Execute(chat);

        e.Handled = true;
    }
}
