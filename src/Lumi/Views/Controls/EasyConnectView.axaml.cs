using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;

namespace Lumi.Views.Controls;

public partial class EasyConnectView : UserControl
{
    private EasyConnectViewModel? _viewModel;
    private GitHubLoginViewModel? _login;

    public EasyConnectView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Wire(VisualRoot is not null ? DataContext as EasyConnectViewModel : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wire(DataContext as EasyConnectViewModel);
        _viewModel?.Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Wire(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Wire(EasyConnectViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        WireLogin(_viewModel?.LoginVM);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EasyConnectViewModel.LoginVM))
            WireLogin(_viewModel?.LoginVM);
    }

    private void WireLogin(GitHubLoginViewModel? login)
    {
        if (ReferenceEquals(_login, login))
            return;
        if (_login is not null)
            _login.CopyToClipboardRequested -= OnCopyToClipboard;
        _login = login;
        if (_login is not null)
            _login.CopyToClipboardRequested += OnCopyToClipboard;
    }

    private async void OnCopyToClipboard(string text)
    {
        // Onboarding and Settings can both show this list; only the visible one copies.
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EasyConnect] Clipboard copy failed ({ex.GetType().Name}).");
        }
    }
}
