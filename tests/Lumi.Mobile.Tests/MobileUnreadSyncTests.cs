using System.Text.Json;
using Lumi.Mobile.Localization;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileUnreadSyncTests
{
    [Fact]
    public void AccessibilityNameTracksTitleRunningBackgroundActivityAndUnread()
    {
        var row = new ChatListItemViewModel(new RemoteChat { Title = "Draft" });
        var notifications = new List<string?>();
        row.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Assert.Equal("Draft", row.AccessibilityName);

        row.Title = "Release";
        row.IsRunning = true;
        row.HasUnreadMessages = true;
        Assert.Equal("Release, Running, Unread", row.AccessibilityName);
        Assert.Equal(3, notifications.Count(name => name == nameof(row.AccessibilityName)));

        row.IsRunning = false;
        row.HasUnreadMessages = false;
        Assert.Equal("Release", row.AccessibilityName);
        Assert.Equal(5, notifications.Count(name => name == nameof(row.AccessibilityName)));

        row.IsSessionActive = true;
        row.HasUnreadMessages = true;
        Assert.Equal($"Release, {ChatSessionStrings.ReadyWithBackgroundActivity}, Unread", row.AccessibilityName);
        Assert.Equal(7, notifications.Count(name => name == nameof(row.AccessibilityName)));
        row.IsSessionActive = false;
        Assert.Equal("Release, Unread", row.AccessibilityName);
        Assert.Equal(8, notifications.Count(name => name == nameof(row.AccessibilityName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAcknowledgementWaitsForVisibleTranscriptAndDoesNotRepeatForReadRefreshes(
        bool statusDuringInitialLoad)
    {
        var chatId = Guid.NewGuid();
        await using var desktop = new FakeLumiDesktop
        {
            Snapshot = Snapshot(chatId, unread: true),
            Transcript = Transcript(chatId, unread: true)
        };
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.TranscriptResponseFactory = async _ =>
        {
            requested.TrySetResult();
            await release.Task;
            return desktop.Transcript;
        };
        var acknowledgements = 0;
        desktop.CommandResultFactory = command =>
        {
            if (command?.Action == RemoteProtocol.Actions.OpenChat)
            {
                Assert.Equal(chatId.ToString(), command.Get("chatId"));
                Assert.Equal("2", command.Get("readThroughMessageCount"));
                desktop.Transcript = Transcript(chatId, unread: false);
                desktop.Snapshot = Snapshot(chatId, unread: false);
                Interlocked.Increment(ref acknowledgements);
            }
            return new RemoteCommandResult { Ok = true };
        };
        desktop.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        var row = Assert.Single(Assert.Single(shell.ChatList.Groups).Chats);
        shell.ChatList.OpenChatCommand.Execute(row);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(row.HasUnreadMessages);
        Assert.Equal(0, Volatile.Read(ref acknowledgements));
        if (statusDuringInitialLoad)
        {
            var statusVersion = shell.Chat.StatusVersion;
            await desktop.PushAsync(RemoteProtocol.Events.ChatStatus,
                JsonSerializer.Serialize(new RemoteChatStatus
                {
                    ChatId = chatId, HasUnreadMessages = true
                }, RemoteJsonContext.Default.RemoteChatStatus));
            await WaitAsync(() => shell.Chat.StatusVersion > statusVersion);
            Assert.True(shell.Chat.IsLoading);
            Assert.Equal(0, Volatile.Read(ref acknowledgements));
        }
        release.TrySetResult();
        await WaitAsync(() => Volatile.Read(ref acknowledgements) == 1);
        for (var i = 0; i < 3; i++)
        {
            await shell.RefreshTranscriptAsync();
            await shell.ChatList.RefreshFromServerAsync();
        }
        Assert.False(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats).HasUnreadMessages);
        Assert.Equal(1, Volatile.Read(ref acknowledgements));

        // A terminal update can mark the same two-message transcript unread again.
        // Count-only client watermarks must not suppress a new authoritative read.
        desktop.Transcript = Transcript(chatId, unread: true, revision: 2);
        desktop.Snapshot = Snapshot(chatId, unread: true);
        await shell.RefreshTranscriptAsync();
        await WaitAsync(() => Volatile.Read(ref acknowledgements) == 2);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("drawer")]
    [InlineData("background")]
    [InlineData("history")]
    [InlineData("git")]
    public async Task HiddenOrHistoricalTranscriptDoesNotAcknowledgeUnread(string hiddenBy)
    {
        var chatId = Guid.NewGuid();
        await using var desktop = new FakeLumiDesktop
        {
            Snapshot = Snapshot(chatId, unread: true),
            Transcript = Transcript(chatId, unread: true)
        };
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.TranscriptResponseFactory = async _ =>
        {
            requested.TrySetResult();
            await release.Task;
            return desktop.Transcript;
        };
        var acknowledgements = 0;
        desktop.CommandResultFactory = command =>
        {
            if (command?.Action == RemoteProtocol.Actions.OpenChat)
                Interlocked.Increment(ref acknowledgements);
            return new RemoteCommandResult { Ok = true };
        };
        desktop.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        shell.UpdateLayout(393, 852);
        shell.ChatList.OpenChatCommand.Execute(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        switch (hiddenBy)
        {
            case "settings": shell.Page = MobilePage.Settings; break;
            case "drawer": shell.IsDrawerOpen = true; break;
            case "background": await shell.NotifyApplicationDeactivatedAsync(); break;
            case "history": desktop.Transcript.IsLatestWindow = false; break;
            case "git": shell.Chat.IsGitChangesOpen = true; break;
        }
        release.TrySetResult();
        if (hiddenBy != "background")
        {
            await shell.RefreshTranscriptAsync();
            await WaitAsync(() => !shell.Chat.IsLoading);
        }
        Assert.Equal(0, Volatile.Read(ref acknowledgements));
        Assert.True(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats).HasUnreadMessages);

        if (hiddenBy == "git")
            shell.Chat.IsGitChangesOpen = false;
        else if (hiddenBy == "drawer")
            shell.IsDrawerOpen = false;
        if (hiddenBy is "git" or "drawer")
            await WaitAsync(() => Volatile.Read(ref acknowledgements) > 0);
    }

    [Fact]
    public async Task LegacyReadWatermarkDoesNotLeakAcrossHostSwitch()
    {
        var chatId = Guid.NewGuid();
        await using var first = new FakeLumiDesktop
        {
            Snapshot = Snapshot(chatId, unread: true),
            Transcript = Transcript(chatId, unread: true)
        };
        await using var second = new FakeLumiDesktop
        {
            Snapshot = Snapshot(chatId, unread: true),
            Transcript = Transcript(chatId, unread: true)
        };
        first.Transcript.Status.HasUnreadMessages = null;
        second.Transcript.Status.HasUnreadMessages = null;
        first.Start();
        second.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, first);
        shell.ChatList.OpenChatCommand.Execute(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats));
        await WaitAsync(() => ReadCommands(first).Any(c => c.Action == RemoteProtocol.Actions.OpenChat));
        await shell.RefreshTranscriptAsync();

        await shell.ForgetPcLocallyCommand.ExecuteAsync(null);
        await PairAsync(shell, second);
        shell.ChatList.OpenChatCommand.Execute(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats));
        await WaitAsync(() => ReadCommands(second).Any(c => c.Action == RemoteProtocol.Actions.OpenChat));
        Assert.All(ReadCommands(second).Where(c => c.Action == RemoteProtocol.Actions.OpenChat),
            command => Assert.Equal(chatId.ToString(), command.Get("chatId")));
    }

    [Fact]
    public async Task PendingTranscriptFromPreviousHostDoesNotAcknowledgeOnNewHost()
    {
        var oldChatId = Guid.NewGuid();
        var newChatId = Guid.NewGuid();
        await using var first = new FakeLumiDesktop { Snapshot = Snapshot(oldChatId, unread: true) };
        await using var second = new FakeLumiDesktop
        {
            Snapshot = Snapshot(newChatId, unread: true),
            Transcript = Transcript(newChatId, unread: true)
        };
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.TranscriptResponseFactory = async _ =>
        {
            requested.TrySetResult();
            await release.Task;
            return Transcript(oldChatId, unread: true);
        };
        first.Start();
        second.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, first);
        shell.ChatList.OpenChatCommand.Execute(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats));
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await shell.ForgetPcLocallyCommand.ExecuteAsync(null);
        await PairAsync(shell, second);
        release.TrySetResult();
        shell.ChatList.OpenChatCommand.Execute(Assert.Single(Assert.Single(shell.ChatList.Groups).Chats));
        await WaitAsync(() => ReadCommands(second).Any(c => c.Action == RemoteProtocol.Actions.OpenChat));
        await shell.RefreshTranscriptAsync();
        Assert.Equal(newChatId, shell.Chat.ChatId);
        Assert.DoesNotContain(ReadCommands(first), c => c.Action == RemoteProtocol.Actions.OpenChat);
        Assert.All(ReadCommands(second).Where(c => c.Action == RemoteProtocol.Actions.OpenChat),
            command => Assert.Equal(newChatId.ToString(), command.Get("chatId")));
    }

    [Fact]
    public async Task DesktopStatusUpdatesBothListsWithoutManufacturingUnreadOnUnchangedSnapshots()
    {
        var chatId = Guid.NewGuid();
        await using var desktop = new FakeLumiDesktop { Snapshot = Snapshot(chatId, unread: true) };
        desktop.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        shell.SearchChatList.Apply(Snapshot(chatId, unread: true).Chats);
        var drawerRow = Assert.Single(Assert.Single(shell.ChatList.Groups).Chats);
        var searchRow = Assert.Single(Assert.Single(shell.SearchChatList.Groups).Chats);

        await PushStatusAsync(unread: true, running: true);
        await WaitAsync(() => drawerRow.IsRunning && searchRow.IsRunning);
        Assert.True(drawerRow.HasUnreadMessages);
        Assert.True(searchRow.HasUnreadMessages);

        await PushStatusAsync(unread: false, running: false);
        await WaitAsync(() => !drawerRow.HasUnreadMessages && !searchRow.HasUnreadMessages);
        Assert.False(drawerRow.IsRunning);
        Assert.False(searchRow.IsRunning);

        await PushStatusAsync(unread: true, running: false, sessionActive: true);
        await WaitAsync(() => drawerRow.HasBackgroundActivity && searchRow.HasBackgroundActivity
                              && drawerRow.HasUnreadMessages && searchRow.HasUnreadMessages);
        Assert.False(drawerRow.IsRunning);
        Assert.False(searchRow.IsRunning);
        await PushStatusAsync(unread: false, running: false);
        await WaitAsync(() => !drawerRow.IsSessionActive && !searchRow.IsSessionActive
                              && !drawerRow.HasUnreadMessages && !searchRow.HasUnreadMessages);

        desktop.Snapshot = Snapshot(chatId, unread: false);
        for (var i = 0; i < 3; i++)
        {
            await shell.RefreshSnapshotAsync();
            await shell.ChatList.RefreshFromServerAsync();
        }
        Assert.False(drawerRow.HasUnreadMessages);
        Assert.False(searchRow.HasUnreadMessages);

        // A status frame for an older paged-in row must not require it to be in the first page.
        shell.ChatList.ApplyLive(new RemoteChatPage { TotalCount = 200, HasMore = true });
        await PushStatusAsync(unread: true, running: false);
        await WaitAsync(() => drawerRow.HasUnreadMessages && searchRow.HasUnreadMessages);
        Assert.DoesNotContain(ReadCommands(desktop), c => c.Action == RemoteProtocol.Actions.OpenChat);

        Task PushStatusAsync(bool unread, bool running, bool sessionActive = false) => desktop.PushAsync(
            RemoteProtocol.Events.ChatStatus,
            JsonSerializer.Serialize(new RemoteChatStatus
            {
                ChatId = chatId, HasUnreadMessages = unread, IsBusy = running, IsSessionActive = sessionActive
            }, RemoteJsonContext.Default.RemoteChatStatus));
    }

    private static RemoteCommand[] ReadCommands(FakeLumiDesktop desktop)
    {
        lock (desktop.ReceivedCommands)
            return desktop.ReceivedCommands.ToArray();
    }

    private static RemoteSnapshot Snapshot(Guid chatId, bool unread) => new()
    {
        Chats = new RemoteChatPage
        {
            TotalCount = 1,
            Groups = [new RemoteChatGroup
            {
                Label = "Today",
                Chats = [new RemoteChat
                {
                    Id = chatId, Title = "Read state", MessageCount = 2, HasUnreadMessages = unread
                }]
            }]
        }
    };

    private static RemoteTranscript Transcript(Guid chatId, bool unread, long revision = 1) => new()
    {
        ChatId = chatId,
        Title = "Read state",
        Revision = revision,
        TotalRawMessageCount = 2,
        WindowEndMessageIndex = 2,
        IsLatestWindow = true,
        Status = new RemoteChatStatus { ChatId = chatId, HasUnreadMessages = unread },
        Turns = [new RemoteTranscriptTurn
        {
            Id = "turn",
            Items =
            [
                new RemoteTranscriptItem { Id = "user", Kind = RemoteProtocol.ItemKinds.User, Text = "Fixture" },
                new RemoteTranscriptItem { Id = "answer", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Answer" }
            ]
        }]
    };

    private static MobileShellViewModel CreateShell() => new(
        new LumiRemoteClient("read-state-fixture", "Test phone"),
        new LumiDiscoveryClient(),
        new MobileSettingsStore(Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("N"))),
        action => action());

    private static async Task PairAsync(MobileShellViewModel shell, FakeLumiDesktop desktop)
    {
        var bootstrapCount = shell.BootstrapSnapshotCount;
        shell.Connect.ManualAddress = desktop.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        await WaitAsync(() => shell.BootstrapSnapshotCount > bootstrapCount);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "The expected read/running state did not arrive.");
    }
}
