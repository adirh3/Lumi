using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnIconSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is StrataIconPicker picker && DataContext is ViewModels.SkillsViewModel vm)
            vm.EditIconGlyph = picker.SelectedIcon ?? "⚡";

        this.FindControl<Button>("SkillIconPickerButton")?.Flyout?.Hide();
    }
}
