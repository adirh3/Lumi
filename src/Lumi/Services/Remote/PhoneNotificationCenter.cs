using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.ViewModels;

namespace Lumi.Services.Remote;

internal enum PhoneNotificationDecision
{
    /// <summary>Keep it: the user may still leave the PC without having seen it.</summary>
    Wait,

    /// <summary>Send it to the phone now.</summary>
    Deliver,

    /// <summary>Forget it: the user saw it here, or had their chance at the PC.</summary>
    Drop
}

/// <summary>What decides whether the phone hears about something now.</summary>
/// <param name="IsRequest">A question, form or approval Lumi waits on, rather than a finished reply.</param>
/// <param name="Age">How long ago it happened.</param>
/// <param name="IsLocked">The PC is locked, or its remote session is disconnected.</param>
/// <param name="IdleFor">How long the PC has gone without input.</param>
/// <param name="AwayAfter">Idle time after which the user counts as away.</param>
/// <param name="IsPhoneInForeground">Lumi is open on a phone.</param>
/// <param name="IsChatOpenOnPhone">The chat itself is on a phone's screen, not just behind its chat list.</param>
/// <param name="IsSeenOnDesktop">The chat is on screen in Lumi's active window and the user is at the PC.</param>
internal readonly record struct PhoneNotificationContext(
    bool IsRequest,
    TimeSpan Age,
    bool IsLocked,
    TimeSpan IdleFor,
    TimeSpan AwayAfter,
    bool IsPhoneInForeground,
    bool IsChatOpenOnPhone,
    bool IsSeenOnDesktop);

/// <summary>What a test notification reached: how many phones heard it, or why a browser did not.</summary>
internal readonly record struct PhoneNotificationTestResult(int Heard, string? PushError);

/// <summary>
/// Tells paired phones about the moments the desktop notifies about — a finished reply, a question,
/// a form or an approval — but only while the user is away from this PC, the way Teams and Slack
/// hold mobile notifications while you are active on the desktop.
/// </summary>
/// <remarks>
/// <para>The user is away when the PC is locked or has had no input for the chosen time. Something
/// that happens while the user is at the PC waits: if the user leaves within <see cref="Grace"/> of
/// it without having dealt with it, the phone hears about it once they count as away; if they read or
/// answer it here, or stay at the PC longer, it never goes out. With Lumi open on the phone the user
/// is taken to be there once the PC has been idle briefly. A finished reply also waits a moment for
/// Lumi's follow-up suggestions (<see cref="SuggestionWait"/>), which the phone offers to send.</para>
/// <para>The Lumi app holds a quiet notification stream (<see cref="RemoteProtocol.Routes.Notifications"/>).
/// Every stream starts with the notifications still waiting, then hears about new ones and about the
/// ones the user dealt with anywhere, so the phone takes them down. A browser instead hands the PC a
/// Web Push subscription, and the PC pushes each notification through the browser's push service
/// (<see cref="WebPush"/>); a newer one for the same chat replaces it there, and opening the chat
/// takes it down.</para>
/// <para>State is owned by the UI thread; streams are registered from HTTP threads.</para>
/// </remarks>
internal sealed class PhoneNotificationCenter : IDisposable
{
    /// <summary>
    /// How long after something happened the user may still leave the PC without it counting as seen.
    /// Someone who kept working at the PC for longer had their chance to see it there.
    /// </summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    /// <summary>With Lumi open on the phone, this much idle time at the PC counts as away.</summary>
    internal static readonly TimeSpan PhoneGlanceIdle = TimeSpan.FromSeconds(30);

    /// <summary>Input this recent means the user is at the PC now.</summary>
    internal static readonly TimeSpan PresentInputWindow = TimeSpan.FromSeconds(15);

    /// <summary>A notification nobody dealt with comes down after this long.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>A test the browser's push service could not hand over by then is no longer worth showing.</summary>
    internal static readonly TimeSpan TestTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a finished reply waits for its follow-up suggestions, which Lumi writes just after it, so
    /// the phone can offer them. Without them by then, it goes out as it is.
    /// </summary>
    internal static readonly TimeSpan SuggestionWait = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<int> AwayMinuteChoices { get; } = [1, 2, 5, 10];

    public const int DefaultAwayMinutes = 2;

