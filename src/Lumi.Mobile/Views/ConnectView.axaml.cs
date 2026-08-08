using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Mobile.ViewModels;
using System.ComponentModel;

namespace Lumi.Mobile.Views;

public partial class ConnectView : UserControl
{
    private ConnectViewModel? _viewModel;
    private bool _isAttached;

    public ConnectView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AttachViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_isAttached)
            AttachViewModel();
    }

    private void AttachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as ConnectViewModel;
        if (_viewModel is null)
            return;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        FocusPairingCodeIfNeeded();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectViewModel.Step))
            FocusPairingCodeIfNeeded();
    }

    private void FocusPairingCodeIfNeeded()
    {
        if (_viewModel?.IsCodeStep != true)
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel?.IsCodeStep == true
                    && this.FindControl<TextBox>("PairingCodeBox") is { } textBox)
                {
                    textBox.Focus();
                    textBox.CaretIndex = textBox.Text?.Length ?? 0;
                }
            },
            DispatcherPriority.Input);
    }

    private void OnPairingCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel?.CanSubmitCode != true)
            return;

        e.Handled = true;
        _viewModel.SubmitCodeCommand.Execute(null);
    }

    private void OnManualAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || string.IsNullOrWhiteSpace(_viewModel?.ManualAddress)
            || _viewModel.ConnectManuallyCommand.CanExecute(null) != true)
        {
            return;
        }

        e.Handled = true;
        _viewModel.ConnectManuallyCommand.Execute(null);
    }
}
