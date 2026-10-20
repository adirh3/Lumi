using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services.Remote;

namespace Lumi.ViewModels;

public sealed record RemotePairedDeviceItem(
    string DeviceId,
    string DeviceName,
    string LastSeenText);

internal enum MobileSetupKind
{
    Web,
    Android
}

/// <summary>
/// Settings for the mobile companion. Kept in its own partial so the phone feature adds no noise
/// to the main settings view model.
/// </summary>
public partial class SettingsViewModel
{
    private LumiRemoteServer? _remoteServer;
    private IDisposable? _remotePairingExpiryRegistration;
    private bool _attachingRemoteServer;
    private MobileSetupKind _activeMobileSetupKind;
    private bool _mobileSetupNeedsPairing;
    private string? _openedDevTunnelSignInCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMobileExperienceSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDevTunnelSetupVisible))]
    private bool _remoteAccessEnabled;
    [ObservableProperty] private bool _useLocalNetworkForMobile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMobileExperienceSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDevTunnelSetupVisible))]
    [NotifyPropertyChangedFor(nameof(MobileExperienceTitle))]
    private bool _useDevTunnelForMobile;
    [ObservableProperty] private string _devTunnelStatusText = "";
    [ObservableProperty] private string _devTunnelSetupTitle = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevTunnelErrorDetails))]
    private string _devTunnelErrorDetails = "";
    [ObservableProperty] private bool _isDevTunnelSetupBusy;
    [ObservableProperty] private bool _isDevTunnelInstallDialogOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevTunnelSignInCode))]
    private string _devTunnelSignInCode = "";
    [ObservableProperty] private string _devTunnelSignInUrl = "";
    [ObservableProperty] private string _devTunnelBrowserMessage = "";
    [ObservableProperty] private string _remotePairingCode = "";
    [ObservableProperty] private bool _isRemotePairing;
    [ObservableProperty] private string _remotePairActionText = Loc.Get("Remote_PairButton");
    [ObservableProperty] private string _remoteStatusText = "";
    [ObservableProperty] private string _remoteDevicesText = "";
    [ObservableProperty] private bool _canManageRemoteSecurity = true;
    [ObservableProperty] private bool _isMobileSetupActive;
    [ObservableProperty] private bool _isMobileSetupChoiceEnabled;
    [ObservableProperty] private bool _isMobileWebSetup;
    [ObservableProperty] private bool _isMobileAndroidSetup;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMobileExperienceSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsDevTunnelSetupVisible))]
    private bool _isMobileSetupReady;
    [ObservableProperty] private bool _isMobileTailscaleAvailable;
    [ObservableProperty] private string _mobileTransportDescription = "";
    [ObservableProperty] private string _mobileSetupTitle = "";
    [ObservableProperty] private string _mobileSetupDescription = "";
    [ObservableProperty] private string _mobileSetupConnectionText = "";
    [ObservableProperty] private string _mobileSetupInstructions = "";
    [ObservableProperty] private string _mobileSetupUrl = "";
    [ObservableProperty] private string _mobileSetupQrValue = "";

    public ObservableCollection<RemotePairedDeviceItem> RemoteDevices { get; } = [];

    public bool IsMobileTailscaleSelected =>
        IsMobileTailscaleAvailable && !UseLocalNetworkForMobile && !UseDevTunnelForMobile;

    public bool IsMobileLocalNetworkSelected => UseLocalNetworkForMobile && !UseDevTunnelForMobile;

    public bool IsMobileAndroidSetupChoiceEnabled => IsMobileSetupChoiceEnabled && !UseDevTunnelForMobile;

    public string MobileExperienceDescription => UseDevTunnelForMobile ? "" : Loc.Get("SettingDesc_MobileChoose");
    public string MobileExperienceTitle => Loc.Get(
        UseDevTunnelForMobile ? "Remote_DevTunnelPhoneTitle" : "Setting_MobileChoose");
    public bool IsMobileExperienceSectionVisible =>
        RemoteAccessEnabled && (!UseDevTunnelForMobile || IsMobileSetupReady);
    public bool IsDevTunnelSetupVisible =>
        RemoteAccessEnabled && UseDevTunnelForMobile && !IsMobileSetupReady;
    public bool HasDevTunnelErrorDetails => DevTunnelErrorDetails.Length > 0;

    private MobileOnboardingTransport SelectedMobileTransport => UseDevTunnelForMobile
        ? MobileOnboardingTransport.DevTunnel
        : UseLocalNetworkForMobile ? MobileOnboardingTransport.LocalNetwork : MobileOnboardingTransport.Tailscale;

    public bool CanRetryMobileDevTunnel =>
        RemoteAccessEnabled && UseDevTunnelForMobile
        && _remoteServer is { RequiresDevTunnelSignIn: false } server
        && (!server.IsDevTunnelStarting || server.IsDevTunnelReconnecting);

    public bool CanSignInMobileDevTunnel =>
        RemoteAccessEnabled && UseDevTunnelForMobile
        && _remoteServer is { IsRunning: true, IsDevTunnelStarting: false, RequiresDevTunnelSignIn: true };

    public bool CanCancelMobileDevTunnelSetup =>
        _remoteServer is { IsDevTunnelStarting: true, RequiresDevTunnelInstallConfirmation: false };

    public bool HasDevTunnelSignInCode => DevTunnelSignInCode.Length > 0;

    public bool CanUseDevTunnelBrowserSignIn =>
        RemoteAccessEnabled && UseDevTunnelForMobile && _remoteServer is { IsRunning: true } server
        && (server.RequiresDevTunnelSignIn && !server.IsDevTunnelStarting
            || server.IsDevTunnelSigningIn && server.DevTunnelSignInCode is null);

    public string DevTunnelRetryActionText => Loc.Get(
        _remoteServer?.DevTunnelOrigin is not null ? "Remote_DevTunnelReconnect" : "Remote_DevTunnelRetry");

    internal void AttachRemoteServer(LumiRemoteServer server)
    {
        if (_remoteServer is { } previous)
            previous.StateChanged -= OnRemoteServerStateChanged;

        _remotePairingExpiryRegistration?.Dispose();
        _remotePairingExpiryRegistration = null;
        _remoteServer = server;
        // App startup owns the guarded listener start. Reflect persisted state without letting the
        // generated setter callback start the server before that guard.
        _attachingRemoteServer = true;
        try
        {
            RemoteAccessEnabled = _dataStore.Data.Settings.RemoteAccessEnabled;
            UseLocalNetworkForMobile = _dataStore.Data.Settings.RemoteAllowInsecureLan;
            UseDevTunnelForMobile = _dataStore.Data.Settings.RemoteUseDevTunnel;
        }
        finally
        {
            _attachingRemoteServer = false;
        }
        server.StateChanged += OnRemoteServerStateChanged;
        RefreshRemoteState();
    }

    private void OnRemoteServerStateChanged()
    {
        if (_remoteServer is not { } source)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshRemoteState();
        else
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(_remoteServer, source))
                        RefreshRemoteState();
                },
                DispatcherPriority.Background);
        }
    }

    private void RefreshRemoteState() => RefreshRemoteState(DateTimeOffset.UtcNow);

    internal void RefreshRemoteState(DateTimeOffset now)
    {
        var server = _remoteServer;
        CanManageRemoteSecurity = server is null
            || server.CanManageSecurityState && server.IsSecurityStateReady;
        var devices = _dataStore.SnapshotRemotePairedDevices();
        _attachingRemoteServer = true;
        try
        {
            RemoteAccessEnabled = _dataStore.Data.Settings.RemoteAccessEnabled;
            UseLocalNetworkForMobile = _dataStore.Data.Settings.RemoteAllowInsecureLan;
            UseDevTunnelForMobile = _dataStore.Data.Settings.RemoteUseDevTunnel;
        }
        finally
        {
            _attachingRemoteServer = false;
        }
        IsMobileTailscaleAvailable = server is { IsRunning: true, IsTailscaleAvailable: true };
        MobileTransportDescription = Loc.Get(
            IsMobileTailscaleAvailable
                ? "Remote_TransportDetected"
                : "Remote_TransportUnavailable");
        IsDevTunnelSetupBusy = server is { IsDevTunnelStarting: true, RequiresDevTunnelInstallConfirmation: false };
        DevTunnelSetupTitle = Loc.Get(server switch
        {
            { DevTunnelError: not null } or { StartError: not null } => "Remote_DevTunnelErrorTitle",
            { DevTunnelOrigin: not null, IsWebAppAvailable: false } => "Remote_DevTunnelErrorTitle",
            { RequiresDevTunnelInstallConfirmation: true } => "Remote_DevTunnelInstallTitle",
            { RequiresDevTunnelSignIn: true } or { IsDevTunnelSigningIn: true } => "Remote_DevTunnelSignInTitle",
            { IsDevTunnelReconnecting: true } => "Remote_DevTunnelReconnect",
            _ => "Remote_DevTunnelSetupTitle"
        });
        DevTunnelErrorDetails = server?.DevTunnelError ?? "";
        DevTunnelStatusText = server switch
        {
            { StartError: { } error } => error,
            { DevTunnelError: not null } => Loc.Get("Remote_DevTunnelErrorSummary"),
            { DevTunnelOrigin: not null, IsWebAppAvailable: false } => Loc.Get("Remote_WebAppUnavailable"),
            { DevTunnelOrigin: not null, DevTunnelAccount: { } account } =>
                Loc.Get("Remote_DevTunnelReady", account),
            { DevTunnelSetupMessage: { } message } => message,
            { IsDevTunnelStarting: true } => Loc.Get("Remote_DevTunnelStarting"),
            _ => Loc.Get("Remote_DevTunnelInstall")
        };
        IsDevTunnelInstallDialogOpen = server?.RequiresDevTunnelInstallConfirmation == true;
        DevTunnelSignInUrl = server?.DevTunnelSignInUrl ?? "";
        DevTunnelSignInCode = server?.DevTunnelSignInCode ?? "";
        if (DevTunnelSignInCode.Length == 0)
        {
            _openedDevTunnelSignInCode = null;
            DevTunnelBrowserMessage = "";
        }
        else if (_openedDevTunnelSignInCode != DevTunnelSignInCode)
        {
            _openedDevTunnelSignInCode = DevTunnelSignInCode;
            OpenDevTunnelSignIn();
        }
        RetryMobileDevTunnelCommand.NotifyCanExecuteChanged();
        SignInMobileDevTunnelCommand.NotifyCanExecuteChanged();
        UseDevTunnelBrowserSignInCommand.NotifyCanExecuteChanged();
        CancelMobileDevTunnelSetupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRetryMobileDevTunnel));
        OnPropertyChanged(nameof(CanSignInMobileDevTunnel));
        OnPropertyChanged(nameof(CanUseDevTunnelBrowserSignIn));
        OnPropertyChanged(nameof(CanCancelMobileDevTunnelSetup));
        OnPropertyChanged(nameof(DevTunnelRetryActionText));
        var pairing = server is null
            ? (Code: (string?)null, ExpiresAt: (DateTimeOffset?)null)
            : server.GetPairingDisplayState(now);

        RemoteStatusText = server switch
        {
            { StartError: { } error } => error,
            { IsRunning: true } when server.ListenAddresses.Count > 0 =>
                Loc.Get(
                    "Remote_ListeningOn",
                    PreferredRemoteAddress(server, UseLocalNetworkForMobile)),
            { IsRunning: true } => Loc.Get("Remote_WaitingForConnection"),
            _ => Loc.Get("Remote_NotRunning")
        };

        RemoteDevicesText = devices.Count == 0
            ? Loc.Get("Remote_NoDevices")
            : Loc.Get("Remote_DeviceCount", devices.Count.ToString());
        RemoteDevices.Clear();
        foreach (var device in devices.OrderBy(static device => device.DeviceName, StringComparer.CurrentCultureIgnoreCase))
        {
            RemoteDevices.Add(new RemotePairedDeviceItem(
                device.DeviceId,
                string.IsNullOrWhiteSpace(device.DeviceName) ? Loc.Get("Remote_UnknownDevice") : device.DeviceName,
                device.LastSeenAt is { } lastSeen
                    ? Loc.Get("Remote_LastSeen", lastSeen.ToLocalTime().ToString("g"))
                    : Loc.Get("Remote_LastSeenNever")));
        }

        RemotePairingCode = pairing.Code ?? "";
        IsRemotePairing = RemotePairingCode.Length > 0;
        RemotePairActionText = Loc.Get(IsRemotePairing ? "Remote_PairStop" : "Remote_PairButton");
        IsMobileSetupChoiceEnabled =
            RemoteAccessEnabled
            && server is { IsRunning: true }
            && server.ListenAddresses.Count > 0;
        ScheduleRemotePairingExpiry(pairing.ExpiresAt, now);
        if (RemoteAccessEnabled && UseDevTunnelForMobile && !IsMobileSetupActive
            && server is { IsRunning: true })
            StartMobileSetup(MobileSetupKind.Web);
        else
            RefreshMobileOnboarding(server);
    }

    private void ScheduleRemotePairingExpiry(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        _remotePairingExpiryRegistration?.Dispose();
        _remotePairingExpiryRegistration = null;

        if (!IsRemotePairing || expiresAt is not { } expiry)
            return;

        var delay = expiry - now;
        if (delay <= TimeSpan.Zero)
            return;

        _remotePairingExpiryRegistration = DispatcherTimer.RunOnce(
            () =>
            {
                _remotePairingExpiryRegistration = null;
                RefreshRemoteState();
            },
            delay,
            DispatcherPriority.Background);
    }

    private static string PreferredRemoteAddress(
        LumiRemoteServer server,
        bool useLocalNetwork) =>
        server.DevTunnelOrigin ?? MobileOnboardingLinks.SelectEndpoint(
            server.ListenAddresses,
            useLocalNetwork
                ? MobileOnboardingTransport.LocalNetwork
                : MobileOnboardingTransport.Tailscale)?.BaseUrl
        ?? "http://127.0.0.1";

    partial void OnRemoteAccessEnabledChanged(bool value)
    {
        if (_attachingRemoteServer)
            return;
        if (_remoteServer is { CanManageSecurityState: false })
        {
            RefreshRemoteState();
            return;
        }

        if (_dataStore.Data.Settings.RemoteAccessEnabled == value && _remoteServer?.IsRunning == value)
            return;

        _dataStore.Data.Settings.RemoteAccessEnabled = value;
        if (value && UseDevTunnelForMobile)
            _remoteServer?.EnsureDevTunnelId();
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();

        if (value)
            _remoteServer?.Start();
        else
        {
            ResetMobileOnboarding();
            _remoteServer?.Stop();
        }

        RefreshRemoteState();
    }

    partial void OnUseLocalNetworkForMobileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMobileTailscaleSelected));
        OnPropertyChanged(nameof(IsMobileLocalNetworkSelected));
        if (_attachingRemoteServer)
            return;
        if (_remoteServer is { CanManageSecurityState: false })
        {
            RefreshRemoteState();
            return;
        }

        var leavingTunnel = _dataStore.Data.Settings.RemoteUseDevTunnel;
        if (_dataStore.Data.Settings.RemoteAllowInsecureLan == value && !leavingTunnel)
            return;

        if (leavingTunnel)
        {
            _remoteServer?.Stop();
            ResetMobileOnboarding();
        }
        _dataStore.Data.Settings.RemoteUseDevTunnel = false;
        _dataStore.Data.Settings.RemoteAllowInsecureLan = value;
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();
        if (leavingTunnel && RemoteAccessEnabled)
            _remoteServer?.Start();
        else
            _remoteServer?.RefreshNetworkPolicy();
        RefreshRemoteState();
    }

    partial void OnUseDevTunnelForMobileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMobileTailscaleSelected));
        OnPropertyChanged(nameof(IsMobileLocalNetworkSelected));
        OnPropertyChanged(nameof(IsMobileAndroidSetupChoiceEnabled));
        OnPropertyChanged(nameof(MobileExperienceDescription));
        if (_attachingRemoteServer)
            return;
        if (_remoteServer is { CanManageSecurityState: false })
        {
            RefreshRemoteState();
            return;
        }
        if (_dataStore.Data.Settings.RemoteUseDevTunnel == value)
            return;

        _remoteServer?.Stop();
        ResetMobileOnboarding();
        _dataStore.Data.Settings.RemoteUseDevTunnel = value;
        if (value)
        {
            _dataStore.Data.Settings.RemoteAllowInsecureLan = false;
            _remoteServer?.EnsureDevTunnelId();
        }
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();
        if (RemoteAccessEnabled)
            _remoteServer?.Start();
        RefreshRemoteState();
    }

    partial void OnIsMobileSetupChoiceEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(IsMobileAndroidSetupChoiceEnabled));

    partial void OnIsMobileTailscaleAvailableChanged(bool value) =>
        OnPropertyChanged(nameof(IsMobileTailscaleSelected));

    private async Task PersistRemoteSettingsAsync()
    {
        try
        {
            await _dataStore.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Remote] Failed to persist phone settings: {ex.Message}");
        }
    }

    [RelayCommand]
    private void StartMobileWebSetup() =>
        StartMobileSetup(MobileSetupKind.Web);

    [RelayCommand]
    private void StartMobileAndroidSetup()
    {
        if (UseDevTunnelForMobile)
        {
            MobileSetupDescription = Loc.Get("Remote_DevTunnelWebOnly");
            return;
        }
        StartMobileSetup(MobileSetupKind.Android);
    }

    private void StartMobileSetup(MobileSetupKind kind)
    {
        if (!RemoteAccessEnabled || _remoteServer is not { IsRunning: true } server)
            return;

        _activeMobileSetupKind = kind;
        IsMobileSetupActive = true;
        IsMobileWebSetup = kind == MobileSetupKind.Web;
        IsMobileAndroidSetup = kind == MobileSetupKind.Android;
        _mobileSetupNeedsPairing = true;
        RefreshMobileOnboarding(server);
    }

    [RelayCommand]
    private void SelectMobileTailscale()
    {
        if (RemoteAccessEnabled && IsMobileTailscaleAvailable)
        {
            UseLocalNetworkForMobile = false;
            UseDevTunnelForMobile = false;
        }
    }

    [RelayCommand]
    private void SelectMobileLocalNetwork()
    {
        if (RemoteAccessEnabled)
            UseLocalNetworkForMobile = true;
    }

    [RelayCommand]
    private void SelectMobileDevTunnel()
    {
        if (RemoteAccessEnabled)
        {
            UseDevTunnelForMobile = true;
            StartMobileSetup(MobileSetupKind.Web);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryMobileDevTunnel))]
    private void RetryMobileDevTunnel()
    {
        if (_remoteServer is { IsRunning: true } server)
            server.RestartDevTunnel();
        else
            _remoteServer?.Start();
        RefreshRemoteState();
    }

    [RelayCommand(CanExecute = nameof(CanSignInMobileDevTunnel))]
    private void SignInMobileDevTunnel()
    {
        _remoteServer?.RestartDevTunnel(signIn: true);
        RefreshRemoteState();
    }

    [RelayCommand(CanExecute = nameof(CanUseDevTunnelBrowserSignIn))]
    private void UseDevTunnelBrowserSignIn()
    {
        _remoteServer?.RestartDevTunnel(signIn: true, useDeviceCode: true);
        RefreshRemoteState();
    }

    [RelayCommand(CanExecute = nameof(CanCancelMobileDevTunnelSetup))]
    private void CancelMobileDevTunnelSetup()
    {
        _remoteServer?.CancelDevTunnelSetup();
        RefreshRemoteState();
    }

    [RelayCommand]
    private void OpenDevTunnelSignIn()
    {
        if (!RemoteDevTunnelHost.IsAllowedSignInUrl(DevTunnelSignInUrl))
        {
            DevTunnelBrowserMessage = Loc.Get("Remote_DevTunnelInvalidResponse");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(DevTunnelSignInUrl) { UseShellExecute = true });
            DevTunnelBrowserMessage = "";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            Trace.TraceWarning($"[Remote] Could not open Microsoft sign-in: {ex.Message}");
            DevTunnelBrowserMessage = Loc.Get("Remote_DevTunnelBrowserOpenFailed");
        }
    }

    [RelayCommand]
    private async Task CopyDevTunnelSignInCodeAsync()
    {
        if (HasDevTunnelSignInCode)
            await Services.ClipboardHelper.CopyTextAsync(DevTunnelSignInCode);
    }

    [RelayCommand]
    private void ApproveDevTunnelInstall()
    {
        _remoteServer?.RespondToDevTunnelInstallConfirmation(true);
        IsDevTunnelInstallDialogOpen = false;
    }

    [RelayCommand]
    private void CancelDevTunnelInstall()
    {
        _remoteServer?.RespondToDevTunnelInstallConfirmation(false);
        IsDevTunnelInstallDialogOpen = false;
    }

    [RelayCommand]
    private async Task CopyMobileSetupLinkAsync()
    {
        if (MobileSetupUrl.Length == 0)
            return;

        await Services.ClipboardHelper.CopyTextAsync(MobileSetupUrl);
        MobileSetupDescription = Loc.Get("Remote_SetupCopied");
    }

    private void RefreshMobileOnboarding(LumiRemoteServer? server)
    {
        if (!IsMobileSetupActive)
            return;

        MobileSetupTitle = Loc.Get(
            UseDevTunnelForMobile
                ? "Remote_DevTunnelPhonePanelTitle"
                : _activeMobileSetupKind == MobileSetupKind.Web
                ? "Remote_SetupWebPanelTitle"
                : "Remote_SetupAndroidPanelTitle");
        MobileSetupInstructions = Loc.Get(
            _activeMobileSetupKind == MobileSetupKind.Web
                ? "Remote_SetupWebInstructions"
                : "Remote_SetupAndroidInstructions");
        if (UseDevTunnelForMobile)
            MobileSetupInstructions = Loc.Get("Remote_DevTunnelInstructions");

        if (server is not { IsRunning: true })
        {
            SetMobileSetupUnavailable(Loc.Get("Remote_WebSetupStarting"));
            return;
        }

        if (!server.IsWebAppAvailable)
        {
            SetMobileSetupUnavailable(Loc.Get("Remote_WebAppUnavailable"));
            return;
        }

        var endpoint = MobileOnboardingLinks.SelectEndpoint(
            server.ListenAddresses,
            SelectedMobileTransport);
        if (endpoint is null)
        {
            SetMobileSetupUnavailable(UseDevTunnelForMobile
                ? DevTunnelStatusText
                : Loc.Get("Remote_SetupNoAddress"));
            return;
        }

        MobileSetupUrl = _activeMobileSetupKind == MobileSetupKind.Web
            ? MobileOnboardingLinks.BuildWebAppUrl(endpoint.BaseUrl)
            : MobileOnboardingLinks.BuildAndroidInstallUrl(
                endpoint.BaseUrl,
                AppVersion);
        MobileSetupQrValue = MobileSetupUrl;
        MobileSetupDescription = Loc.Get(
            UseDevTunnelForMobile
                ? "Remote_DevTunnelPhoneDescription"
                : _activeMobileSetupKind == MobileSetupKind.Web
                ? "Remote_SetupWebPanelDesc"
                : "Remote_SetupAndroidPanelDesc");
        MobileSetupConnectionText = Loc.Get(
            endpoint.Transport switch
            {
                MobileOnboardingTransport.DevTunnel => "Remote_DevTunnelAccount",
                MobileOnboardingTransport.LocalNetwork => "Remote_SetupUsingWifi",
                _ => "Remote_SetupUsingTailscale"
            },
            server.DevTunnelAccount ?? "");
        IsMobileSetupReady = true;
        if (_mobileSetupNeedsPairing)
        {
            _mobileSetupNeedsPairing = false;
            server.BeginPairing();
        }
    }

    private void SetMobileSetupUnavailable(string description)
    {
        IsMobileSetupReady = false;
        MobileSetupDescription = description;
        MobileSetupConnectionText = "";
        MobileSetupUrl = "";
        MobileSetupQrValue = "";
    }

    private void ResetMobileOnboarding()
    {
        _mobileSetupNeedsPairing = false;
        IsMobileSetupActive = false;
        IsMobileSetupChoiceEnabled = false;
        IsMobileWebSetup = false;
        IsMobileAndroidSetup = false;
        IsMobileSetupReady = false;
        IsMobileTailscaleAvailable = false;
        MobileTransportDescription = "";
        MobileSetupTitle = "";
        MobileSetupDescription = "";
        MobileSetupConnectionText = "";
        MobileSetupInstructions = "";
        MobileSetupUrl = "";
        MobileSetupQrValue = "";
    }

    [RelayCommand]
    private void ToggleRemotePairing()
    {
        if (_remoteServer is not { IsRunning: true } server)
            return;

        if (IsRemotePairing)
            server.CancelPairing();
        else
            server.BeginPairing();
    }

    [RelayCommand]
    private async Task RevokeRemoteDeviceAsync(RemotePairedDeviceItem? device)
    {
        if (device is null || _remoteServer is not { CanManageSecurityState: true } server)
            return;

        await server.RevokeDeviceAsync(device.DeviceId);
        RefreshRemoteState();
    }

    private void DisposeRemoteState()
    {
        _remotePairingExpiryRegistration?.Dispose();
        _remotePairingExpiryRegistration = null;

        if (_remoteServer is { } server)
            server.StateChanged -= OnRemoteServerStateChanged;
        _remoteServer = null;
    }
}
