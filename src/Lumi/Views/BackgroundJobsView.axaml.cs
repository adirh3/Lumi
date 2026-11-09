using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using Lumi.Views.Management;

namespace Lumi.Views;

public partial class BackgroundJobsView : UserControl
{
    private BackgroundJobsViewModel? _viewModel;
    private Control? _editor;

    public BackgroundJobsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as BackgroundJobsViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelChanged;
            EnsureEditor();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackgroundJobsViewModel.IsEditing))
            EnsureEditor();
    }

    private void EnsureEditor()
    {
        if (_viewModel is not { IsEditing: true } || _editor is not null)
            return;

        var host = this.FindControl<Panel>("JobDetail")!;
        var template = (IControlTemplate)host.Resources["JobEditorTemplate"]!;
        var built = template.Build(this)!;
        _editor = built.Result;
        NameScope.SetNameScope(_editor, built.NameScope);
        _editor.FindControl<TextBlock>("JobSaveHint")!.Text = ManagementPage.SaveHint;
        ManagementPage.ResetScrollOn(_editor, "JobDetailScroll", nameof(BackgroundJobsViewModel.SelectedJob));
        host.Children.Add(_editor);
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
