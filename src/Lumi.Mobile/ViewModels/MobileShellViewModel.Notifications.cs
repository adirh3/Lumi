using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// Settings → Notifications, and the offer to turn them on: the PC tells this phone about finished
/// replies and questions while the user is away from it. The platform shows them
/// (<see cref="IPhoneNotificationPlatform"/>): the app keeps a quiet background connection to the PC,
/// while a browser hands the PC a Web Push subscription, which this view model registers. The PC
/// decides when the user is away.
/// </summary>
public sealed partial class MobileShellViewModel
{
    private IPhoneNotificationPlatform? _notificationPlatform;
    private bool? _pcNotificationsEnabled;
    private int? _pcAwayMinutes;
    private string? _pcWebPushKey;
    private WebPushState _webPushState;
    private string? _webPushError;
    private WebPushSubscriptionKeys? _registeredWebPush;
    private Task _webPushSync = Task.CompletedTask;
    private bool _webPushSyncAgain;

    /// <summary>Where this browser's push subscription stands with the PC.</summary>
    private enum WebPushState
    {
        Off,

        /// <summary>Waiting to hear the PC's push key with its settings.</summary>
        Waiting,
        Registering,
        Registered,
        Failed,

        /// <summary>The PC's Lumi cannot push to browsers.</summary>
        PcUnsupported
    }

    /// <summary>The user wants this phone notified. Saved with the phone's settings.</summary>
    [ObservableProperty] private bool _notificationsEnabled;

    [ObservableProperty] private bool _isNotificationsPromptOpen;

