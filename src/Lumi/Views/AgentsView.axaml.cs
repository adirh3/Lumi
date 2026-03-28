using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class AgentsView : UserControl
{
    public AgentsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnIconSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is StrataIconPicker picker && DataContext is ViewModels.AgentsViewModel vm)
            vm.EditIconGlyph = picker.SelectedIcon ?? "✦";

        this.FindControl<Button>("AgentIconPickerButton")?.Flyout?.Hide();
    }
}
