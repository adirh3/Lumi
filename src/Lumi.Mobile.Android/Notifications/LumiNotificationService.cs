using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using RemoteInput = AndroidX.Core.App.RemoteInput;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Android.Notifications;

/// <summary>
/// Keeps the quiet notification stream to the PC open while notifications are on: a foreground
/// service of Android's remote-messaging kind, with its own silent notice. Everything stays on the
/// private link to the PC; no push relay is involved.
/// </summary>
[Service(
    Name = "com.lumi.mobile.LumiNotificationService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeRemoteMessaging)]
public sealed class LumiNotificationService : Service
{
    public const string ActionStart = "com.lumi.mobile.notifications.START";

    /// <summary>The user answered a reply from its notification (<see cref="AndroidNotificationPresenter.ReplyInputKey"/>).</summary>
    public const string ActionReply = "com.lumi.mobile.notifications.REPLY";

    /// <summary>The notification answered, as JSON, so it can be shown again whatever this service remembers.</summary>
    internal const string NotificationExtra = "com.lumi.mobile.notification";

    /// <summary>One of Lumi's suggestions, picked in a reply's notification and sent as it is.</summary>
    internal const string SuggestionExtra = "com.lumi.mobile.suggestion";

    private const string Tag = "LumiNotifications";

    /// <summary>How long an answer from a notification may take to reach the PC.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(45);

    private static readonly object StateGate = new();
    private static PhoneNotificationLinkState _state = PhoneNotificationLinkState.Stopped;

    private PhoneNotificationListener? _listener;
    private LumiRemoteClient? _client;
    private AndroidNotificationPresenter? _presenter;
    private string _hostName = "";
    private string? _configuration;

    /// <summary>The stream's state, for Settings. Process-wide: the app and this service share a process.</summary>
    public static PhoneNotificationLinkState State
    {
        get
        {
            lock (StateGate)
                return _state;
        }
    }

    public static event Action? StateChanged;

    public static Intent StartIntent(Context context) =>
        new Intent(context, typeof(LumiNotificationService)).SetAction(ActionStart)!;

    /// <summary>Whether notifications are on, allowed and have a PC to come from.</summary>
    internal static bool CanRun(Context context, MobileConnectionSettings settings) =>
        settings is { NotificationsEnabled: true, BaseUrl.Length: > 0, Token.Length: > 0 }
        && NotificationManagerCompat.From(context)!.AreNotificationsEnabled();

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        LumiNotificationChannels.Ensure(this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var settings = new MobileSettingsStore().Load();
        _hostName = string.IsNullOrWhiteSpace(settings.HostName) ? Strings.Get("YourPc") : settings.HostName;
        _presenter ??= CreatePresenter();

        // Android requires the foreground promise to be kept at once, whatever comes next.
        if (!PromoteToForeground())
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == ActionReply)
            Reply(intent, settings);

        if (!CanRun(this, settings))
        {
            StopListening(clearShown: true);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        Listen(settings);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        StopListening(clearShown: true);
        SetState(PhoneNotificationLinkState.Stopped);
        base.OnDestroy();
    }

    private AndroidNotificationPresenter CreatePresenter()
    {
        var presenter = new AndroidNotificationPresenter(this, () => _hostName);
        presenter.LinkChanged += OnLinkChanged;
        return presenter;
    }

    private bool PromoteToForeground()
    {
        try
        {
            ServiceCompat.StartForeground(
                this,
                AndroidNotificationPresenter.ConnectionNotificationId,
                _presenter!.BuildConnection(State),
                OperatingSystem.IsAndroidVersionAtLeast(29) ? (int)ForegroundService.TypeRemoteMessaging : 0);
            return true;
        }
        catch (Exception ex)
        {
            // Android 12+ refuses a foreground start the user did not ask for while Lumi is in the background.
            global::Android.Util.Log.Warn(Tag, $"Could not start in the foreground: {ex.Message}");
            return false;
        }
    }

    /// <summary>Starts the stream for the paired PC, or restarts it when the PC changed.</summary>
    private void Listen(MobileConnectionSettings settings)
    {
        var configuration = $"{settings.BaseUrl}\n{settings.Token}\n{settings.DeviceId}";
        if (_listener is not null && configuration == _configuration)
            return;

        // Another PC's notifications, and its chats, have nothing to do with the new one.
        if (_configuration is not null)
        {
            StopListening(clearShown: true);
            LumiConversations.ForgetAll(this);
        }

        _configuration = configuration;
        _client = new LumiRemoteClient(settings.DeviceId, settings.DeviceName);
        _client.Configure(settings.BaseUrl, settings.Token);
        _listener = new PhoneNotificationListener(
            _client,
            _presenter!,
            AndroidPhoneNotificationPlatform.Ledger,
            MobilePlatformServices.NetworkMonitor);
        _listener.Start();
    }

    /// <summary>
    /// Sends what the user picked or typed in a reply's notification to its chat. What was sent shows
    /// under the notification at once, the way a messaging app's reply does; if it did not go, the
    /// notification says so and offers Reply again.
    /// </summary>
    private void Reply(Intent intent, MobileConnectionSettings settings)
    {
        var text = (RemoteInput.GetResultsFromIntent(intent)
                    ?.GetCharSequence(AndroidNotificationPresenter.ReplyInputKey)
                    ?.ToString()
                ?? intent.GetStringExtra(SuggestionExtra))
            ?.Trim();
        var notification = ReadNotification(intent);
        if (string.IsNullOrEmpty(text) || notification is not { ChatId: var chatId } || chatId == Guid.Empty
            || _presenter is not { } presenter)
        {
            return;
        }

        presenter.ShowReplied(notification, text);
        _ = Task.Run(async () =>
        {
            // Taken down meanwhile (the PC moved on, or notifications went off) needs no failure shown.
            if (!await SendAsync(settings, chatId, text).ConfigureAwait(false)
                && AndroidPhoneNotificationPlatform.Ledger.Contains(notification.Id))
            {
                presenter.ShowReplyFailed(notification, text);
            }
        });
    }

    private static RemoteNotification? ReadNotification(Intent intent)
    {
        try
        {
            return intent.GetStringExtra(NotificationExtra) is { } json
                ? JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteNotification)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends a message to the chat as the app does, over its own short connection.</summary>
    private static async Task<bool> SendAsync(MobileConnectionSettings settings, Guid chatId, string text)
    {
        var client = new LumiRemoteClient(settings.DeviceId, settings.DeviceName);
        try
        {
            client.Configure(settings.BaseUrl, settings.Token);
            using var timeout = new CancellationTokenSource(ReplyTimeout);
            await client.HelloAsync(settings.BaseUrl, timeout.Token).ConfigureAwait(false);
            if (!client.HasCompatibleBootstrap)
                return false;

            var result = await client.SendCommandAsync(
                    new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                        .With("message", text)
                        .With("chatId", chatId.ToString("D")),
                    timeout.Token)
                .ConfigureAwait(false);
            if (!result.Ok)
                global::Android.Util.Log.Warn(Tag, $"The PC did not take the answer: {result.Error}");
            return result.Ok;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, $"Could not send the answer: {ex.Message}");
            return false;
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void StopListening(bool clearShown)
    {
        var listener = _listener;
        var client = _client;
        _listener = null;
        _client = null;
        _configuration = null;
        if (listener is not null)
        {
            _ = Task.Run(async () =>
            {
                await listener.StopAsync().ConfigureAwait(false);
                if (client is not null)
                    await client.DisposeAsync().ConfigureAwait(false);
            });
        }

        if (clearShown && _presenter is { } presenter)
            presenter.Dismiss(AndroidPhoneNotificationPlatform.Ledger.TakeAll());
    }

    private void OnLinkChanged(PhoneNotificationLinkState state)
    {
        SetState(state);
        try
        {
            if (_presenter is { } presenter && state != PhoneNotificationLinkState.Stopped)
            {
                NotificationManagerCompat.From(this)!.Notify(
                    AndroidNotificationPresenter.ConnectionNotificationId,
                    presenter.BuildConnection(state));
            }
        }
        catch (Exception ex) when (ex is Java.Lang.SecurityException)
        {
            global::Android.Util.Log.Warn(Tag, $"Could not update the connection notice: {ex.Message}");
        }

        // The PC forgot this phone: nothing will come until it is paired again.
        if (state == PhoneNotificationLinkState.Unpaired)
        {
            new Handler(Looper.MainLooper!).Post(() =>
            {
                StopListening(clearShown: true);
                LumiConversations.ForgetAll(this);
                StopSelf();
            });
        }
    }

    private static void SetState(PhoneNotificationLinkState state)
    {
        lock (StateGate)
        {
            if (_state == state)
                return;
            _state = state;
        }

        StateChanged?.Invoke();
    }
}
