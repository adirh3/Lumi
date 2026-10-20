using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.ViewModels;

namespace Lumi.Views.Controls;

public partial class EasyConnectDialogView : UserControl
{
    private EasyConnectViewModel? _viewModel;

    public EasyConnectDialogView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as EasyConnectViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_viewModel is null && DataContext is EasyConnectViewModel viewModel)
        {
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { IsDialogOpen: true } viewModel)
            return;
        // Put the caret where the next step expects input, so keyboard users never hunt for it.
        var target = e.PropertyName switch
        {
            nameof(EasyConnectViewModel.IsDialogOpen) or nameof(EasyConnectViewModel.Stage) when viewModel.IsGallery => "ProviderSearch",
            nameof(EasyConnectViewModel.Stage) when viewModel.IsAccountStage && viewModel.IsFoundry
                && string.IsNullOrWhiteSpace(viewModel.BaseUrl) => "ConnectBaseUrl",
            nameof(EasyConnectViewModel.ShowKeyEntry) when viewModel.ShowKeyEntry && viewModel.IsAccountStage => "ConnectApiKey",
            nameof(EasyConnectViewModel.Stage) when viewModel.IsAccountStage && viewModel.ShowKeyEntry => "ConnectApiKey",
            _ => null
        };
        if (target is null)
            return;
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>(target)?.Focus(), DispatcherPriority.Loaded);
    }
}