    private readonly DataStore _dataStore;
    private readonly MainViewModel _main;
    private readonly IDesktopPresenceProbe _presence;
    private readonly TimeProvider _time;
    private readonly Func<RemoteEventHub?> _hub;
    private readonly Func<bool> _isLumiWindowActive;
    private readonly WebPushSender _webPush;
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly object _listenerGate = new();
    private readonly List<RemoteEventClient> _listeners = [];
    private RemoteNotification[] _delivered = [];
    private DispatcherTimer? _poll;
    private ITimer? _suggestionWait;
    private ITimer? _ping;
    private ChatEventHub? _events;
    private DateTimeOffset _lumiLastActiveAt;
    private DateTimeOffset _lastEvaluatedAt;
    private string _epoch = "";
    private bool _disposed;

    public PhoneNotificationCenter(
        DataStore dataStore,
        MainViewModel main,
        Func<RemoteEventHub?> hub,
        IDesktopPresenceProbe? presence = null,
        TimeProvider? time = null,
        Func<bool>? isLumiWindowActive = null,
        WebPushSender? webPush = null)
    {
        _dataStore = dataStore;
        _main = main;
        _hub = hub;
        _presence = presence ?? SystemDesktopPresenceProbe.Instance;
        _time = time ?? TimeProvider.System;
        _isLumiWindowActive = isLumiWindowActive ?? IsAnyLumiWindowActive;
        _webPush = webPush ?? new WebPushSender(time: _time);
    }

    /// <summary>Raised when the set of phones that can hear notifications changes. Any thread.</summary>
    public event Action? ListenersChanged;

    public bool IsAttached => _events is not null;

    /// <summary>Devices that can hear notifications now: a stream is open, or the browser subscribed to pushes.</summary>
    public IReadOnlySet<string> ListeningDeviceIds
    {
        get
        {
            HashSet<string> devices;
            lock (_listenerGate)
                devices = _listeners.Select(static client => client.DeviceId).ToHashSet(StringComparer.Ordinal);
            foreach (var device in _dataStore.SnapshotRemotePairedDevices())
            {
                if (device.WebPush is not null)
                    devices.Add(device.DeviceId);
            }

            return devices;
        }
    }

    /// <summary>Notifications on the phones now. Exposed for tests.</summary>
    internal IReadOnlyList<RemoteNotification> Delivered => Volatile.Read(ref _delivered);

    /// <summary>Things waiting for the user to leave the PC. Exposed for tests.</summary>
    internal int WaitingCount => _items.Values.Count(static item => !item.IsDelivered);

    private TimeSpan AwayAfter => TimeSpan.FromMinutes(
        AwayMinuteChoices.Contains(_dataStore.Data.Settings.PhoneNotificationAwayMinutes)
            ? _dataStore.Data.Settings.PhoneNotificationAwayMinutes
            : DefaultAwayMinutes);

    private bool IsEnabled => _dataStore.Data.Settings.PhoneNotificationsEnabled;

    // ── Lifetime ────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts following the chats. Called on the UI thread when the remote server starts.</summary>
    public void Attach(string epoch)
    {
        if (_disposed || _events is not null)
            return;

        _epoch = epoch;
        _lumiLastActiveAt = _time.GetUtcNow();
        _events = _main.ChatEvents;
        _events.AttentionRaised += OnAttentionRaised;
        _events.AttentionResolved += OnAttentionResolved;
        _events.SuggestionsSettled += OnSuggestionsSettled;
        _events.EventPublished += OnLifecycleEvent;
        _main.ChatActivityOrReadStateChanged += OnReadStateChanged;
        _main.ChatDeleted += OnChatDeleted;
        _ping = _time.CreateTimer(
            _ => PingListeners(),
            null,
            RemoteProtocol.NotificationPingInterval,
            RemoteProtocol.NotificationPingInterval);
    }

    /// <summary>Stops following the chats and closes every stream. Called on the UI thread.</summary>
    public void Detach()
    {
        if (_events is { } events)
        {
            events.AttentionRaised -= OnAttentionRaised;
            events.AttentionResolved -= OnAttentionResolved;
            events.SuggestionsSettled -= OnSuggestionsSettled;
            events.EventPublished -= OnLifecycleEvent;
            _main.ChatActivityOrReadStateChanged -= OnReadStateChanged;
            _main.ChatDeleted -= OnChatDeleted;
            _events = null;
        }

        _ping?.Dispose();
        _ping = null;
        _suggestionWait?.Dispose();
        _suggestionWait = null;
        _poll?.Stop();
        _poll = null;
        _items.Clear();
        Volatile.Write(ref _delivered, []);

        List<RemoteEventClient> listeners;
        lock (_listenerGate)
        {
            listeners = [.. _listeners];
            _listeners.Clear();
        }

        foreach (var listener in listeners)
            listener.Dispose();
        if (listeners.Count > 0)
            ListenersChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (Dispatcher.UIThread.CheckAccess())
            Detach();
        else
            Dispatcher.UIThread.Post(Detach);
        _webPush.Dispose();
    }

