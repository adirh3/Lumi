using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Services;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

public sealed partial class MobileShellViewModel
{
    private readonly IMobileHostEnvironment _installationHost = MobilePlatformServices.HostEnvironment;
    private bool _installSuggestionShown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanDragDrawer))]
    [NotifyPropertyChangedFor(nameof(CanApplyWebAppUpdate))]
    private bool _isWebAppInstallPromptOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebAppInstallActionText))]
    private bool _isWebAppInstallInstructionsVisible;

    [ObservableProperty] private bool _isInstallingWebApp;
    [ObservableProperty] private string _webAppInstallError = "";

    public bool CanAddWebApp => _installationHost.WebAppInstallMode != WebAppInstallMode.Unavailable;

    public string WebAppInstallActionText => Strings.Get(
        IsWebAppInstallInstructionsVisible ? "Done"
        : _installationHost.WebAppInstallMode == WebAppInstallMode.NativePrompt ? "WebAppInstallAction"
        : "WebAppInstallHow");

    public string WebAppInstallFirstStep => Strings.Get(
        _installationHost.WebAppInstallMode == WebAppInstallMode.Apple
            ? "WebAppInstallShareStep" : "WebAppInstallMenuStep");

    public string WebAppInstallSecondStep => Strings.Get(
        _installationHost.WebAppInstallMode == WebAppInstallMode.Apple
            ? "WebAppInstallAppleStep" : "WebAppInstallBrowserStep");

    public void NotifyWebAppInstallationChanged()
    {
        OnPropertyChanged(nameof(CanAddWebApp));
        OnPropertyChanged(nameof(WebAppInstallActionText));
        OnPropertyChanged(nameof(WebAppInstallFirstStep));
        OnPropertyChanged(nameof(WebAppInstallSecondStep));
        OpenWebAppInstallCommand.NotifyCanExecuteChanged();
        if (!CanAddWebApp)
            IsWebAppInstallPromptOpen = false;
    }

    internal void OfferPhoneSetupIfUseful()
    {
        NotifyWebAppInstallationChanged();
        // One suggestion per run: do not replace a dismissed install offer with a notification offer.
        if (_installSuggestionShown)
            return;
        if (CanAddWebApp
            && !_settings.WebAppInstallPromptAnswered
            && IsPaired && IsConnected && _isApplicationActive
            && !IsModalSheetPresented && !IsNotificationsPromptOpen
            && !IsWebAppInstallPromptOpen && !IsWebAppUpdateConfirmationOpen
            && !IsProjectPickerOpen && !IsChatActionsOpen && !IsDisconnectConfirmationOpen
            && !Chat.HasOpenSheet && !Library.HasOpenSurface)
        {
            _installSuggestionShown = true;
            OpenWebAppInstall();
            return;
        }

        if (!IsWebAppInstallPromptOpen)
            OfferNotificationsIfUseful();
    }

    [RelayCommand(CanExecute = nameof(CanAddWebApp))]
    private void OpenWebAppInstall()
    {
        if (!CanAddWebApp)
            return;
        WebAppInstallError = "";
        IsWebAppInstallInstructionsVisible = false;
        IsWebAppInstallPromptOpen = true;
    }

    [RelayCommand]
    private async Task InstallWebAppAsync()
    {
        if (!CanAddWebApp)
        {
            NotifyWebAppInstallationChanged();
            return;
        }
        if (IsWebAppInstallInstructionsVisible)
        {
            IsWebAppInstallPromptOpen = false;
            return;
        }
        if (_installationHost.WebAppInstallMode != WebAppInstallMode.NativePrompt)
        {
            IsWebAppInstallInstructionsVisible = true;
            return;
        }

        try
        {
            // Start the browser prompt in this tap's call stack, before yielding to anything else.
            var installation = _installationHost.InstallWebAppAsync();
            IsInstallingWebApp = true;
            var result = await installation;
            if (result is WebAppInstallResult.Accepted or WebAppInstallResult.Dismissed)
                IsWebAppInstallPromptOpen = false;
            else
                IsWebAppInstallInstructionsVisible = CanAddWebApp;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Could not open the web app installation prompt: {ex}");
            WebAppInstallError = Strings.Get("WebAppInstallFailed");
            IsWebAppInstallInstructionsVisible = true;
        }
        finally
        {
            IsInstallingWebApp = false;
            NotifyWebAppInstallationChanged();
        }
    }

    [RelayCommand]
    private void DismissWebAppInstall() => IsWebAppInstallPromptOpen = false;

    partial void OnIsWebAppInstallPromptOpenChanged(bool value)
    {
        NotifyWebAppUpdateSafetyChanged();
        if (!value && !_settings.WebAppInstallPromptAnswered)
        {
            _settings.WebAppInstallPromptAnswered = true;
            _store.Save(_settings);
        }
    }
}