    [ObservableProperty] private string _notificationTestMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestNotificationCommand))]
    private bool _isSendingTestNotification;

    private IPhoneNotificationPlatform NotificationPlatform =>
        _notificationPlatform ??= MobilePlatformServices.Notifications;

    /// <summary>This host can show notifications at all; the others leave the feature out.</summary>
    public bool IsNotificationsAvailable => NotificationPlatform.IsAvailable;

    /// <summary>Notifications can work here now. A browser may first need https or the Home Screen.</summary>
    public bool IsNotificationsSupported => NotificationPlatform.Support == PhoneNotificationSupport.Supported;

    /// <summary>The switch turns notifications on only where they can work, and always back off.</summary>
    public bool CanToggleNotifications => IsNotificationsSupported || NotificationsEnabled;

    /// <summary>What this browser needs before it can get notifications, in words.</summary>
    public string NotificationsSupportText => NotificationPlatform.Support switch
    {
        PhoneNotificationSupport.NeedsSecureAddress => Strings.Get("NotificationsNeedHttps", HostDisplayName),
        PhoneNotificationSupport.NeedsHomeScreen => Strings.Get("NotificationsNeedHomeScreen"),
        PhoneNotificationSupport.Unsupported => Strings.Get("NotificationsBrowserUnsupported"),
        _ => ""
    };

    /// <summary>Notifications are on, but the platform does not let Lumi show them.</summary>
    private bool IsNotificationsNotAllowed =>
        NotificationsEnabled && IsNotificationsSupported && !NotificationPlatform.AreNotificationsAllowed;

    /// <summary>
    /// Notifications are on but not allowed here yet, and one tap can ask. An app installed from the
    /// browser can start out this way even though its tab was allowed.
    /// </summary>
    public bool IsNotificationsPermissionNeeded =>
        IsNotificationsNotAllowed && NotificationPlatform.CanRequestPermission;

    /// <summary>Notifications are on, but the platform refuses them; only its settings can allow them.</summary>
    public bool IsNotificationsBlocked =>
        IsNotificationsNotAllowed && !NotificationPlatform.CanRequestPermission;

    public string NotificationsBlockedText => Strings.Get(
        !NotificationPlatform.UsesWebPush ? "NotificationsBlocked"
        : NotificationPlatform.IsInstalledApp ? "NotificationsBlockedInstalled"
        : "NotificationsBlockedWeb");

    /// <summary>The kinds, the connection and the test, which only mean something once allowed.</summary>
    public bool IsNotificationDetailsVisible => NotificationsEnabled && !IsNotificationsNotAllowed;

    /// <summary>Lumi can take the user to the platform's notification settings.</summary>
    public bool CanOpenNotificationSettings => NotificationPlatform.CanOpenSettings;

    /// <summary>The PC's own switch for phone notifications is off.</summary>
    public bool IsNotificationsOffOnPc => IsPaired && _pcNotificationsEnabled == false;

    /// <summary>The PC's Lumi is older than phone notifications, or than notifications in a browser.</summary>
    public bool IsNotificationsUnsupportedOnPc =>
        IsPaired
        && (Client.HasCompatibleBootstrap && !Client.SupportsPhoneNotifications
            || (NotificationPlatform.UsesWebPush
                ? _webPushState == WebPushState.PcUnsupported
                : NotificationPlatform.LinkState == PhoneNotificationLinkState.Unsupported));

    /// <summary>The PC won't send notifications now: its Lumi is older, or its own switch is off.</summary>
    public bool IsNotificationsPcNoticeVisible => IsNotificationsUnsupportedOnPc || IsNotificationsOffOnPc;

    public string NotificationsPcNoticeText => Strings.Get(
        IsNotificationsUnsupportedOnPc ? "NotificationsPcUpdateNeeded" : "NotificationsOffOnPc",
        HostDisplayName);

    /// <summary>On or Off, beside Notifications in the Settings list.</summary>
    public string NotificationsSummary => Strings.Get(NotificationsEnabled && !IsNotificationsNotAllowed ? "On" : "Off");

    public string NotificationsHeroText => Strings.Get("NotificationsSummary", HostDisplayName);

    /// <summary>How notifications reach this phone now, in words.</summary>
    public string NotificationsStatusText => NotificationPlatform.UsesWebPush
        ? _webPushState switch
        {
            WebPushState.Registered => Strings.Get("NotificationsWebReady", HostDisplayName),
            WebPushState.Registering => Strings.Get("NotificationsWebRegistering"),
            WebPushState.Failed => Strings.Get(
                "NotificationsWebFailed",
                _webPushError ?? Strings.Get("ConnectionClosed")),
            WebPushState.PcUnsupported => Strings.Get("NotificationsPcUpdateNeeded", HostDisplayName),
            _ => Strings.Get("NotificationWaiting", HostDisplayName)
        }
        : NotificationPlatform.LinkState switch
        {
            PhoneNotificationLinkState.Connected => Strings.Get("NotificationConnected", HostDisplayName),
            PhoneNotificationLinkState.Connecting => Strings.Get("NotificationConnecting", HostDisplayName),
            PhoneNotificationLinkState.Unsupported => Strings.Get("NotificationsPcUpdateNeeded", HostDisplayName),
            _ => Strings.Get("NotificationWaiting", HostDisplayName)
        };

    public bool IsNotificationLinkLive => NotificationPlatform.UsesWebPush
        ? _webPushState == WebPushState.Registered
        : NotificationPlatform.LinkState == PhoneNotificationLinkState.Connected;

    /// <summary>How notifications travel to this phone, under the page.</summary>
    public string NotificationsDeliveryNote =>
        Strings.Get(NotificationPlatform.UsesWebPush ? "NotificationsWebNote" : "NotificationsBackgroundNote");

    /// <summary>When the PC counts the user as away, as its own setting says.</summary>
    public string NotificationsAwayRule => Strings.Get(
        "NotificationsAwayRule",
        HostDisplayName,
        _pcAwayMinutes ?? 2);

    /// <summary>What the sheet offers: turning notifications on, allowing them, or the way to unblock them.</summary>
    private NotificationsPromptMode _notificationsPromptMode;

    /// <summary>The permission sheet came up once in this run; it does not come back until the next.</summary>
    private bool _notificationsPermissionPrompted;

    private enum NotificationsPromptMode
    {
        Offer,
        Allow,
        Blocked
    }

    public string NotificationsPromptTitle => Strings.Get(_notificationsPromptMode switch
    {
        NotificationsPromptMode.Allow => "NotificationsAllowTitle",
        NotificationsPromptMode.Blocked => "NotificationsBlockedTitle",
        _ => "NotificationsPromptTitle"
    });

    public string NotificationsPromptText => _notificationsPromptMode switch
    {
        NotificationsPromptMode.Allow => Strings.Get("NotificationsPermissionNeeded"),
        NotificationsPromptMode.Blocked => NotificationsBlockedText,
        _ => Strings.Get("NotificationsPromptText", HostDisplayName)
    };

    /// <summary>The sheet's main action. A browser that refused can only be changed in its own settings.</summary>
    public string NotificationsPromptAction => Strings.Get(_notificationsPromptMode switch
    {
        NotificationsPromptMode.Allow => "AllowNotifications",
        NotificationsPromptMode.Blocked => "OpenSystemSettings",
        _ => "TurnOnNotifications"
    });

    public bool HasNotificationsPromptAction =>
        _notificationsPromptMode != NotificationsPromptMode.Blocked || CanOpenNotificationSettings;

    public bool IsNotificationsPromptPreviewVisible => _notificationsPromptMode != NotificationsPromptMode.Blocked;

    /// <summary>Whether a kind of notification is on in the platform's settings, in words.</summary>
    public string QuestionsChannelState => ChannelState(PhoneNotificationChannel.Questions);

    public string RepliesChannelState => ChannelState(PhoneNotificationChannel.Replies);

    public bool HasNotificationChannelSettings => NotificationPlatform.HasChannelSettings;

    private string HostDisplayName => string.IsNullOrWhiteSpace(HostName) ? Strings.Get("YourPc") : HostName;

    private string ChannelState(PhoneNotificationChannel channel) =>
        Strings.Get(NotificationPlatform.IsChannelEnabled(channel) ? "On" : "Off");

    /// <summary>The browser's registration with the PC, for tests.</summary>
    internal Task WebPushSync => _webPushSync;

    private void InitializeNotifications()
    {
#pragma warning disable MVVMTK0034 // Loading, not changing: the platform is told below.
        _notificationsEnabled = _settings.NotificationsEnabled && IsNotificationsAvailable;
#pragma warning restore MVVMTK0034
        NotificationPlatform.LinkStateChanged += OnNotificationLinkStateChanged;
        if (NotificationsEnabled && IsPaired)
            ApplyNotificationConnection();
    }

    private void DisposeNotifications() =>
        NotificationPlatform.LinkStateChanged -= OnNotificationLinkStateChanged;

    private void OnNotificationLinkStateChanged() => _post(RefreshNotificationState);

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _settings.NotificationsEnabled = value;
        if (value)
            _settings.NotificationsPromptAnswered = true;
        _store.Save(_settings);
        NotificationTestMessage = "";
        _ = ApplyNotificationsChoiceAsync(value);
    }

    private async Task ApplyNotificationsChoiceAsync(bool enabled)
    {
        // A browser asks only in answer to the tap itself, so nothing may be awaited before this.
        if (enabled && !NotificationPlatform.AreNotificationsAllowed)
        {
            // This request is this run's question: coming back from the system's dialog, refused,
            // must not bring up the sheet that asks again.
            _notificationsPermissionPrompted = true;
            try
            {
                await NotificationPlatform.RequestPermissionAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Notifications] Permission request failed: {ex.Message}");
            }
        }

        // The choice may have changed while the permission dialog was up.
        ApplyNotificationConnection();
        RefreshNotificationState();
    }

    /// <summary>
    /// Brings delivery in line with the switch: the app runs its background connection exactly while
    /// notifications are on, allowed and paired; a browser registers its push subscription with the PC.
    /// </summary>
    private void ApplyNotificationConnection()
    {
        if (!IsNotificationsAvailable)
            return;

        if (NotificationPlatform.UsesWebPush)
        {
            SyncWebPush();
            return;
        }

        try
        {
            NotificationPlatform.Apply(NotificationsEnabled && IsPaired && NotificationPlatform.AreNotificationsAllowed);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notifications] Could not apply the background connection: {ex.Message}");
        }
    }

    /// <summary>One registration at a time; a change meanwhile runs it once more after.</summary>
    private void SyncWebPush()
    {
        if (!_webPushSync.IsCompleted)
        {
            _webPushSyncAgain = true;
            return;
        }

        _webPushSync = SyncWebPushUntilSettledAsync();
    }

    private async Task SyncWebPushUntilSettledAsync()
    {
        do
        {
            _webPushSyncAgain = false;
            await SyncWebPushOnceAsync();
        }
        while (_webPushSyncAgain);
    }

    private async Task SyncWebPushOnceAsync()
    {
        var wanted = NotificationsEnabled
                     && IsPaired
                     && IsNotificationsSupported
                     && NotificationPlatform.AreNotificationsAllowed;
        if (!wanted)
        {
            _registeredWebPush = null;
            SetWebPushState(WebPushState.Off);
            await ForgetWebPushAsync();
            return;
        }

        if (_pcWebPushKey is not { Length: > 0 } key)
        {
            // A PC that sent its notification settings without a key cannot push to browsers.
            SetWebPushState(_pcNotificationsEnabled is null ? WebPushState.Waiting : WebPushState.PcUnsupported);
            return;
        }

        WebPushSubscriptionKeys subscription;
        try
        {
            subscription = await NotificationPlatform.SubscribeAsync(key);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notifications] The browser could not subscribe to pushes: {ex.Message}");
            _registeredWebPush = null;
            _webPushError = Strings.Get("NotificationsBrowserFailed");
            SetWebPushState(WebPushState.Failed);
            return;
        }

        // Resuming checks the browser's subscription; the PC hears again only when it changed.
        if (_webPushState == WebPushState.Registered && subscription == _registeredWebPush)
            return;

        SetWebPushState(WebPushState.Registering);
        var result = await SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.RegisterWebPush)
            .With("endpoint", subscription.Endpoint)
            .With("p256dh", subscription.P256dh)
            .With("auth", subscription.Auth));
        _registeredWebPush = result.Ok ? subscription : null;
        _webPushError = result.Ok ? null : result.Error;
        SetWebPushState(result.Ok ? WebPushState.Registered : WebPushState.Failed);
    }

    /// <summary>
    /// Turned off here, the PC forgets this browser and the browser ends its subscription. Unpaired,
    /// the PC already forgot it. Blocked or unsupported, nothing is undone.
    /// </summary>
    private async Task ForgetWebPushAsync()
    {
        if (NotificationsEnabled && IsPaired)
            return;

        if (IsPaired)
            await SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.UnregisterWebPush));

        try
        {
            await NotificationPlatform.UnsubscribeAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notifications] The browser could not end its push subscription: {ex.Message}");
        }
    }

    private void SetWebPushState(WebPushState state)
    {
        if (_webPushState == state && state != WebPushState.Failed)
            return;

        _webPushState = state;
        RefreshNotificationState();
    }

    /// <summary>Re-reads what the platform and the PC say. Cheap; runs when Settings shows the page.</summary>
    internal void RefreshNotificationState()
    {
        // A sheet asking to allow or unblock notifications goes once that is no longer needed, as
        // when they were allowed in the system's settings meanwhile.
        if (IsNotificationsPromptOpen
            && _notificationsPromptMode != NotificationsPromptMode.Offer
            && !IsNotificationsNotAllowed)
        {
            IsNotificationsPromptOpen = false;
        }

        OnPropertyChanged(nameof(IsNotificationsAvailable));
        OnPropertyChanged(nameof(IsNotificationsSupported));
        OnPropertyChanged(nameof(CanToggleNotifications));
        OnPropertyChanged(nameof(NotificationsSupportText));
        OnPropertyChanged(nameof(IsNotificationsPermissionNeeded));
        OnPropertyChanged(nameof(IsNotificationsBlocked));
        OnPropertyChanged(nameof(NotificationsBlockedText));
        OnPropertyChanged(nameof(IsNotificationDetailsVisible));
        OnPropertyChanged(nameof(CanOpenNotificationSettings));
        OnPropertyChanged(nameof(IsNotificationsOffOnPc));
        OnPropertyChanged(nameof(IsNotificationsUnsupportedOnPc));
        OnPropertyChanged(nameof(IsNotificationsPcNoticeVisible));
        OnPropertyChanged(nameof(NotificationsPcNoticeText));
        OnPropertyChanged(nameof(NotificationsSummary));
        OnPropertyChanged(nameof(NotificationsHeroText));
        OnPropertyChanged(nameof(NotificationsStatusText));
        OnPropertyChanged(nameof(IsNotificationLinkLive));
        OnPropertyChanged(nameof(NotificationsDeliveryNote));
        OnPropertyChanged(nameof(NotificationsAwayRule));
        OnPropertyChanged(nameof(NotificationsPromptTitle));
        OnPropertyChanged(nameof(NotificationsPromptText));
        OnPropertyChanged(nameof(NotificationsPromptAction));
        OnPropertyChanged(nameof(HasNotificationsPromptAction));
        OnPropertyChanged(nameof(IsNotificationsPromptPreviewVisible));
        OnPropertyChanged(nameof(QuestionsChannelState));
        OnPropertyChanged(nameof(RepliesChannelState));
        OnPropertyChanged(nameof(HasNotificationChannelSettings));
        SendTestNotificationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The PC's own notification settings arrived with its settings.</summary>
    private void ApplyPcNotificationSettings(RemoteSettings settings)
    {
        if (settings.PhoneNotificationsEnabled is null && settings.PhoneNotificationAwayMinutes is null)
            return;

        _pcNotificationsEnabled = settings.PhoneNotificationsEnabled;
        _pcAwayMinutes = settings.PhoneNotificationAwayMinutes;
        var keyChanged = !string.Equals(_pcWebPushKey, settings.WebPushKey, StringComparison.Ordinal);
        _pcWebPushKey = settings.WebPushKey;
        if (keyChanged)
            _registeredWebPush = null;

        // A browser registers once it knows the PC's key, again if the key changed, and retries a
        // registration that failed while the PC was out of reach.
        if (NotificationPlatform.UsesWebPush
            && NotificationsEnabled
            && IsPaired
            && (keyChanged || _webPushState != WebPushState.Registered))
        {
            SyncWebPush();
        }

        RefreshNotificationState();
    }

    /// <summary>
    /// After connecting or coming back to the app, says once what the phone can do about
    /// notifications: offers them to someone who has not decided yet, and, once per run, asks again
    /// where they are on but not allowed — an app installed from the browser can start out that way —
    /// or says how to unblock them. Only where the phone can show them and the PC can send them.
    /// </summary>
    private void OfferNotificationsIfUseful()
    {
        if (!IsNotificationsAvailable
            || !IsNotificationsSupported
            || !IsPaired
            || !Client.SupportsPhoneNotifications
            || !_isApplicationActive
            || IsModalSheetPresented
            || IsNotificationsPromptOpen)
        {
            return;
        }

        NotificationsPromptMode mode;
        if (!NotificationsEnabled)
        {
            if (_settings.NotificationsPromptAnswered)
                return;
            mode = NotificationsPromptMode.Offer;
        }
        else if (!NotificationPlatform.AreNotificationsAllowed && !_notificationsPermissionPrompted)
        {
            _notificationsPermissionPrompted = true;
            mode = NotificationPlatform.CanRequestPermission
                ? NotificationsPromptMode.Allow
                : NotificationsPromptMode.Blocked;
        }
        else
        {
            return;
        }

        _notificationsPromptMode = mode;
        RefreshNotificationState();
        IsNotificationsPromptOpen = true;
    }

    [RelayCommand]
    private void TurnOnNotifications()
    {
        IsNotificationsPromptOpen = false;
        _settings.NotificationsPromptAnswered = true;
        if (_notificationsPromptMode == NotificationsPromptMode.Blocked)
        {
            OpenNotificationSettings(null);
            return;
        }

        if (NotificationsEnabled)
            _ = ApplyNotificationsChoiceAsync(true);
        else
            NotificationsEnabled = true;
    }

    /// <summary>Asks the platform again, from a tap, for notifications that are on but not allowed yet.</summary>
    [RelayCommand]
    private Task AllowNotificationsAsync() => ApplyNotificationsChoiceAsync(true);

    [RelayCommand]
    private void DismissNotificationsPrompt()
    {
        IsNotificationsPromptOpen = false;
        _settings.NotificationsPromptAnswered = true;
        _store.Save(_settings);
    }

    partial void OnIsNotificationsPromptOpenChanged(bool value)
    {
        // A sheet closed by dragging it down is an answer too.
        if (!value && !_settings.NotificationsPromptAnswered)
        {
            _settings.NotificationsPromptAnswered = true;
            _store.Save(_settings);
        }
    }

    [RelayCommand]
    private void OpenNotificationSettings(string? channel)
    {
        PhoneNotificationChannel? target = Enum.TryParse<PhoneNotificationChannel>(channel, ignoreCase: true, out var parsed)
            ? parsed
            : null;
        try
        {
            NotificationPlatform.OpenSettings(target);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Notifications] Could not open notification settings: {ex.Message}");
        }
    }

    private bool CanSendTestNotification() =>
        NotificationsEnabled && IsPaired && !IsSendingTestNotification;

    /// <summary>How long a test may take to reach a browser before it counts as lost.</summary>
    internal static TimeSpan TestDeliveryTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Asks the PC for a test notification, which travels the same way real ones do. A browser also
    /// says whether it arrived, because some accept a push subscription and then never deliver.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendTestNotification))]
    private async Task SendTestNotificationAsync()
    {
        IsSendingTestNotification = true;
        NotificationTestMessage = "";
        try
        {
            if (IsNotificationsNotAllowed)
            {
                NotificationTestMessage = Strings.Get(
                    "TestNotificationFailed",
                    IsNotificationsPermissionNeeded ? Strings.Get("NotificationsPermissionNeeded") : NotificationsBlockedText);
                return;
            }

            if (NotificationPlatform.UsesWebPush)
            {
                SyncWebPush();
                await _webPushSync;
                if (_webPushState != WebPushState.Registered)
                {
                    NotificationTestMessage = Strings.Get("TestNotificationFailed", _webPushError ?? NotificationsStatusText);
                    return;
                }
            }
            else if (NotificationPlatform.LinkState != PhoneNotificationLinkState.Connected)
            {
                ApplyNotificationConnection();
            }

            // Watching starts first: the push can arrive before the PC's answer does.
            var arrival = NotificationPlatform.ExpectTestNotificationAsync(TestDeliveryTimeout);
            var result = await SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.TestNotification));
            if (!result.Ok)
            {
                NotificationTestMessage = Strings.Get("TestNotificationFailed", result.Error ?? Strings.Get("ConnectionClosed"));
                return;
            }

            NotificationTestMessage = Strings.Get("TestNotificationSent");
            if (await arrival is { } arrived)
            {
                NotificationTestMessage = arrived
                    ? Strings.Get("TestNotificationDelivered")
                    : Strings.Get("TestNotificationNotReceived", HostDisplayName);
            }
        }
        finally
        {
            IsSendingTestNotification = false;
        }
    }

    /// <summary>
    /// Opens the chat a notification was about. The notification itself comes down at once; the PC
    /// hears the chat was read the moment the transcript shows.
    /// </summary>
    public void OpenChatFromNotification(Guid chatId, string? title)
    {
        if (!IsPaired || chatId == Guid.Empty)
            return;

        IsNotificationsPromptOpen = false;
        var known = ChatList.FindLoadedChat(chatId);
        OnChatActivated(chatId, known?.Title ?? title ?? Strings.Get("Chat"), known?.LastModelUsed);
    }
}