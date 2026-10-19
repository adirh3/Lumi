using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Lumi.Mobile.Behaviors;
using Lumi.Mobile.Layout;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _viewModel;
    private MobileShellViewModel? _shell;
    private MobileShellView? _shellView;
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
        AttachShell();
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

    /// <summary>
    /// The page's DataContext is the Library, so the pane geometry comes from the shell that hosts it:
    /// how many panes fit and how wide the list column is.
    /// </summary>
    private void AttachShell()
    {
        var view = this.FindLogicalAncestorOfType<MobileShellView>();
        if (!ReferenceEquals(view, _shellView))
        {
            if (_shellView is not null)
                _shellView.DataContextChanged -= OnShellDataContextChanged;
            _shellView = view;
            if (_shellView is not null)
                _shellView.DataContextChanged += OnShellDataContextChanged;
        }

        SetShell(_shellView?.DataContext as MobileShellViewModel);
    }

    private void OnShellDataContextChanged(object? sender, EventArgs e) =>
        SetShell(_shellView?.DataContext as MobileShellViewModel);

    private void SetShell(MobileShellViewModel? shell)
    {
        if (ReferenceEquals(shell, _shell))
        {
            ApplyPaneLayout();
            return;
        }

        if (_shell is not null)
            _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = shell;
        if (_shell is not null)
            _shell.PropertyChanged += OnShellPropertyChanged;
        ApplyPaneLayout();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MobileShellViewModel.IsTwoPane)
            or nameof(MobileShellViewModel.ListPaneWidth))
            ApplyPaneLayout();
    }

    private void ApplyPaneLayout()
    {
        if (this.FindControl<ListDetailPanel>("LibraryPanes") is not { } panes)
            return;

        panes.IsTwoPane = _shell?.IsTwoPane ?? false;
        if (_shell is not null)
            panes.ListWidth = _shell.ListPaneWidth;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        if (_shell is not null)
            _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = null;
        if (_shellView is not null)
            _shellView.DataContextChanged -= OnShellDataContextChanged;
        _shellView = null;
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
                // Beside the editor the list never left, so only a phone replays its entrance.
                if (_shell?.IsTwoPane != true)
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

    /// <summary>A secondary click asks for the same actions as a long press.</summary>
    private void OnLibraryRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: LibraryEntryViewModel { HasActions: true } entry }
            && DataContext is LibraryViewModel { IsEditing: false, IsRowActionsOpen: false } library)
        {
            library.OpenRowActionsCommand.Execute(entry);
        }

        e.Handled = true;
    }
}