    // ── Streams ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything still waiting on the phones, the first frame of every stream.</summary>
    public RemoteEventFrame BuildSyncFrame() => new(
        RemoteProtocol.Events.NotificationSync,
        JsonSerializer.Serialize(
            new RemoteNotificationSync { Epoch = _epoch, Notifications = [.. Volatile.Read(ref _delivered)] },
            RemoteJsonContext.Default.RemoteNotificationSync));

    /// <summary>
    /// Registers a stream whose sync frame is already queued. A phone keeps one stream: an older one
    /// from the same device is a connection it gave up on.
    /// </summary>
    public void AddListener(RemoteEventClient client)
    {
        List<RemoteEventClient> replaced;
        lock (_listenerGate)
        {
            replaced = _listeners
                .Where(listener => string.Equals(listener.DeviceId, client.DeviceId, StringComparison.Ordinal))
                .ToList();
            _listeners.RemoveAll(replaced.Contains);
            _listeners.Add(client);
        }

        foreach (var listener in replaced)
            listener.Dispose();
        ListenersChanged?.Invoke();
    }

    public void RemoveListener(RemoteEventClient client)
    {
        bool removed;
        lock (_listenerGate)
            removed = _listeners.Remove(client);
        if (removed)
            ListenersChanged?.Invoke();
    }

    /// <summary>
    /// Sends a test straight to <paramref name="deviceId"/>, or to every device when null, whether or
    /// not the user is at the PC: through each open stream, and through the push service of each
    /// browser that subscribed.
    /// </summary>
    public async Task<PhoneNotificationTestResult> SendTestAsync(string? deviceId)
    {
        var notification = new RemoteNotification
        {
            Id = "test:" + Guid.NewGuid().ToString("N"),
            Kind = RemoteProtocol.NotificationKinds.Test,
            CreatedAt = _time.GetUtcNow()
        };
        var heard = Broadcast(
            NotificationFrame(notification),
            client => deviceId is null || string.Equals(client.DeviceId, deviceId, StringComparison.Ordinal));
        var pushes = await PushAsync(notification, deviceId).ConfigureAwait(false);
        heard += pushes.Count(static result => result.Outcome == WebPushOutcome.Delivered);
        return new PhoneNotificationTestResult(
            heard,
            pushes.FirstOrDefault(static result => result.Outcome != WebPushOutcome.Delivered).Error);
    }

    /// <summary>A browser subscribed to pushes, or stopped. Any thread.</summary>
    public void OnSubscriptionsChanged() => ListenersChanged?.Invoke();

    private void PingListeners()
    {
        Broadcast(new RemoteEventFrame(RemoteProtocol.Events.Ping, "{}"), coalesceKey: RemoteProtocol.Events.Ping);
    }

    private int Broadcast(
        RemoteEventFrame frame,
        Func<RemoteEventClient, bool>? predicate = null,
        string? coalesceKey = null)
    {
        List<RemoteEventClient> listeners;
        lock (_listenerGate)
            listeners = [.. _listeners];

        var delivered = 0;
        foreach (var listener in listeners)
        {
            if (predicate is not null && !predicate(listener))
                continue;

            if (listener.Enqueue(frame, coalesceKey))
            {
                delivered++;
                continue;
            }

            RemoveListener(listener);
            listener.Dispose();
        }

        return delivered;
    }

    // ── Chat events ─────────────────────────────────────────────────────────────────────────

    private void OnAttentionRaised(ChatAttention attention)
    {
        if (!OnUiThread(() => OnAttentionRaised(attention)) || !IsEnabled)
            return;

        if (_dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == attention.ChatId) is not { } chat)
            return;

        var notification = Build(chat, attention);
        if (_items.TryGetValue(notification.Id, out var previous))
            Take(previous, dismiss: true);

