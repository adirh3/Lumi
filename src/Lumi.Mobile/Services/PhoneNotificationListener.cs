using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Services;

/// <summary>How the phone's notification stream to the PC is doing.</summary>
public enum PhoneNotificationLinkState
{
    Stopped,
    Connecting,
    Connected,

    /// <summary>The PC could not be reached; the next try waits for its delay or a network change.</summary>
    Waiting,

    /// <summary>The PC no longer knows this phone. Nothing more can be done without pairing again.</summary>
    Unpaired,

    /// <summary>The PC's Lumi predates phone notifications.</summary>
    Unsupported
}

/// <summary>Shows notifications the way the platform does. Called off the UI thread.</summary>
public interface IPhoneNotificationPresenter
{
    /// <summary>Shows or updates a notification. <paramref name="alert"/> is false for one already shown.</summary>
    void Show(RemoteNotification notification, bool alert);

    void Dismiss(IReadOnlyCollection<string> ids);

    void LinkStateChanged(PhoneNotificationLinkState state);
}

/// <summary>
/// Keeps the phone's notification stream to the PC open for as long as notifications are on: it
/// reconnects with growing pauses while the PC cannot be reached, at once when the network comes
/// back, and turns each frame into what the phone shows. One instance per paired PC.
/// </summary>
public sealed class PhoneNotificationListener
{
    public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromMinutes(30);

    private readonly LumiRemoteClient _client;
    private readonly IPhoneNotificationPresenter _presenter;
    private readonly PhoneNotificationLedger _ledger;
    private readonly RemoteConnectionOptions _pacing;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private RemoteConnectionSupervisor? _supervisor;
    private Task? _run;
    private string _epoch = "";
    private PhoneNotificationLinkState _state = PhoneNotificationLinkState.Stopped;

    public PhoneNotificationListener(
        LumiRemoteClient client,
        IPhoneNotificationPresenter presenter,
        PhoneNotificationLedger ledger,
        IRemoteNetworkMonitor? network = null,
        TimeProvider? time = null,
        Func<double>? random = null)
    {
        _client = client;
        _presenter = presenter;
        _ledger = ledger;
        // Paced like the app's own connection, only slower, and it never gives up while there is a
        // network: the phone has no other way to hear from the PC.
        _pacing = new RemoteConnectionOptions
        {
            InitialRetryDelay = InitialRetryDelay,
            MaxRetryDelay = MaxRetryDelay,
            GiveUpAfter = TimeSpan.MaxValue,
            Network = network,
            Time = time ?? TimeProvider.System,
            Random = random ?? Random.Shared.NextDouble
        };
    }

