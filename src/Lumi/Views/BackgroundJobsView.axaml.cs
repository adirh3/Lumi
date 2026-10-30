using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using Lumi.Views.Management;

namespace Lumi.Views;

public partial class BackgroundJobsView : UserControl
{
    public BackgroundJobsView()
    {
        InitializeComponent();
        if (this.FindControl<TextBlock>("JobSaveHint") is { } hint)
            hint.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(this, "JobDetailScroll", nameof(BackgroundJobsViewModel.SelectedJob));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Saving re-anchors a job's schedule, so the shortcut saves only edits, as the save bar does.
        if (ManagementPage.IsSaveGesture(e) && DataContext is BackgroundJobsViewModel { HasUnsavedWork: true } vm)
        {
            vm.SaveJobCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (DataContext is BackgroundJobsViewModel page
            && ManagementPage.TryHandleEscape(e, page.IsEditing, page.IsConfirmingDelete, page.HasUnsavedWork,
                () => page.CancelDeleteCommand.Execute(null), () => page.CloseDetailCommand.Execute(null)))
            return;

        base.OnKeyDown(e);
    }
}
