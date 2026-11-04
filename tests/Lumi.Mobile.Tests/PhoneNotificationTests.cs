using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The phone's notification stream: what the PC lists is shown once, what it takes down comes down,
/// a reconnect never rings twice, and an unpaired or older PC is told apart from a passing outage.
/// </summary>
public sealed class PhoneNotificationListenerTests
{
    private const string Epoch = "run-1";

    [Fact]
    public async Task WhatThePcListsIsShownOnceAndOnlyTheNewestRings()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch, Reply("a", 1), Reply("b", 2)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 2);
        Assert.Equal(new[] { ("a", false), ("b", true) }, rig.Presenter.Shown.Select(shown => (shown.Notification.Id, shown.Alert)));

        // The same list again, as after a reconnect, shows nothing new.
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch, Reply("a", 1), Reply("b", 2)));
        await rig.SendAsync(RemoteProtocol.Events.Notification, Json(Reply("c", 3)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 3);
        Assert.Equal(("c", true), (rig.Presenter.Shown[2].Notification.Id, rig.Presenter.Shown[2].Alert));
    }

    [Fact]
    public async Task ASyncTakesDownWhatTheSameRunNoLongerListsButKeepsAnEarlierRunsOnes()
    {
        var ledger = new PhoneNotificationLedger(null);
        ledger.Record(Reply("old-run", 0), "run-0");
        await using var rig = await Rig.StartAsync(ledger);
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch, Reply("a", 1), Reply("b", 2)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 2);

        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch, Reply("b", 2)));
        await WaitAsync(() => rig.Presenter.Dismissed.Contains("a"));
        Assert.DoesNotContain("old-run", rig.Presenter.Dismissed);
        Assert.True(ledger.Contains("old-run"));
        Assert.False(ledger.Contains("a"));
    }

    [Fact]
    public async Task AChatsNextReplyRingsWhileThePhoneStillHoldsItsLastOne()
    {
        // A chat's replies share an id, and the phone still holds the last one, from before the PC restarted.
        var chatId = Guid.NewGuid();
        var ledger = new PhoneNotificationLedger(null);
        ledger.Record(ChatReply(chatId, minute: 0), "run-0");
        await using var rig = await Rig.StartAsync(ledger);
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch));

        await rig.SendAsync(RemoteProtocol.Events.Notification, Json(ChatReply(chatId, minute: 5)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 1);
        Assert.True(rig.Presenter.Shown[0].Alert);

        // The next one went out while the phone was away: it comes with the next connection's list.
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch, ChatReply(chatId, minute: 9)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 2);
        Assert.True(rig.Presenter.Shown[1].Alert);

        // The same one again stays quiet.
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch, ChatReply(chatId, minute: 9)));
        await rig.SendAsync(RemoteProtocol.Events.Notification, Json(ChatReply(chatId, minute: 9)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 3);
        Assert.False(rig.Presenter.Shown[2].Alert);
    }

    [Fact]
    public async Task DismissalsTakeNotificationsDownAndForgetThem()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(RemoteProtocol.Events.NotificationSync, Sync(Epoch));
        await rig.SendAsync(RemoteProtocol.Events.Notification, Json(Reply("a", 1)));
        await WaitAsync(() => rig.Presenter.Shown.Count == 1);

        await rig.SendAsync(
            RemoteProtocol.Events.NotificationsDismissed,
            JsonSerializer.Serialize(new RemoteNotificationDismissal { Ids = ["a"] }, RemoteJsonContext.Default.RemoteNotificationDismissal));
        await WaitAsync(() => rig.Presenter.Dismissed.Contains("a"));
        Assert.False(rig.Ledger.Contains("a"));
    }

    [Fact]
    public async Task ATestRingsEveryTimeAndIsNotRemembered()
    {
        await using var rig = await Rig.StartAsync();
        var test = new RemoteNotification { Id = "test:1", Kind = RemoteProtocol.NotificationKinds.Test };
        await rig.SendAsync(RemoteProtocol.Events.Notification, Json(test));
        await rig.SendAsync(RemoteProtocol.Events.Notification, Json(test));
        await WaitAsync(() => rig.Presenter.Shown.Count == 2);

        Assert.All(rig.Presenter.Shown, shown => Assert.True(shown.Alert));
        Assert.False(rig.Ledger.Contains("test:1"));
    }

    [Fact]
    public async Task APhoneThePcForgotStopsListening()
    {
        await using var rig = await Rig.StartAsync(status: HttpStatusCode.Unauthorized);
        await WaitAsync(() => rig.Listener.State == PhoneNotificationLinkState.Unpaired);
        await Task.Delay(200);
        Assert.Equal(1, rig.Handler.Requests);
    }

    [Fact]
    public async Task AnOlderPcIsReportedInsteadOfRetriedAtOnce()
    {
        await using var rig = await Rig.StartAsync(status: HttpStatusCode.NotFound);
        await WaitAsync(() => rig.Listener.State == PhoneNotificationLinkState.Unsupported);
        await Task.Delay(200);
        Assert.Equal(1, rig.Handler.Requests);
    }

    [Fact]
    public async Task AClosedStreamComesBackWhenTheNetworkDoes()
    {
        await using var rig = await Rig.StartAsync();
        rig.Network.Report(false);
        rig.CurrentPipe.Writer.Complete();
        await WaitAsync(() => rig.Listener.State == PhoneNotificationLinkState.Waiting);
        await Task.Delay(200);
        Assert.Equal(1, rig.Handler.Requests);

        rig.Network.Report(true);
        await WaitAsync(() => rig.Handler.Requests == 2 && rig.Listener.State == PhoneNotificationLinkState.Connected);
    }

    [Fact]
    public void TheLedgerSurvivesARestartAndForgetsWhatIsOld()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Lumi.Mobile.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var time = new ManualTimeProvider();
            var chatId = Guid.NewGuid();
            var ledger = new PhoneNotificationLedger(folder, time);
            ledger.Record(new RemoteNotification { Id = "a", ChatId = chatId }, Epoch);
            ledger.Record(new RemoteNotification { Id = "b", ChatId = Guid.NewGuid() }, Epoch);

            var reopened = new PhoneNotificationLedger(folder, time);
            Assert.True(reopened.Contains("a"));
            Assert.Equal(new[] { "a" }, reopened.TakeChat(chatId));
            Assert.False(new PhoneNotificationLedger(folder, time).Contains("a"));

            time.Advance(PhoneNotificationLedger.Retention + TimeSpan.FromMinutes(1));
            Assert.False(new PhoneNotificationLedger(folder, time).Contains("b"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static RemoteNotification Reply(string id, int minute) => new()
    {
        Id = id,
        Kind = RemoteProtocol.NotificationKinds.Reply,
        ChatId = Guid.NewGuid(),
        ChatTitle = "Chat " + id,
        Text = "Done",
        CreatedAt = new DateTimeOffset(2026, 11, 1, 12, minute, 0, TimeSpan.Zero)
    };

    /// <summary>A reply in <paramref name="chatId"/>, with the id the PC gives every reply in a chat.</summary>
    private static RemoteNotification ChatReply(Guid chatId, int minute) => new()
    {
        Id = $"reply:{chatId:N}",
        Kind = RemoteProtocol.NotificationKinds.Reply,
        ChatId = chatId,
        ChatTitle = "Chat",
        Text = $"Done at {minute}",
        CreatedAt = new DateTimeOffset(2026, 11, 1, 12, minute, 0, TimeSpan.Zero)
    };

    private static string Json(RemoteNotification notification) =>
        JsonSerializer.Serialize(notification, RemoteJsonContext.Default.RemoteNotification);

    private static string Sync(string epoch, params RemoteNotification[] notifications) =>
        JsonSerializer.Serialize(
            new RemoteNotificationSync { Epoch = epoch, Notifications = [.. notifications] },
            RemoteJsonContext.Default.RemoteNotificationSync);

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out.");
            await Task.Delay(10);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(
            NotificationStreamHandler handler,
            PhoneNotificationListener listener,
            RecordingPresenter presenter,
            PhoneNotificationLedger ledger,
            PushedNetworkMonitor network,
            LumiRemoteClient client)
        {
            Handler = handler;
            Listener = listener;
            Presenter = presenter;
            Ledger = ledger;
            Network = network;
            Client = client;
        }

        public NotificationStreamHandler Handler { get; }
        public PhoneNotificationListener Listener { get; }
        public RecordingPresenter Presenter { get; }
        public PhoneNotificationLedger Ledger { get; }
        public PushedNetworkMonitor Network { get; }
        private LumiRemoteClient Client { get; }
        public Pipe CurrentPipe { get; private set; } = new();

        public static async Task<Rig> StartAsync(
            PhoneNotificationLedger? ledger = null,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            var handler = new NotificationStreamHandler { Status = status };
            var client = new LumiRemoteClient("phone", "Phone", handler);
            client.Configure("http://127.0.0.1:47653", "token");
            var presenter = new RecordingPresenter();
            ledger ??= new PhoneNotificationLedger(null);
            var network = new PushedNetworkMonitor();
            var listener = new PhoneNotificationListener(client, presenter, ledger, network, random: () => 0.5);
            var rig = new Rig(handler, listener, presenter, ledger, network, client);
            listener.Start();
            if (status == HttpStatusCode.OK)
                rig.CurrentPipe = await handler.Opened.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return rig;
        }

        public async Task SendAsync(string eventName, string json)
        {
            await CurrentPipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(new RemoteEventFrame(eventName, json).ToWire()));
            await CurrentPipe.Writer.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Listener.StopAsync();
            await Client.DisposeAsync();
        }
    }

    private sealed class NotificationStreamHandler : HttpMessageHandler
    {
        private readonly Channel<Pipe> _opened = Channel.CreateUnbounded<Pipe>();
        private int _requests;

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public int Requests => Volatile.Read(ref _requests);

        public ChannelReader<Pipe> Opened => _opened.Reader;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            Assert.Equal(RemoteProtocol.Routes.Notifications, request.RequestUri!.AbsolutePath);
            if (Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Status));

            var pipe = new Pipe();
            _opened.Writer.TryWrite(pipe);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream())
            });
        }
    }

    internal sealed class RecordingPresenter : IPhoneNotificationPresenter
    {
        private readonly ConcurrentQueue<(RemoteNotification Notification, bool Alert)> _shown = new();
        private readonly ConcurrentQueue<string> _dismissed = new();

        public IReadOnlyList<(RemoteNotification Notification, bool Alert)> Shown => [.. _shown];

        public IReadOnlyList<string> Dismissed => [.. _dismissed];

        public void Show(RemoteNotification notification, bool alert) => _shown.Enqueue((notification, alert));

        public void Dismiss(IReadOnlyCollection<string> ids)
        {
            foreach (var id in ids)
                _dismissed.Enqueue(id);
        }

        public void LinkStateChanged(PhoneNotificationLinkState state)
        {
        }
    }
}

