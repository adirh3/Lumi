using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using Lumi.Views.Management;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class AgentsView : UserControl
{
    public AgentsView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("LumiSaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "LumiDetailScroll", nameof(AgentsViewModel.SelectedAgent));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ManagementPage.IsSaveGesture(e)
            && DataContext is AgentsViewModel { IsEditing: true } vm
            && vm.CanSave)
        {
            vm.SaveAgentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is AgentsViewModel page
            && ManagementPage.TryHandleEscape(e, page.IsEditing, page.IsConfirmingDelete,
                page.HasUnsavedChanges || page.IsNewAgent,
                () => page.CancelDeleteCommand.Execute(null), () => page.CloseDetailCommand.Execute(null)))
            return;

        base.OnKeyDown(e);
    }

    private void OnIconSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is StrataIconPicker picker && DataContext is AgentsViewModel vm)
            vm.EditIconGlyph = picker.SelectedIcon ?? "✦";

        this.FindControl<Button>("AgentIconPickerButton")?.Flyout?.Hide();
    }
}