    public PhoneNotificationLinkState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false })
                return;

            _supervisor?.Dispose();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _supervisor = new RemoteConnectionSupervisor(_pacing);
            var (supervisor, token) = (_supervisor, _cts.Token);
            _run = Task.Run(() => RunAsync(supervisor, token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        RemoteConnectionSupervisor? supervisor;
        Task? run;
        lock (_gate)
        {
            cts = _cts;
            supervisor = _supervisor;
            run = _run;
            _cts = null;
            _supervisor = null;
            _run = null;
        }

        if (cts is null)
            return;

        await cts.CancelAsync().ConfigureAwait(false);
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        supervisor?.Dispose();
        cts.Dispose();
        SetState(PhoneNotificationLinkState.Stopped);
    }

    private async Task RunAsync(RemoteConnectionSupervisor supervisor, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan? delay;
            SetState(PhoneNotificationLinkState.Connecting);
            try
            {
                await _client.ReadNotificationStreamAsync(
                        OnFrame,
                        () =>
                        {
                            failures = 0;
                            supervisor.OnConnected();
                            SetState(PhoneNotificationLinkState.Connected);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                // The PC closed the stream (it restarted, or its network changed): come back quickly.
                delay = supervisor.NextDelay(++failures);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                SetState(PhoneNotificationLinkState.Unpaired);
                return;
            }
            catch (RemoteNotificationsUnsupportedException)
            {
                SetState(PhoneNotificationLinkState.Unsupported);
                delay = UnsupportedRetryDelay;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Notifications] Stream failed: {ex.GetType().Name}: {ex.Message}");
                delay = supervisor.NextDelay(++failures);
            }

            if (State != PhoneNotificationLinkState.Unsupported)
                SetState(PhoneNotificationLinkState.Waiting);

            try
            {
                // Without a network there is no delay: the next try waits for the network to return.
                await supervisor.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnFrame(RemoteEventFrame frame)
    {
        try
        {
            switch (frame.Event)
            {
                case RemoteProtocol.Events.NotificationSync:
                    if (JsonSerializer.Deserialize(frame.Data, RemoteJsonContext.Default.RemoteNotificationSync) is { } sync)
                        ApplySync(sync);
                    break;
                case RemoteProtocol.Events.Notification:
                    if (JsonSerializer.Deserialize(frame.Data, RemoteJsonContext.Default.RemoteNotification) is { } notification)
                        Apply(notification);
                    break;
                case RemoteProtocol.Events.NotificationsDismissed:
                    if (JsonSerializer.Deserialize(frame.Data, RemoteJsonContext.Default.RemoteNotificationDismissal) is { Ids.Count: > 0 } dismissal)
                    {
                        _ledger.Forget(dismissal.Ids);
                        _presenter.Dismiss(dismissal.Ids);
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning($"[Notifications] Unreadable {frame.Event} frame: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows what the PC still lists and this phone has not shown yet, and takes down what this run
    /// of the PC no longer lists. Only the newest new notification makes a sound.
    /// </summary>
    private void ApplySync(RemoteNotificationSync sync)
    {
        _epoch = sync.Epoch;
        var fresh = sync.Notifications
            .Where(notification => notification.Id.Length > 0 && !_ledger.HasShown(notification))
            .OrderBy(static notification => notification.CreatedAt)
            .ToList();
        for (var index = 0; index < fresh.Count; index++)
        {
            _ledger.Record(fresh[index], _epoch);
            _presenter.Show(fresh[index], alert: index == fresh.Count - 1);
        }

        var stale = _ledger.TakeStale(
            _epoch,
            sync.Notifications.Select(static notification => notification.Id).ToHashSet(StringComparer.Ordinal));
        if (stale.Count > 0)
            _presenter.Dismiss(stale);
    }

    private void Apply(RemoteNotification notification)
    {
        if (notification.Id.Length == 0)
            return;

        if (notification.Kind == RemoteProtocol.NotificationKinds.Test)
        {
            _presenter.Show(notification, alert: true);
            return;
        }

        var known = _ledger.HasShown(notification);
        _ledger.Record(notification, _epoch);
        _presenter.Show(notification, alert: !known);
    }

    private void SetState(PhoneNotificationLinkState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;
            _state = state;
        }

        _presenter.LinkStateChanged(state);
    }
}

/// <summary>
/// The notifications this phone has shown, so a reconnect or a restart of the app never sounds the
/// same one twice, and so the ones the PC dealt with can be taken down. A small JSON file beside the
/// connection settings.
/// </summary>
public sealed class PhoneNotificationLedger
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(3);

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, PhoneNotificationLedgerEntry> _entries = new(StringComparer.Ordinal);

    /// <param name="directory">Where to keep the file; null keeps it in memory only.</param>
    public PhoneNotificationLedger(string? directory, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        if (directory is null)
            return;

        _path = Path.Combine(directory, "notifications.json");
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize(
                    File.ReadAllText(_path),
                    PhoneNotificationLedgerJsonContext.Default.ListPhoneNotificationLedgerEntry) is { } entries)
            {
                var cutoff = _time.GetUtcNow() - Retention;
                foreach (var entry in entries.Where(entry => entry.Id.Length > 0 && entry.ShownAt >= cutoff))
                    _entries[entry.Id] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[Notifications] Could not read the shown notifications: {ex.Message}");
        }
    }

    /// <summary>The default place, beside the phone's connection settings.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("LUMI_MOBILE_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumiMobile");

    public bool Contains(string id)
    {
        lock (_gate)
            return _entries.ContainsKey(id);
    }

    /// <summary>
    /// Whether this phone already showed this very notification. A chat's next reply keeps the id of
    /// the one before, even after the PC restarts, so the time it was raised tells them apart.
    /// </summary>
    public bool HasShown(RemoteNotification notification)
    {
        lock (_gate)
            return _entries.TryGetValue(notification.Id, out var entry) && entry.CreatedAt == notification.CreatedAt;
    }

    public void Record(RemoteNotification notification, string epoch)
    {
        lock (_gate)
        {
            _entries[notification.Id] = new PhoneNotificationLedgerEntry
            {
                Id = notification.Id,
                Epoch = epoch,
                ChatId = notification.ChatId,
                CreatedAt = notification.CreatedAt,
                ShownAt = _time.GetUtcNow()
            };
            PruneLocked();
            SaveLocked();
        }
    }

    public void Forget(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var changed = false;
            foreach (var id in ids)
                changed |= _entries.Remove(id);
            if (changed)
                SaveLocked();
        }
    }

    /// <summary>Forgets and returns what this run of the PC showed and no longer lists.</summary>
    public IReadOnlyList<string> TakeStale(string epoch, IReadOnlySet<string> active)
    {
        lock (_gate)
        {
            var stale = _entries.Values
                .Where(entry => entry.Epoch == epoch && !active.Contains(entry.Id))
                .Select(static entry => entry.Id)
                .ToList();
            foreach (var id in stale)
                _entries.Remove(id);
            if (stale.Count > 0)
                SaveLocked();
            return stale;
        }
    }

    /// <summary>Forgets and returns the notifications of a chat the user just opened.</summary>
    public IReadOnlyList<string> TakeChat(Guid chatId)
    {
        lock (_gate)
        {
            var shown = _entries.Values
                .Where(entry => entry.ChatId == chatId)
                .Select(static entry => entry.Id)
                .ToList();
            foreach (var id in shown)
                _entries.Remove(id);
            if (shown.Count > 0)
                SaveLocked();
            return shown;
        }
    }

    /// <summary>Forgets everything and returns what was shown, when the phone leaves its PC.</summary>
    public IReadOnlyList<string> TakeAll()
    {
        lock (_gate)
        {
            var shown = _entries.Keys.ToList();
            _entries.Clear();
            if (shown.Count > 0)
                SaveLocked();
            return shown;
        }
    }

    private void PruneLocked()
    {
        var cutoff = _time.GetUtcNow() - Retention;
        foreach (var id in _entries.Values.Where(entry => entry.ShownAt < cutoff).Select(static entry => entry.Id).ToList())
            _entries.Remove(id);
    }

    private void SaveLocked()
    {
        if (_path is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    _entries.Values.ToList(),
                    PhoneNotificationLedgerJsonContext.Default.ListPhoneNotificationLedgerEntry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[Notifications] Could not save the shown notifications: {ex.Message}");
        }
    }
}

public sealed class PhoneNotificationLedgerEntry
{
    public string Id { get; set; } = "";
    public string Epoch { get; set; } = "";
    public Guid ChatId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ShownAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PhoneNotificationLedgerEntry>))]
internal sealed partial class PhoneNotificationLedgerJsonContext : JsonSerializerContext;