/// <summary>
/// Settings → Notifications and the one-time offer: turning them on asks the platform for permission,
/// runs the background connection only while allowed and paired, and opening a notification opens
/// its chat.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileNotificationSettingsTests
{
    [Fact]
    public async Task TurningNotificationsOnAsksOnceAndStartsTheConnection()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform { GrantOnRequest = true };
        await WithPlatformAsync(platform, async () =>
        {
            var store = PairedStore(session);
            await using (var shell = new MobileShellViewModel(store: store, post: action => action()))
            {
                Assert.True(shell.IsNotificationsAvailable);
                Assert.False(shell.NotificationsEnabled);
                Assert.Equal("Off", shell.NotificationsSummary);

                shell.NotificationsEnabled = true;

                Assert.Equal(1, platform.PermissionRequests);
                Assert.True(platform.Applied.Last());
                Assert.False(shell.IsNotificationsBlocked);
                Assert.Equal("On", shell.NotificationsSummary);
            }

            // Remembered: the next launch starts the connection without asking again.
            platform.Applied.Clear();
            await using (var reopened = new MobileShellViewModel(store: store, post: action => action()))
            {
                Assert.True(reopened.NotificationsEnabled);
                Assert.Equal(new[] { true }, platform.Applied);
                Assert.Equal(1, platform.PermissionRequests);

                reopened.NotificationsEnabled = false;
                Assert.False(platform.Applied.Last());
            }

            Assert.False(store.Load().NotificationsEnabled);
            Assert.True(store.Load().NotificationsPromptAnswered);
        });
    }

    [Fact]
    public async Task ARefusedPermissionShowsTheWayToTheSystemSettings()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform { GrantOnRequest = false };
        await WithPlatformAsync(platform, async () =>
        {
            await using var shell = new MobileShellViewModel(store: PairedStore(session), post: action => action());
            shell.NotificationsEnabled = true;

            Assert.True(shell.IsNotificationsBlocked);
            Assert.False(platform.Applied.Last());
            Assert.Equal("Off", shell.NotificationsSummary);

            shell.OpenNotificationSettingsCommand.Execute(null);
            shell.OpenNotificationSettingsCommand.Execute("Replies");
            Assert.Equal(new PhoneNotificationChannel?[] { null, PhoneNotificationChannel.Replies }, platform.Opened);
        });
    }

    [Fact]
    public async Task OpeningANotificationOpensItsChatAndTakesItsNotificationsDown()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform();
        await WithPlatformAsync(platform, async () =>
        {
            await using var shell = new MobileShellViewModel(store: PairedStore(session), post: action => action());
            var chatId = Guid.NewGuid();

            shell.OpenChatFromNotification(chatId, "Trip plan");

            Assert.Equal(chatId, shell.Chat.ChatId);
            Assert.Equal("Trip plan", shell.Chat.Title);
            Assert.Equal(MobilePage.Chat, shell.Page);
            Assert.Contains(chatId, platform.Cleared);
        });
    }

    [Fact]
    public async Task HostsWithoutNotificationsLeaveTheFeatureOut()
    {
        using var session = HeadlessMobileSession.Start();
        await using var shell = new MobileShellViewModel(store: PairedStore(session), post: action => action());

        Assert.False(shell.IsNotificationsAvailable);
        shell.NotificationsEnabled = true;
        Assert.False(shell.IsNotificationsPromptOpen);
    }

    [Fact]
    public async Task APcThatCanSendThemOffersNotificationsOnceAfterPairing()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform { GrantOnRequest = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = new FakeLumiDesktop();
            desktop.Capabilities.Add(RemoteProtocol.Capabilities.PhoneNotificationsV1);
            desktop.Start();
            var store = session.NewStore();
            await using (var shell = NewShell(store))
            {
                await PairAsync(shell, desktop);
                Assert.True(shell.IsNotificationsPromptOpen);
                Assert.Contains("TEST-PC", shell.NotificationsPromptText);

                shell.TurnOnNotificationsCommand.Execute(null);

                Assert.False(shell.IsNotificationsPromptOpen);
                Assert.True(shell.NotificationsEnabled);
                Assert.Equal(1, platform.PermissionRequests);
                Assert.True(platform.Applied.Last());
            }

            // Answered: reconnecting later does not ask again.
            await using var reopened = NewShell(store);
            var bootstraps = reopened.BootstrapSnapshotCount;
            await reopened.StartAsync();
            await WaitAsync(() => reopened.BootstrapSnapshotCount > bootstraps);
            Assert.False(reopened.IsNotificationsPromptOpen);
        });
    }

    [Fact]
    public async Task NotNowIsAnAnswerAndAnOlderPcIsNeverOffered()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform();
        await WithPlatformAsync(platform, async () =>
        {
            await using (var older = new FakeLumiDesktop())
            {
                older.Start();
                await using var shell = NewShell(session.NewStore());
                await PairAsync(shell, older);
                Assert.False(shell.IsNotificationsPromptOpen);
                Assert.True(shell.IsNotificationsUnsupportedOnPc);
                // Settings says why nothing will come, before anything is turned on.
                Assert.True(shell.IsNotificationsPcNoticeVisible);
                Assert.Equal(Strings.Get("NotificationsPcUpdateNeeded", "TEST-PC"), shell.NotificationsPcNoticeText);
            }

            await using var desktop = new FakeLumiDesktop();
            desktop.Capabilities.Add(RemoteProtocol.Capabilities.PhoneNotificationsV1);
            desktop.Start();
            var store = session.NewStore();
            await using var declined = NewShell(store);
            await PairAsync(declined, desktop);
            Assert.False(declined.IsNotificationsPcNoticeVisible);
            Assert.True(declined.IsNotificationsPromptOpen);

            declined.DismissNotificationsPromptCommand.Execute(null);

            Assert.False(declined.IsNotificationsPromptOpen);
            Assert.False(declined.NotificationsEnabled);
            Assert.True(store.Load().NotificationsPromptAnswered);
            Assert.Equal(0, platform.PermissionRequests);
            Assert.DoesNotContain(true, platform.Applied);
        });
    }

    private static MobileShellViewModel NewShell(MobileSettingsStore store) => new(
        new LumiRemoteClient("notification-test", "Test phone"),
        new LumiDiscoveryClient(),
        store,
        action => action());

    // ── In a browser: Web Push instead of a background connection ──────────────────────────────

    [Fact]
    public async Task ABrowserHandsThePcItsPushSubscriptionOnceAndNotAgainUntilItChanges()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { GrantOnRequest = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);
            Assert.True(shell.IsNotificationsPromptOpen);

            shell.TurnOnNotificationsCommand.Execute(null);
            await shell.WebPushSync;

            Assert.Equal(1, platform.PermissionRequests);
            Assert.Equal(["BPcKey"], platform.SubscribedWith);
            var register = Assert.Single(Commands(desktop, RemoteProtocol.Actions.RegisterWebPush));
            Assert.Equal(platform.Subscription.Endpoint, register.Get("endpoint"));
            Assert.Equal(platform.Subscription.P256dh, register.Get("p256dh"));
            Assert.Equal(platform.Subscription.Auth, register.Get("auth"));
            Assert.True(shell.IsNotificationLinkLive);
            Assert.Equal(Strings.Get("NotificationsWebReady", "TEST-PC"), shell.NotificationsStatusText);
            Assert.Equal(Strings.Get("NotificationsWebNote"), shell.NotificationsDeliveryNote);
            Assert.False(shell.HasNotificationChannelSettings);
            Assert.False(shell.CanOpenNotificationSettings);
            // A browser keeps no connection of its own.
            Assert.Empty(platform.Applied);

            // Coming back to the app checks the subscription; the PC hears only of a change.
            await shell.NotifyApplicationActivatedAsync();
            await shell.WebPushSync;
            Assert.Single(Commands(desktop, RemoteProtocol.Actions.RegisterWebPush));

            platform.Subscription = platform.Subscription with { Endpoint = "https://fcm.googleapis.com/fcm/send/renewed" };
            await shell.NotifyApplicationActivatedAsync();
            await shell.WebPushSync;
            Assert.Equal(
                "https://fcm.googleapis.com/fcm/send/renewed",
                Commands(desktop, RemoteProtocol.Actions.RegisterWebPush).Last().Get("endpoint"));

            var tests = Commands(desktop, RemoteProtocol.Actions.TestNotification).Count;
            await shell.SendTestNotificationCommand.ExecuteAsync(null);
            Assert.Equal(tests + 1, Commands(desktop, RemoteProtocol.Actions.TestNotification).Count);
            Assert.Equal(Strings.Get("TestNotificationDelivered"), shell.NotificationTestMessage);
        });
    }

    [Fact]
    public async Task AnAppInstalledFromTheBrowserThatStartsOutNotAllowedAsksAgainWithOneTap()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { GrantOnRequest = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            var store = session.NewStore();
            await using (var tab = NewShell(store))
            {
                await PairAsync(tab, desktop);
                tab.TurnOnNotificationsCommand.Execute(null);
                await tab.WebPushSync;
                Assert.True(tab.IsNotificationLinkLive);
            }

            // Installed from the browser: the same pairing and choice, but the app starts out not allowed.
            platform.Permission = "default";
            platform.IsInstalledApp = true;
            var requests = platform.PermissionRequests;
            await using var installed = NewShell(store);
            var bootstraps = installed.BootstrapSnapshotCount;
            await installed.StartAsync();
            await WaitAsync(() => installed.BootstrapSnapshotCount > bootstraps);

            Assert.True(installed.NotificationsEnabled);
            Assert.True(installed.IsNotificationsPromptOpen);
            Assert.Equal(Strings.Get("NotificationsAllowTitle"), installed.NotificationsPromptTitle);
            Assert.Equal(Strings.Get("NotificationsPermissionNeeded"), installed.NotificationsPromptText);
            Assert.Equal(Strings.Get("AllowNotifications"), installed.NotificationsPromptAction);
            Assert.True(installed.HasNotificationsPromptAction);
            Assert.True(installed.IsNotificationsPermissionNeeded);
            Assert.False(installed.IsNotificationsBlocked);
            Assert.False(installed.IsNotificationDetailsVisible);
            Assert.Equal("Off", installed.NotificationsSummary);
            // A browser only shows its prompt in answer to a tap, so nothing is asked before one.
            Assert.Equal(requests, platform.PermissionRequests);

            installed.TurnOnNotificationsCommand.Execute(null);
            await installed.WebPushSync;

            Assert.False(installed.IsNotificationsPromptOpen);
            Assert.Equal(requests + 1, platform.PermissionRequests);
            Assert.True(installed.IsNotificationDetailsVisible);
            Assert.True(installed.IsNotificationLinkLive);
            Assert.Equal("On", installed.NotificationsSummary);
        });
    }

    [Fact]
    public async Task ABlockedAppSaysOnceARunHowToUnblockItAndItsTestSaysWhy()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            var store = session.NewStore();
            await using (var tab = NewShell(store))
            {
                await PairAsync(tab, desktop);
                tab.NotificationsEnabled = true;
                await tab.WebPushSync;
            }

            platform.Permission = "denied";
            platform.IsInstalledApp = true;
            await using var installed = NewShell(store);
            var bootstraps = installed.BootstrapSnapshotCount;
            await installed.StartAsync();
            await WaitAsync(() => installed.BootstrapSnapshotCount > bootstraps);

            Assert.True(installed.IsNotificationsPromptOpen);
            Assert.Equal(Strings.Get("NotificationsBlockedTitle"), installed.NotificationsPromptTitle);
            Assert.Equal(Strings.Get("NotificationsBlockedInstalled"), installed.NotificationsPromptText);
            // A page can't open its browser's settings, so the sheet only explains.
            Assert.False(installed.HasNotificationsPromptAction);
            Assert.False(installed.IsNotificationsPromptPreviewVisible);
            Assert.True(installed.IsNotificationsBlocked);
            Assert.False(installed.IsNotificationsPermissionNeeded);
            Assert.False(installed.IsNotificationDetailsVisible);

            installed.DismissNotificationsPromptCommand.Execute(null);
            await installed.NotifyApplicationActivatedAsync();
            Assert.False(installed.IsNotificationsPromptOpen);

            var tests = Commands(desktop, RemoteProtocol.Actions.TestNotification).Count;
            await installed.SendTestNotificationCommand.ExecuteAsync(null);
            Assert.Equal(
                Strings.Get("TestNotificationFailed", Strings.Get("NotificationsBlockedInstalled")),
                installed.NotificationTestMessage);
            Assert.Equal(tests, Commands(desktop, RemoteProtocol.Actions.TestNotification).Count);
            Assert.Equal(0, platform.PermissionRequests);

            // In a browser tab the way out is the site's settings.
            platform.IsInstalledApp = false;
            installed.RefreshNotificationState();
            Assert.Equal(Strings.Get("NotificationsBlockedWeb"), installed.NotificationsBlockedText);
        });
    }

    [Fact]
    public async Task AnAndroidAppWhoseNotificationsWereBlockedSaysSoOnLaunchWithTheWayOut()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform { GrantOnRequest = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = new FakeLumiDesktop();
            desktop.Capabilities.Add(RemoteProtocol.Capabilities.PhoneNotificationsV1);
            desktop.Start();
            var store = session.NewStore();
            await using (var shell = NewShell(store))
            {
                await PairAsync(shell, desktop);
                shell.TurnOnNotificationsCommand.Execute(null);
                Assert.True(platform.Allowed);
            }

            // Blocked afterwards in Android's settings.
            platform.Allowed = false;
            await using var reopened = NewShell(store);
            var bootstraps = reopened.BootstrapSnapshotCount;
            await reopened.StartAsync();
            await WaitAsync(() => reopened.BootstrapSnapshotCount > bootstraps);

            Assert.True(reopened.IsNotificationsPromptOpen);
            Assert.Equal(Strings.Get("NotificationsBlockedTitle"), reopened.NotificationsPromptTitle);
            Assert.Equal(Strings.Get("NotificationsBlocked"), reopened.NotificationsPromptText);
            Assert.Equal(Strings.Get("OpenSystemSettings"), reopened.NotificationsPromptAction);
            Assert.True(reopened.HasNotificationsPromptAction);

            reopened.TurnOnNotificationsCommand.Execute(null);

            Assert.False(reopened.IsNotificationsPromptOpen);
            Assert.Equal(new PhoneNotificationChannel?[] { null }, platform.Opened);
        });
    }

    [Fact]
    public async Task RefusingAndroidsOwnDialogDoesNotBringUpTheBlockedSheet()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform { GrantOnRequest = false };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = new FakeLumiDesktop();
            desktop.Capabilities.Add(RemoteProtocol.Capabilities.PhoneNotificationsV1);
            desktop.Start();
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);
            Assert.True(shell.IsNotificationsPromptOpen);

            // Android's permission dialog pauses Lumi, and "Don't allow" brings it back.
            shell.TurnOnNotificationsCommand.Execute(null);
            await shell.NotifyApplicationDeactivatedAsync();
            await shell.NotifyApplicationActivatedAsync();

            Assert.Equal(1, platform.PermissionRequests);
            Assert.False(shell.IsNotificationsPromptOpen);
            // Settings still shows the way out.
            Assert.True(shell.IsNotificationsBlocked);
        });
    }

    [Fact]
    public async Task ASheetAskingForPermissionGoesOnceItIsAllowedElsewhere()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeNotificationPlatform { GrantOnRequest = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = new FakeLumiDesktop();
            desktop.Capabilities.Add(RemoteProtocol.Capabilities.PhoneNotificationsV1);
            desktop.Start();
            var store = session.NewStore();
            await using (var shell = NewShell(store))
            {
                await PairAsync(shell, desktop);
                shell.TurnOnNotificationsCommand.Execute(null);
            }

            platform.Allowed = false;
            await using var reopened = NewShell(store);
            var bootstraps = reopened.BootstrapSnapshotCount;
            await reopened.StartAsync();
            await WaitAsync(() => reopened.BootstrapSnapshotCount > bootstraps);
            Assert.True(reopened.IsNotificationsPromptOpen);

            // Allowed in Android's settings without the sheet's button, then back to Lumi.
            await reopened.NotifyApplicationDeactivatedAsync();
            platform.Allowed = true;
            await reopened.NotifyApplicationActivatedAsync();

            Assert.False(reopened.IsNotificationsPromptOpen);
            Assert.Empty(platform.Opened);
            Assert.Equal("On", reopened.NotificationsSummary);
        });
    }

    [Fact]
    public async Task ABrowsersTestSaysWhetherItArrived()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);
            shell.NotificationsEnabled = true;
            await shell.WebPushSync;

            await shell.SendTestNotificationCommand.ExecuteAsync(null);
            Assert.Equal(Strings.Get("TestNotificationDelivered"), shell.NotificationTestMessage);

            // Some browsers accept a push subscription and then never deliver.
            platform.TestArrives = false;
            await shell.SendTestNotificationCommand.ExecuteAsync(null);
            Assert.Equal(Strings.Get("TestNotificationNotReceived", "TEST-PC"), shell.NotificationTestMessage);
            Assert.Equal(2, platform.TestsExpected);
            Assert.Equal(2, Commands(desktop, RemoteProtocol.Actions.TestNotification).Count);

            // A PC that refuses says so; nothing waits on a test that was never sent.
            desktop.CommandResultFactory = command => command?.Action == RemoteProtocol.Actions.TestNotification
                ? new RemoteCommandResult { Ok = false, Error = "No phone is listening." }
                : new RemoteCommandResult { Ok = true };
            await shell.SendTestNotificationCommand.ExecuteAsync(null);
            Assert.Equal(Strings.Get("TestNotificationFailed", "No phone is listening."), shell.NotificationTestMessage);
        });
    }

    [Fact]
    public async Task ASuggestionPickedInABrowsersNotificationIsSentInItsChat()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            var chatId = Guid.NewGuid();
            await using var desktop = WebPushDesktop();
            desktop.Transcript = new RemoteTranscript { ChatId = chatId, Title = "Trip plan", Revision = 1 };
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);

            shell.SendFromNotification(chatId, "  Book the Dan ");

            await WaitAsync(() => Commands(desktop, RemoteProtocol.Actions.SendMessage).Count == 1);
            var sent = Commands(desktop, RemoteProtocol.Actions.SendMessage)[0];
            Assert.Equal("Book the Dan", sent.Get("message"));
            Assert.Equal(chatId.ToString(), sent.Get("chatId"));
            Assert.Equal(chatId, shell.Chat.ChatId);
            Assert.Equal("", shell.Chat.PromptText);
        });
    }

    [Fact]
    public async Task APickedSuggestionNeverSendsOrReplacesWhatTheUserWrote()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            var chatId = Guid.NewGuid();
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var desktop = WebPushDesktop();
            desktop.TranscriptResponseFactory = async _ =>
            {
                await loaded.Task;
                return new RemoteTranscript { ChatId = chatId, Title = "Trip plan", Revision = 1 };
            };
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);

            // The chat is still loading: what will go shows in the composer, where it can be changed.
            shell.SendFromNotification(chatId, "Book the Dan");
            Assert.Equal("Book the Dan", shell.Chat.PromptText);
            shell.Chat.PromptText = "Book the Dan for two nights";

            loaded.SetResult();
            await WaitAsync(() => !shell.Chat.IsLoading);
            await Task.Delay(200);
            Assert.Empty(Commands(desktop, RemoteProtocol.Actions.SendMessage));
            Assert.Equal("Book the Dan for two nights", shell.Chat.PromptText);

            // A draft waiting in the chat stays as the user left it; the suggestion only opens the chat.
            shell.SendFromNotification(chatId, "Ship it");
            await Task.Delay(200);
            Assert.Empty(Commands(desktop, RemoteProtocol.Actions.SendMessage));
            Assert.Equal("Book the Dan for two nights", shell.Chat.PromptText);
        });
    }

    [Fact]
    public async Task ABrowserRegistersAgainOnEachLaunchOnceThePcsKeyArrives()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            var store = session.NewStore();
            await using (var shell = NewShell(store))
            {
                await PairAsync(shell, desktop);
                shell.NotificationsEnabled = true;
                await shell.WebPushSync;
            }

            await using var relaunched = NewShell(store);
            Assert.True(relaunched.NotificationsEnabled);
            var bootstraps = relaunched.BootstrapSnapshotCount;
            await relaunched.StartAsync();
            await WaitAsync(() => relaunched.BootstrapSnapshotCount > bootstraps);
            await relaunched.WebPushSync;

            Assert.Equal(2, Commands(desktop, RemoteProtocol.Actions.RegisterWebPush).Count);
            Assert.True(relaunched.IsNotificationLinkLive);
        });
    }

    [Fact]
    public async Task TurningNotificationsOffInABrowserForgetsItOnThePcAndUnsubscribes()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);
            shell.NotificationsEnabled = true;
            await shell.WebPushSync;

            shell.NotificationsEnabled = false;
            await shell.WebPushSync;

            Assert.Single(Commands(desktop, RemoteProtocol.Actions.UnregisterWebPush));
            Assert.Equal(1, platform.Unsubscribed);
            Assert.False(shell.IsNotificationLinkLive);
        });
    }

    [Fact]
    public async Task WhatStopsABrowserIsSaidAndANotificationThatCannotWorkIsNeverOffered()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { GrantOnRequest = true, Support = PhoneNotificationSupport.NeedsSecureAddress };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);

            Assert.True(shell.IsNotificationsAvailable);
            Assert.False(shell.IsNotificationsPromptOpen);
            Assert.False(shell.IsNotificationsSupported);
            Assert.False(shell.CanToggleNotifications);
            Assert.Equal(Strings.Get("NotificationsNeedHttps", "TEST-PC"), shell.NotificationsSupportText);

            platform.Support = PhoneNotificationSupport.NeedsHomeScreen;
            shell.RefreshNotificationState();
            Assert.Equal(Strings.Get("NotificationsNeedHomeScreen"), shell.NotificationsSupportText);
            platform.Support = PhoneNotificationSupport.Unsupported;
            shell.RefreshNotificationState();
            Assert.Equal(Strings.Get("NotificationsBrowserUnsupported"), shell.NotificationsSupportText);
            Assert.Empty(platform.SubscribedWith);
            Assert.Empty(Commands(desktop, RemoteProtocol.Actions.RegisterWebPush));
        });
    }

    [Fact]
    public async Task ABlockedOrFailedBrowserSaysWhatWentWrongInsteadOfSeemingReady()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform
        {
            GrantOnRequest = false,
            SubscribeError = new InvalidOperationException("Registration failed - push service error")
        };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop();
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);

            // Refused: the browser's own settings are the way out, which Lumi cannot open.
            shell.NotificationsEnabled = true;
            await shell.WebPushSync;
            Assert.True(shell.IsNotificationsBlocked);
            Assert.Equal(Strings.Get("NotificationsBlockedWeb"), shell.NotificationsBlockedText);
            Assert.Empty(platform.SubscribedWith);

            // Allowed, but the browser has no working push service.
            platform.Allowed = true;
            await shell.NotifyApplicationActivatedAsync();
            await shell.WebPushSync;
            Assert.False(shell.IsNotificationsBlocked);
            Assert.False(shell.IsNotificationLinkLive);
            Assert.Equal(
                Strings.Get("NotificationsWebFailed", Strings.Get("NotificationsBrowserFailed")),
                shell.NotificationsStatusText);
            await shell.SendTestNotificationCommand.ExecuteAsync(null);
            Assert.Equal(
                Strings.Get("TestNotificationFailed", Strings.Get("NotificationsBrowserFailed")),
                shell.NotificationTestMessage);
            Assert.Empty(Commands(desktop, RemoteProtocol.Actions.TestNotification));

            // The PC refusing the subscription says why.
            platform.SubscribeError = null;
            desktop.CommandResultFactory = command => command?.Action == RemoteProtocol.Actions.RegisterWebPush
                ? new RemoteCommandResult { Ok = false, Error = "Lumi can't send notifications through this browser." }
                : new RemoteCommandResult { Ok = true };
            await shell.NotifyApplicationActivatedAsync();
            await shell.WebPushSync;
            Assert.Equal(
                Strings.Get("NotificationsWebFailed", "Lumi can't send notifications through this browser."),
                shell.NotificationsStatusText);
        });
    }

    [Fact]
    public async Task APcThatCannotPushToBrowsersAsksForAnUpdate()
    {
        using var session = HeadlessMobileSession.Start();
        var platform = new FakeWebPushPlatform { Allowed = true };
        await WithPlatformAsync(platform, async () =>
        {
            await using var desktop = WebPushDesktop(key: null);
            await using var shell = NewShell(session.NewStore());
            await PairAsync(shell, desktop);

            shell.NotificationsEnabled = true;
            await shell.WebPushSync;

            Assert.True(shell.IsNotificationsUnsupportedOnPc);
            Assert.Equal(Strings.Get("NotificationsPcUpdateNeeded", "TEST-PC"), shell.NotificationsStatusText);
            Assert.Empty(platform.SubscribedWith);
        });
    }

    private static FakeLumiDesktop WebPushDesktop(string? key = "BPcKey")
    {
        var desktop = new FakeLumiDesktop();
        desktop.Capabilities.Add(RemoteProtocol.Capabilities.PhoneNotificationsV1);
        desktop.Snapshot.Settings = new RemoteSettings
        {
            PhoneNotificationsEnabled = true,
            PhoneNotificationAwayMinutes = 2,
            WebPushKey = key
        };
        desktop.Start();
        return desktop;
    }

    private static List<RemoteCommand> Commands(FakeLumiDesktop desktop, string action)
    {
        lock (desktop.ReceivedCommands)
            return desktop.ReceivedCommands.Where(command => command.Action == action).ToList();
    }

    private static async Task PairAsync(MobileShellViewModel shell, FakeLumiDesktop desktop)
    {
        var bootstraps = shell.BootstrapSnapshotCount;
        shell.Connect.ManualAddress = desktop.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(shell.IsPaired);
        await WaitAsync(() => shell.BootstrapSnapshotCount > bootstraps);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "Timed out.");
    }

    private static MobileSettingsStore PairedStore(HeadlessMobileSession session)
    {
        var store = session.NewStore();
        var settings = store.Load();
        settings.BaseUrl = "http://127.0.0.1:9";
        settings.Token = "token";
        settings.HostName = "TEST-PC";
        store.Save(settings);
        return store;
    }

    private static async Task WithPlatformAsync(IPhoneNotificationPlatform platform, Func<Task> body)
    {
        var previous = MobilePlatformServices.Notifications;
        MobilePlatformServices.Notifications = platform;
        try
        {
            await body();
        }
        finally
        {
            MobilePlatformServices.Notifications = previous;
        }
    }

    private sealed class FakeNotificationPlatform : IPhoneNotificationPlatform
    {
        public bool Allowed { get; set; }
        public bool GrantOnRequest { get; init; }
        public int PermissionRequests { get; private set; }
        public List<bool> Applied { get; } = [];
        public List<PhoneNotificationChannel?> Opened { get; } = [];
        public List<Guid> Cleared { get; } = [];

        public bool IsAvailable => true;

        public bool AreNotificationsAllowed => Allowed;

        public Task<bool> RequestPermissionAsync()
        {
            PermissionRequests++;
            if (GrantOnRequest)
                Allowed = true;
            return Task.FromResult(Allowed);
        }

        public void Apply(bool enabled) => Applied.Add(enabled);

        public PhoneNotificationLinkState LinkState => PhoneNotificationLinkState.Stopped;

        public event Action? LinkStateChanged
        {
            add { }
            remove { }
        }

        public bool HasChannelSettings => true;

        public bool IsChannelEnabled(PhoneNotificationChannel channel) => true;

        public void OpenSettings(PhoneNotificationChannel? channel) => Opened.Add(channel);

        public void ClearChat(Guid chatId) => Cleared.Add(chatId);
    }

    /// <summary>A browser: Web Push, no background connection, no settings Lumi can open.</summary>
    private sealed class FakeWebPushPlatform : IPhoneNotificationPlatform
    {
        /// <summary>The browser's Notification.permission: default, granted or denied.</summary>
        public string Permission { get; set; } = "default";
        public bool Allowed
        {
            get => Permission == "granted";
            set => Permission = value ? "granted" : "denied";
        }
        public bool GrantOnRequest { get; init; }
        public int PermissionRequests { get; private set; }
        public PhoneNotificationSupport Support { get; set; } = PhoneNotificationSupport.Supported;
        public bool IsInstalledApp { get; set; }
        public WebPushSubscriptionKeys Subscription { get; set; } =
            new("https://fcm.googleapis.com/fcm/send/browser", "BBrowserKey", "Secret");
        public Exception? SubscribeError { get; set; }
        public List<string> SubscribedWith { get; } = [];
        public int Unsubscribed { get; private set; }
        public List<bool> Applied { get; } = [];

        /// <summary>Whether the next test reaches the browser: true, false, or null when unknown.</summary>
        public bool? TestArrives { get; set; } = true;
        public int TestsExpected { get; private set; }

        public bool IsAvailable => true;

        public bool AreNotificationsAllowed => Allowed;

        public bool CanRequestPermission => Permission == "default";

        public bool UsesWebPush => true;

        public bool CanOpenSettings => false;

        public bool HasChannelSettings => false;

        public PhoneNotificationLinkState LinkState => PhoneNotificationLinkState.Stopped;

        public event Action? LinkStateChanged
        {
            add { }
            remove { }
        }

        // As a browser does: it asks only while nothing has refused, and a refusal sticks.
        public Task<bool> RequestPermissionAsync()
        {
            PermissionRequests++;
            if (Permission == "default")
                Permission = GrantOnRequest ? "granted" : "denied";
            return Task.FromResult(Allowed);
        }

        public Task<bool?> ExpectTestNotificationAsync(TimeSpan timeout)
        {
            TestsExpected++;
            return Task.FromResult(TestArrives);
        }

        public Task<WebPushSubscriptionKeys> SubscribeAsync(string serverKey)
        {
            SubscribedWith.Add(serverKey);
            return SubscribeError is null
                ? Task.FromResult(Subscription)
                : Task.FromException<WebPushSubscriptionKeys>(SubscribeError);
        }

        public Task UnsubscribeAsync()
        {
            Unsubscribed++;
            return Task.CompletedTask;
        }

        public void Apply(bool enabled) => Applied.Add(enabled);

        public bool IsChannelEnabled(PhoneNotificationChannel channel) => true;

        public void OpenSettings(PhoneNotificationChannel? channel)
        {
        }

        public void ClearChat(Guid chatId)
        {
        }
    }
}
