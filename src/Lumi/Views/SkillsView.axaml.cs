using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using Lumi.Views.Management;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("SkillSaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "SkillDetailScroll", nameof(SkillsViewModel.SelectedSkill));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ManagementPage.IsSaveGesture(e)
            && DataContext is SkillsViewModel { IsEditing: true } vm
            && vm.CanSave)
        {
            vm.SaveSkillCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is SkillsViewModel page
            && ManagementPage.TryHandleEscape(e, page.IsEditing, page.IsConfirmingDelete,
                page.HasUnsavedChanges || page.IsNewSkill,
                () => page.CancelDeleteCommand.Execute(null), () => page.CloseDetailCommand.Execute(null)))
            return;

        base.OnKeyDown(e);
    }

    private void OnIconSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is StrataIconPicker picker && DataContext is SkillsViewModel vm)
            vm.EditIconGlyph = picker.SelectedIcon ?? "⚡";

        this.FindControl<Button>("SkillIconPickerButton")?.Flyout?.Hide();
    }
}