        var item = new Item(notification, attention.Kind, _time.GetUtcNow(), chat.HasUnreadMessages)
        {
            AwaitsSuggestions = attention.Kind == ChatAttentionKind.Reply && _dataStore.Data.Settings.SuggestFollowUps
        };
        _items[notification.Id] = item;
        Evaluate();
    }

    /// <summary>A reply still waiting for its chat's suggestions takes them, if any came.</summary>
    private void OnSuggestionsSettled(Guid chatId, IReadOnlyList<string> suggestions)
    {
        if (!OnUiThread(() => OnSuggestionsSettled(chatId, suggestions)))
            return;

        // One already sent, or whose suggestions already settled, stays as it is.
        if (!_items.TryGetValue(ReplyKey(chatId), out var item) || !item.AwaitsSuggestions || item.IsDelivered)
            return;

        item.AwaitsSuggestions = false;
        // A suggestion is sent as written, so one too long for a notification is left out, never cut.
        item.Notification.Suggestions =
        [
            .. suggestions
                .Select(static suggestion => PhoneNotificationText.Line(suggestion, int.MaxValue))
                .Where(static suggestion => suggestion.Length is > 0 and <= RemoteProtocol.MobileNotificationSuggestionLimit)
                .Distinct(StringComparer.Ordinal)
                .Take(RemoteProtocol.MobileNotificationSuggestionCount)
        ];
        Evaluate();
    }

    private void OnAttentionResolved(Guid chatId, string requestId)
    {
        if (!OnUiThread(() => OnAttentionResolved(chatId, requestId)))
            return;

        if (_items.TryGetValue(RequestKey(chatId, requestId), out var item))
            Take(item, dismiss: true);
    }

    private void OnLifecycleEvent(ChatLifecycleEvent chatEvent)
    {
        // A new turn moves the conversation on: the previous reply is no longer news.
        if (chatEvent.EventType != ChatLifecycleEventTypes.TurnStart
            || !OnUiThread(() => OnLifecycleEvent(chatEvent)))
        {
            return;
        }

        if (_items.TryGetValue(ReplyKey(chatEvent.ChatId), out var item))
            Take(item, dismiss: true);
    }

    private void OnReadStateChanged(Guid chatId)
    {
        if (!OnUiThread(() => OnReadStateChanged(chatId)))
            return;

        if (_items.TryGetValue(ReplyKey(chatId), out var item)
            && item.WasUnread
            && _dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == chatId) is { HasUnreadMessages: false })
        {
            Take(item, dismiss: true);
        }
    }

    private void OnChatDeleted(Guid chatId)
    {
        if (!OnUiThread(() => OnChatDeleted(chatId)))
            return;

        foreach (var item in _items.Values.Where(item => item.Notification.ChatId == chatId).ToList())
            Take(item, dismiss: true);
    }

    /// <summary>Phone notifications were turned on or off on the PC.</summary>
    public void OnSettingsChanged()
    {
        if (!OnUiThread(OnSettingsChanged))
            return;

        if (!IsEnabled)
        {
            foreach (var item in _items.Values.ToList())
                Take(item, dismiss: true);
            return;
        }

        Evaluate();
    }

    // ── Policy ──────────────────────────────────────────────────────────────────────────────

    internal static PhoneNotificationDecision Decide(in PhoneNotificationContext context)
    {
        if (context.IsChatOpenOnPhone)
            return PhoneNotificationDecision.Drop;

        // The user's last input came long after it happened: they had their chance at the PC.
        if (context.Age - context.IdleFor > Grace)
            return PhoneNotificationDecision.Drop;

        var isAway = context.IsLocked
                     || context.IdleFor >= context.AwayAfter
                     || context.IsPhoneInForeground && context.IdleFor >= PhoneGlanceIdle;
        if (isAway)
            return PhoneNotificationDecision.Deliver;

        // A question waits on the user even when they saw it; only answering it settles it.
        return !context.IsRequest && context.IsSeenOnDesktop
            ? PhoneNotificationDecision.Drop
            : PhoneNotificationDecision.Wait;
    }

    /// <summary>Decides every pending item, and takes down delivered ones the user has seen. UI thread.</summary>
    internal void Evaluate()
    {
        if (_disposed || _events is null)
            return;

        if (_items.Count == 0)
        {
            _poll?.Stop();
            return;
        }

        var now = _time.GetUtcNow();
        var presence = _presence.Read();
        var lumiActive = _isLumiWindowActive();
        // Where the system cannot say, time away from Lumi's own windows stands in; when nothing
        // was being watched, the user is assumed to have been here until now.
        if (lumiActive || now - _lastEvaluatedAt > PollInterval * 2)
            _lumiLastActiveAt = now;
        _lastEvaluatedAt = now;

        var idle = presence.IdleFor ?? now - _lumiLastActiveAt;
        var isPresent = !presence.IsLocked && idle < PresentInputWindow && lumiActive;
        var hub = _hub();
        var phoneInForeground = hub?.HasForegroundClient() == true;

        foreach (var item in _items.Values.ToList())
        {
            if (!_items.ContainsKey(item.Notification.Id))
                continue;

            var chatId = item.Notification.ChatId;
            var openOnPhone = hub?.IsChatOnPhoneScreen(chatId) == true;
            var seenHere = isPresent && IsChatOnScreen(chatId);
            if (item.IsDelivered)
            {
                if (openOnPhone
                    || item.Kind == ChatAttentionKind.Reply && seenHere
                    || now - item.RaisedAt >= Lifetime)
                {
                    Take(item, dismiss: true);
                }

                continue;
            }

            var decision = Decide(new PhoneNotificationContext(
                item.Kind != ChatAttentionKind.Reply,
                now - item.RaisedAt,
                presence.IsLocked,
                idle,
                AwayAfter,
                phoneInForeground,
                openOnPhone,
                seenHere));
            if (decision == PhoneNotificationDecision.Deliver)
            {
                // A finished reply waits a moment for its suggestions, so the phone can offer them.
                if (item.AwaitsSuggestions && now - item.RaisedAt < SuggestionWait)
                    continue;

                Deliver(item);
            }
            else if (decision == PhoneNotificationDecision.Drop)
            {
                Take(item, dismiss: false);
            }
        }

        WakeWhenSuggestionWaitEnds(now);

        if (_items.Count == 0)
        {
            _poll?.Stop();
        }
        else if (_poll is null)
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
            _poll.Tick += (_, _) => Evaluate();
            _poll.Start();
        }
        else if (!_poll.IsEnabled)
        {
            _poll.Start();
        }
    }

    /// <summary>
    /// Evaluates again the moment the earliest reply stops waiting for its suggestions, rather than at
    /// the next poll, so it goes out on time.
    /// </summary>
    private void WakeWhenSuggestionWaitEnds(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var item in _items.Values)
        {
            var ends = item.RaisedAt + SuggestionWait;
            if (item.AwaitsSuggestions && !item.IsDelivered && ends > now && (next is null || ends < next))
                next = ends;
        }

        if (next is not { } due)
            return;

        _suggestionWait ??= _time.CreateTimer(
            _ => Dispatcher.UIThread.Post(Evaluate),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _suggestionWait.Change(due - now, Timeout.InfiniteTimeSpan);
    }

    private void Deliver(Item item)
    {
        item.IsDelivered = true;
        var delivered = _items.Values
            .Where(static candidate => candidate.IsDelivered)
            .OrderBy(static candidate => candidate.RaisedAt)
            .ToList();

        // The phone shows the newest; the oldest give way.
        foreach (var oldest in delivered.Take(Math.Max(0, delivered.Count - RemoteProtocol.MobileNotificationCountLimit)))
            Take(oldest, dismiss: true);

        PublishDelivered();
        Broadcast(NotificationFrame(item.Notification));
        var notification = item.Notification;
        _ = Task.Run(async () =>
        {
            try
            {
                await PushAsync(notification).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Remote] Browser pushes failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Pushes a notification through the push service of every browser that subscribed, or only of
    /// <paramref name="deviceId"/>'s. A subscription its push service no longer knows is forgotten.
    /// </summary>
    private async Task<IReadOnlyList<WebPushResult>> PushAsync(RemoteNotification notification, string? deviceId = null)
    {
        var signingKey = _dataStore.Data.Settings.RemoteWebPushKey;
        var targets = _dataStore.SnapshotRemotePairedDevices()
            .Where(device => device.WebPush is not null
                             && (deviceId is null || string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal)))
            .Select(static device => device.WebPush!)
            .ToList();
        if (targets.Count == 0 || string.IsNullOrEmpty(signingKey))
            return [];

        var payload = WebPush.Payload(notification, Environment.MachineName);
        var timeToLive = notification.Kind == RemoteProtocol.NotificationKinds.Test ? TestTimeToLive : Lifetime;
        var results = await Task.WhenAll(targets.Select(target => SendPushAsync(target, signingKey, payload, timeToLive)))
            .ConfigureAwait(false);

        var expired = false;
        for (var index = 0; index < results.Length; index++)
        {
            if (results[index].Outcome == WebPushOutcome.Expired)
                expired |= _dataStore.RemoveRemoteWebPush(targets[index].Endpoint);
            else if (results[index].Outcome == WebPushOutcome.Failed)
                Trace.TraceWarning($"[Remote] A browser push did not go through: {results[index].Error}");
        }

        if (expired)
        {
            ListenersChanged?.Invoke();
            try
            {
                await _dataStore.SaveAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Remote] Could not save a forgotten push subscription: {ex.Message}");
            }
        }

        return results;
    }

    private async Task<WebPushResult> SendPushAsync(
        RemoteWebPushSubscription target,
        string signingKey,
        byte[] payload,
        TimeSpan timeToLive)
    {
        try
        {
            return await _webPush.SendAsync(target, signingKey, payload, timeToLive).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Lumi is closing.
            return new WebPushResult(WebPushOutcome.Failed, "Lumi is closing.");
        }
    }

    private void Take(Item item, bool dismiss)
    {
        if (!_items.Remove(item.Notification.Id))
            return;

        if (!item.IsDelivered)
            return;

        PublishDelivered();
        if (dismiss)
        {
            Broadcast(new RemoteEventFrame(
                RemoteProtocol.Events.NotificationsDismissed,
                JsonSerializer.Serialize(
                    new RemoteNotificationDismissal { Ids = [item.Notification.Id] },
                    RemoteJsonContext.Default.RemoteNotificationDismissal)));
        }
    }

    private void PublishDelivered() =>
        Volatile.Write(
            ref _delivered,
            _items.Values
                .Where(static item => item.IsDelivered)
                .OrderBy(static item => item.RaisedAt)
                .Select(static item => item.Notification)
                .ToArray());

    private bool IsChatOnScreen(Guid chatId) =>
        _main.ChatVM.IsChatOnScreen(chatId)
        || _main.ChatSurfaceRegistry.SnapshotSurfaces().Any(surface => surface.IsChatOnScreen(chatId));

    private static bool IsAnyLumiWindowActive() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && desktop.Windows.Any(static window => window.IsActive);

    // ── Building ────────────────────────────────────────────────────────────────────────────

    private RemoteNotification Build(Chat chat, ChatAttention attention)
    {
        var agent = chat.AgentId is { } agentId
            ? _dataStore.Data.Agents.FirstOrDefault(candidate => candidate.Id == agentId)?.Name
            : null;
        var notification = new RemoteNotification
        {
            Id = attention.Kind == ChatAttentionKind.Reply
                ? ReplyKey(chat.Id)
                : RequestKey(chat.Id, attention.RequestId ?? ""),
            Kind = attention.Kind switch
            {
                ChatAttentionKind.Question => RemoteProtocol.NotificationKinds.Question,
                ChatAttentionKind.Input => RemoteProtocol.NotificationKinds.Input,
                ChatAttentionKind.Approval => RemoteProtocol.NotificationKinds.Approval,
                _ => RemoteProtocol.NotificationKinds.Reply
            },
            ChatId = chat.Id,
            ChatTitle = PhoneNotificationText.Line(
                string.IsNullOrWhiteSpace(chat.Title) ? Loc.Get("Sidebar_NewChat") : chat.Title,
                RemoteProtocol.MobileNotificationTitleLimit),
            AgentName = agent is null ? null : PhoneNotificationText.Line(agent, RemoteProtocol.MobileNotificationTitleLimit),
            Text = PhoneNotificationText.FromMarkdown(attention.Text),
            CreatedAt = _time.GetUtcNow()
        };

        return notification;
    }

    private static RemoteEventFrame NotificationFrame(RemoteNotification notification) => new(
        RemoteProtocol.Events.Notification,
        JsonSerializer.Serialize(notification, RemoteJsonContext.Default.RemoteNotification));

    internal static string ReplyKey(Guid chatId) => $"reply:{chatId:N}";

    internal static string RequestKey(Guid chatId, string requestId) => $"ask:{chatId:N}:{requestId}";

    private static bool OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return true;

        Dispatcher.UIThread.Post(action);
        return false;
    }

    private sealed class Item(
        RemoteNotification notification,
        ChatAttentionKind kind,
        DateTimeOffset raisedAt,
        bool wasUnread)
    {
        public RemoteNotification Notification { get; } = notification;
        public ChatAttentionKind Kind { get; } = kind;
        public DateTimeOffset RaisedAt { get; } = raisedAt;

        /// <summary>The chat was marked unread when this happened, so reading it settles it.</summary>
        public bool WasUnread { get; } = wasUnread;

        /// <summary>A finished reply whose follow-up suggestions are still being written.</summary>
        public bool AwaitsSuggestions { get; set; }

        public bool IsDelivered { get; set; }
    }
}
