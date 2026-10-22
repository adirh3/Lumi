using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using Lumi.Views.Management;

namespace Lumi.Views;

public partial class MemoriesView : UserControl
{
    public MemoriesView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("MemorySaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        if (this.FindControl<TextBlock>("BriefSaveHint") is { } briefHint)
            briefHint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "MemoryDetailScroll", nameof(MemoriesViewModel.SelectedMemory));
        ManagementPage.ResetScrollOn(this, "BriefDetailScroll", nameof(BriefsViewModel.SelectedBrief),
            context => (context as MemoriesViewModel)?.Briefs);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is MemoriesViewModel { Briefs: { IsOpen: true } briefs })
        {
            if (ManagementPage.IsSaveGesture(e) && briefs.HasUnsavedChanges)
            {
                briefs.SaveCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (ManagementPage.TryHandleEscape(e, briefs.IsOpen, briefs.IsConfirmingClear, briefs.HasUnsavedChanges,
                    () => briefs.CancelClearCommand.Execute(null), () => briefs.CloseCommand.Execute(null)))
                return;
        }

        if (ManagementPage.IsSaveGesture(e)
            && DataContext is MemoriesViewModel { IsEditing: true } vm
            && vm.CanSave)
        {
            vm.SaveMemoryCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is MemoriesViewModel page
            && ManagementPage.TryHandleEscape(e, page.IsEditing, page.IsConfirmingDelete,
                page.HasUnsavedChanges || page.IsNewMemory,
                () => page.CancelDeleteCommand.Execute(null), () => page.CloseDetailCommand.Execute(null)))
            return;

        base.OnKeyDown(e);
    }
}
