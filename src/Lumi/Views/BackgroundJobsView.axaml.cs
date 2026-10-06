using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.Models;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class BackgroundJobsView : UserControl
{
    public BackgroundJobsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnIconSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is StrataIconPicker picker && DataContext is BackgroundJobsViewModel vm)
            vm.EditIconGlyph = picker.SelectedIcon ?? BackgroundJob.DefaultIconGlyph;

        this.FindControl<Button>("JobIconPickerButton")?.Flyout?.Hide();
    }
}
