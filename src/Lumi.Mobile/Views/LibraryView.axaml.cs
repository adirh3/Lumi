using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _viewModel;
    private MobileEntrance? _listEntrance;
    private MobileEntrance? _editorEntrance;
    private bool _attached;

    public LibraryView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        AttachViewModel();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached)
            AttachViewModel();
    }

    private void AttachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as LibraryViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        _listEntrance?.Dispose();
        _listEntrance = null;
        _editorEntrance?.Dispose();
        _editorEntrance = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsEffectivelyVisible || _viewModel is null)
            return;

        if (e.PropertyName == nameof(LibraryViewModel.IsEditing))
        {
            if (_viewModel.IsEditing && this.FindControl<StackPanel>("LibraryEditor") is { } editor)
            {
                _listEntrance?.Stop();
                // Native inputs are positioned outside Avalonia's animated visual tree.
                _editorEntrance ??= new MobileEntrance(editor, offsetY: 0);
                _editorEntrance.Play();
            }
            else
            {
                _editorEntrance?.Stop();
                PlayListEntrance();
            }
        }
        else if (e.PropertyName == nameof(LibraryViewModel.SectionIndex) && !_viewModel.IsEditing)
            PlayListEntrance();
    }

    private void PlayListEntrance()
    {
        if (this.FindControl<StackPanel>("LibraryListContent") is { } list)
        {
            _listEntrance ??= new MobileEntrance(list);
            _listEntrance.Play();
        }
    }

    /// <summary>Long-press is a shortcut to the same sheet as the visible row action button.</summary>
    private void OnLibraryRowHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started
            || sender is not Control control
            || TouchScrollClickGuard.WasDragged(control))
        {
            return;
        }

        if (control.DataContext is not LibraryEntryViewModel { HasActions: true } entry
            || DataContext is not LibraryViewModel library
            || library.IsEditing)
            return;

        library.OpenRowActionsCommand.Execute(entry);
        e.Handled = library.IsRowActionsOpen;
    }
}
