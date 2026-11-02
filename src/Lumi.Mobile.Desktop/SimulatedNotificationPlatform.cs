#if DEBUG
using System.Diagnostics;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Desktop;

/// <summary>
/// Debug-only notifications for the simulator: <c>--notifications</c> is a phone that allows Lumi's
/// notifications, <c>--notifications-denied</c> one that refuses them. Settings → Notifications and the
/// offer appear as on a phone, and the real notification stream runs against the paired PC. What a
/// phone would show is appended to <c>simulated-notifications.log</c> in the simulator's data folder;
/// how Android draws it is checked on a device or emulator.
/// </summary>
internal sealed class SimulatedNotificationPlatform(bool denyPermission)
    : IPhoneNotificationPlatform, IPhoneNotificationPresenter
{
    private readonly object _gate = new();
    private readonly PhoneNotificationLedger _ledger = new(PhoneNotificationLedger.DefaultDirectory);
    private readonly string _logPath = Path.Combine(PhoneNotificationLedger.DefaultDirectory, "simulated-notifications.log");
    private PhoneNotificationListener? _listener;
    private LumiRemoteClient? _client;
    private string? _configuration;
    private PhoneNotificationLinkState _state = PhoneNotificationLinkState.Stopped;

    public bool IsAvailable => true;

    public bool AreNotificationsAllowed => !denyPermission;

    public PhoneNotificationLinkState LinkState
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public event Action? LinkStateChanged;

    public bool HasChannelSettings => true;

    public Task<bool> RequestPermissionAsync() => Task.FromResult(!denyPermission);

    public void Apply(bool enabled)
    {
        var settings = new MobileSettingsStore().Load();
        var configuration = enabled && settings is { BaseUrl.Length: > 0, Token.Length: > 0 }
            ? $"{settings.BaseUrl}\n{settings.Token}\n{settings.DeviceId}"
            : null;
        if (configuration == _configuration)
            return;

        Stop();
        _configuration = configuration;
        if (configuration is null)
        {
            SetState(PhoneNotificationLinkState.Stopped);
            return;
        }

        _client = new LumiRemoteClient(settings.DeviceId, settings.DeviceName);
        _client.Configure(settings.BaseUrl, settings.Token);
        _listener = new PhoneNotificationListener(_client, this, _ledger, MobilePlatformServices.NetworkMonitor);
        _listener.Start();
    }

    public bool IsChannelEnabled(PhoneNotificationChannel channel) => true;

    public void OpenSettings(PhoneNotificationChannel? channel) =>
        Write($"settings {channel?.ToString() ?? "all"}");

    public void ClearChat(Guid chatId)
    {
        var ids = _ledger.TakeChat(chatId);
        if (ids.Count > 0)
            Dismiss(ids);
    }

    public void Show(RemoteNotification notification, bool alert) =>
        Write($"{(alert ? "alert" : "quiet")} {notification.Kind} {notification.Id} \"{notification.ChatTitle}\": {notification.Text}");

    public void Dismiss(IReadOnlyCollection<string> ids) => Write($"dismiss {string.Join(", ", ids)}");

    void IPhoneNotificationPresenter.LinkStateChanged(PhoneNotificationLinkState state)
    {
        Write($"link {state}");
        SetState(state);
    }

    private void SetState(PhoneNotificationLinkState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;
            _state = state;
        }

        LinkStateChanged?.Invoke();
    }

    private void Stop()
    {
        var listener = _listener;
        var client = _client;
        _listener = null;
        _client = null;
        if (listener is null)
            return;

        _ = Task.Run(async () =>
        {
            await listener.StopAsync().ConfigureAwait(false);
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
        });
    }

    private void Write(string line)
    {
        line = $"{DateTimeOffset.Now:HH:mm:ss} {line}";
        Trace.WriteLine("[Notifications] " + line);
        try
        {
            lock (_gate)
                File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
#endif
