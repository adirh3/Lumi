using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Lumi.Localization;
using Lumi.ViewModels;

namespace Lumi.Views.Controls;

public partial class GitHubLoginView : UserControl
{
    private TextBlock? _successText;
    private GitHubLoginViewModel? _wiredVm;

    public GitHubLoginView()
    {
        AvaloniaXamlLoader.Load(this);
        _successText = this.FindControl<TextBlock>("SuccessText");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_wiredVm is not null)
        {
            _wiredVm.PropertyChanged -= OnVmPropertyChanged;
            _wiredVm.CopyToClipboardRequested -= OnCopyToClipboard;
        }

        if (DataContext is GitHubLoginViewModel vm)
        {
            _wiredVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.CopyToClipboardRequested += OnCopyToClipboard;
            UpdateSuccessText(vm);
        }
        else
        {
            _wiredVm = null;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is GitHubLoginViewModel vm && e.PropertyName == nameof(GitHubLoginViewModel.GitHubLogin))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateSuccessText(vm));
    }

    private void UpdateSuccessText(GitHubLoginViewModel vm)
    {
        if (_successText is not null && !string.IsNullOrEmpty(vm.GitHubLogin))
            _successText.Text = string.Format(Loc.Onboarding_SignInSuccess, vm.GitHubLogin);
    }

    private async void OnCopyToClipboard(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
        }
        catch { /* best-effort */ }
    }
}
