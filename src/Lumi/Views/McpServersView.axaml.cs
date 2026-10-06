using Avalonia.Controls;
using Avalonia.Input;
using Lumi.ViewModels;
using Lumi.Views.Management;

namespace Lumi.Views;

public partial class McpServersView : UserControl
{
    public McpServersView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("McpSaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "McpDetailScroll", nameof(McpServersViewModel.SelectedServer));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ManagementPage.IsSaveGesture(e)
            && DataContext is McpServersViewModel { IsEditing: true } vm
            && vm.CanSave)
        {
            vm.SaveServerCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is McpServersViewModel page
            && ManagementPage.TryHandleEscape(e, page.IsEditing || page.IsBrowsing, page.IsConfirmingDelete,
                page.IsEditing && (page.HasUnsavedChanges || page.IsNewServer),
                () => page.CancelDeleteCommand.Execute(null), () => page.CloseDetailCommand.Execute(null)))
            return;

        base.OnKeyDown(e);
    }
}
