using System;
using System.Collections.ObjectModel;
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

/// <summary>
/// Settings for the mobile companion. Kept in its own partial so the phone feature adds no noise
/// to the main settings view model.
/// </summary>
public partial class SettingsViewModel
{
    private LumiRemoteServer? _remoteServer;
    private IDisposable? _remotePairingExpiryRegistration;
    private bool _attachingRemoteServer;

    [ObservableProperty] private bool _remoteAccessEnabled;
    [ObservableProperty] private bool _remoteAllowInsecureLan;
    [ObservableProperty] private string _remotePairingCode = "";
    [ObservableProperty] private bool _isRemotePairing;
    [ObservableProperty] private string _remotePairActionText = Loc.Get("Remote_PairButton");
    [ObservableProperty] private string _remoteStatusText = "";
    [ObservableProperty] private string _remoteDevicesText = "";
    [ObservableProperty] private bool _canManageRemoteSecurity = true;

    public ObservableCollection<RemotePairedDeviceItem> RemoteDevices { get; } = [];

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
            RemoteAllowInsecureLan = _dataStore.Data.Settings.RemoteAllowInsecureLan;
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
            RemoteAllowInsecureLan = _dataStore.Data.Settings.RemoteAllowInsecureLan;
        }
        finally
        {
            _attachingRemoteServer = false;
        }
        var pairing = server is null
            ? (Code: (string?)null, ExpiresAt: (DateTimeOffset?)null)
            : server.GetPairingDisplayState(now);

        RemoteStatusText = server is { IsRunning: true }
            ? Loc.Get("Remote_ListeningOn", string.Join(", ", server.ListenAddresses.DefaultIfEmpty("127.0.0.1")))
            : Loc.Get("Remote_NotRunning");

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
        ScheduleRemotePairingExpiry(pairing.ExpiresAt, now);
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
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();

        if (value)
            _remoteServer?.Start();
        else
            _remoteServer?.Stop();

        RefreshRemoteState();
    }

    partial void OnRemoteAllowInsecureLanChanged(bool value)
    {
        if (_attachingRemoteServer)
            return;
        if (_remoteServer is { CanManageSecurityState: false })
        {
            RefreshRemoteState();
            return;
        }

        if (_dataStore.Data.Settings.RemoteAllowInsecureLan == value)
            return;

        _dataStore.Data.Settings.RemoteAllowInsecureLan = value;
        _dataStore.MarkRemoteSecurityChanged();
        _ = PersistRemoteSettingsAsync();
        _remoteServer?.RefreshNetworkPolicy();
        RefreshRemoteState();
    }

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
