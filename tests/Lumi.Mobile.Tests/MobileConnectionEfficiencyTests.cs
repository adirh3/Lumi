using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The phone's connection costs: what a short app switch, a reopen, an unreachable PC, a busy
/// stream and a refresh of an unchanged chat send over the network.
/// </summary>
public sealed class MobileConnectionEfficiencyTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShortAppSwitchKeepsTheStreamAndResumesWithoutReconnecting()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, time);

        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == 1);

        await shell.NotifyApplicationDeactivatedAsync();
        var background = Assert.Single(desktop.Subscriptions);
        Assert.False(background.IsForeground);
        Assert.True(shell.Client.IsEventStreamActive);

        await shell.NotifyApplicationActivatedAsync();
        await WaitUntilAsync(() => desktop.Subscriptions.Count == 2);
        Assert.True(desktop.Subscriptions[^1].IsForeground);
        // The desktop's resume ping proves the kept stream is alive before the probe looks.
        await WaitUntilAsync(() => shell.Client.ReceivedFrameCount >= 2);
        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(100);

        Assert.Equal(1, desktop.Events);
        Assert.Equal(1, desktop.Hellos);
        Assert.Equal(0, desktop.Snapshots);
        Assert.Equal(1, shell.BootstrapSnapshotCount);
        Assert.True(shell.IsConnected);
    }

    [Fact]
    public async Task ReopenAfterTheGraceSkipsTheHandshakeAndReusesUnchangedSections()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var chatId = Guid.NewGuid();
        var desktop = new EfficiencyDesktop
        {
            Snapshot = new RemoteSnapshot
            {
                HostName = "TEST-PC",
                IsConnected = true,
                Chats = new RemoteChatPage
                {
                    TotalCount = 1,
                    Groups = [new RemoteChatGroup { Label = "Today", Chats = [new RemoteChat { Id = chatId, Title = "Kept" }] }]
                },
                Library = new RemoteLibrary { Projects = [new RemoteProject { Id = Guid.NewGuid(), Name = "Kept project" }] },
                Settings = new RemoteSettings { UserName = "Adir" }
            }
        };
        await using var shell = CreateShell(desktop, temp.Path, time);
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 1);

        await shell.NotifyApplicationDeactivatedAsync();
        time.Advance(TimeSpan.FromSeconds(61));
        await WaitUntilAsync(() => !shell.Client.IsEventStreamActive);

        await shell.NotifyApplicationActivatedAsync();
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 2);

        Assert.Equal(1, desktop.Hellos);
        Assert.Equal(2, desktop.Events);
        Assert.Contains("known=chats:c1,library:l1,settings:s1", desktop.LastEventQuery, StringComparison.Ordinal);
        Assert.Equal(["chats", "library", "settings"], desktop.LastUnchangedSections);
        Assert.Contains(shell.ChatList.Groups.SelectMany(group => group.Chats), chat => chat.Id == chatId);
        Assert.Contains(shell.Projects, project => project.Name == "Kept project");
        Assert.Equal("Adir", shell.UserName);
        Assert.Equal(3, shell.Client.Stats.Snapshot().BootstrapSectionsReused);
    }

    [Fact]
    public async Task OlderDesktopsStillDisconnectAsSoonAsTheAppLeaves()
    {
        using var temp = new TempDirectory();
        var desktop = new EfficiencyDesktop();
        desktop.Capabilities.Remove(RemoteProtocol.Capabilities.ResumePingV1);
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected);

        await shell.NotifyApplicationDeactivatedAsync();

        Assert.False(shell.Client.IsEventStreamActive);
        Assert.Empty(desktop.Subscriptions);
    }

    [Fact]
    public async Task ASilentKeptStreamIsReplacedAfterResuming()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var desktop = new EfficiencyDesktop { PingOnForegroundReturn = false };
        await using var shell = CreateShell(desktop, temp.Path, time);
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected);

        await shell.NotifyApplicationDeactivatedAsync();
        await shell.NotifyApplicationActivatedAsync();
        await WaitUntilAsync(() => desktop.Subscriptions.Count == 2);
        await WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => desktop.Events == 2 && shell.BootstrapSnapshotCount == 2);
        Assert.Equal(1, desktop.Hellos);
    }

    [Fact]
    public async Task AProcessFrozenPastTheGraceStartsAFreshStreamOnResume()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, time);
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected);

        await shell.NotifyApplicationDeactivatedAsync();
        // Android froze the app: the clock moved, but the grace timer never ran.
        time.Freeze(TimeSpan.FromMinutes(10));
        Assert.True(shell.Client.IsEventStreamActive);

        await shell.NotifyApplicationActivatedAsync();
        await WaitUntilAsync(() => desktop.Events == 2 && shell.BootstrapSnapshotCount == 2);
        Assert.Equal(1, desktop.Hellos);
    }

    [Fact]
    public async Task AnUnreachablePcStopsRetryingUntilTheUserRetries()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var desktop = new EfficiencyDesktop { FailEvents = true };
        await using var shell = CreateShell(
            desktop,
            temp.Path,
            time,
            giveUpAfter: TimeSpan.FromSeconds(30));
        await shell.StartAsync();

        for (var step = 0; step < 200 && !shell.IsConnectionOffline; step++)
        {
            await Task.Delay(5);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(shell.IsConnectionOffline);
        Assert.True(shell.ShowRetryConnectionAction);
        Assert.StartsWith("Can't reach TEST-PC.", shell.ConnectionBannerText, StringComparison.Ordinal);
        var attempts = desktop.Events;
        Assert.InRange(attempts, 3, 8);

        // Offline means no timer is driving more attempts.
        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(100);
        Assert.Equal(attempts, desktop.Events);

        desktop.FailEvents = false;
        await shell.RetryConnectionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => shell.IsConnected);
        Assert.False(shell.IsConnectionOffline);
    }

    [Fact]
    public async Task ANetworkThatComesBackEndsTheOfflineWaitAtOnce()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var network = new PushedNetworkMonitor();
        var desktop = new EfficiencyDesktop { FailEvents = true };
        await using var shell = CreateShell(desktop, temp.Path, time, network: network);
        network.Report(false);

        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnectionOffline);
        Assert.StartsWith("This phone is offline.", shell.ConnectionBannerText, StringComparison.Ordinal);
        var attempts = desktop.Events;

        desktop.FailEvents = false;
        network.Report(true);
        await WaitUntilAsync(() => shell.IsConnected);
        Assert.Equal(attempts + 1, desktop.Events);
    }

    [Fact]
    public async Task AMessageSentBeforeTheAppLeavesStillCompletes()
    {
        using var temp = new TempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktop = new EfficiencyDesktop
        {
            CommandResponder = async command =>
            {
                if (command.Action == RemoteProtocol.Actions.SendMessage)
                    await release.Task;
                return new RemoteCommandResult { Ok = true, RequestId = command.RequestId, ChatId = Guid.NewGuid() };
            }
        };
        desktop.Capabilities.Remove(RemoteProtocol.Capabilities.ResumePingV1);
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected);

        shell.Chat.PromptText = "Sent just before locking the phone";
        var send = shell.Chat.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => desktop.Commands.Any(command => command.Action == RemoteProtocol.Actions.SendMessage));
        await shell.NotifyApplicationDeactivatedAsync();
        Assert.False(shell.Client.IsEventStreamActive);

        release.SetResult();
        await send.WaitAsync(WaitTimeout);
        Assert.Null(shell.Chat.ErrorText);
        Assert.Equal("", shell.Chat.PromptText);
        Assert.Single(desktop.Commands, command => command.Action == RemoteProtocol.Actions.SendMessage);
    }

    [Fact]
    public async Task UnchangedChatRefreshesCostNoTranscriptBody()
    {
        using var temp = new TempDirectory();
        var chatId = Guid.NewGuid();
        var desktop = new EfficiencyDesktop
        {
            Snapshot = new RemoteSnapshot { HostName = "TEST-PC", IsConnected = true, ActiveChatId = chatId },
            TranscriptResponder = (id, known) => Task.FromResult(known == "v1"
                ? new RemoteTranscript
                {
                    ChatId = id, IsUnchanged = true, TranscriptVersion = "v1", Revision = 1,
                    Status = new RemoteChatStatus { ChatId = id, StatusText = "still idle" }
                }
                : Transcript(id, "v1", "Hello"))
        };
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => AnswerText(shell) == "Hello" && !shell.Chat.IsLoading);
        var turn = Assert.Single(shell.Chat.Turns);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            shell.IsDrawerOpen = true;
            shell.IsDrawerOpen = false;
            await WaitUntilAsync(() => desktop.TranscriptKnown.Count == cycle + 2);
        }

        await WaitUntilAsync(() => shell.Chat.StatusText == "still idle");
        Assert.Equal([null, "v1", "v1", "v1"], desktop.TranscriptKnown);
        Assert.Same(turn, Assert.Single(shell.Chat.Turns));
        Assert.Equal(3, shell.Client.Stats.Snapshot().TranscriptsUnchanged);
    }

    [Fact]
    public async Task AnUnchangedAnswerThatNoLongerMatchesReadsTheWholeWindow()
    {
        using var temp = new TempDirectory();
        var chatId = Guid.NewGuid();
        var desktop = new EfficiencyDesktop
        {
            Snapshot = new RemoteSnapshot { HostName = "TEST-PC", IsConnected = true, ActiveChatId = chatId },
            TranscriptResponder = (id, known) => Task.FromResult(known is null
                ? Transcript(id, "v1", "Hello")
                : new RemoteTranscript
                {
                    ChatId = id, IsUnchanged = true, TranscriptVersion = "someone-else", Revision = 1,
                    Status = new RemoteChatStatus { ChatId = id }
                })
        };
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => AnswerText(shell) == "Hello");

        await shell.RefreshTranscriptAsync();
        await WaitUntilAsync(() => desktop.TranscriptKnown.Count == 3);
        Assert.Equal([null, "v1", null], desktop.TranscriptKnown);
    }

    [Fact]
    public async Task DeltasForARowStillBeingFetchedAreReplayedWithoutAnotherFetch()
    {
        using var temp = new TempDirectory();
        var chatId = Guid.NewGuid();
        var gates = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<RemoteTranscript>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var desktop = new EfficiencyDesktop
        {
            Snapshot = new RemoteSnapshot { HostName = "TEST-PC", IsConnected = true, ActiveChatId = chatId }
        };
        desktop.TranscriptResponder = (_, _) => gates[Math.Min(desktop.Transcripts, gates.Length) - 1].Task;
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => desktop.Transcripts == 1);
        gates[0].SetResult(Transcript(chatId, null, answer: null));
        await WaitUntilAsync(() => !shell.Chat.IsLoading && shell.Chat.Turns.Count == 1);

        // A new answer starts streaming before any transcript knows its row.
        desktop.PushDelta(chatId, "answer", -1, "Hel");
        await WaitUntilAsync(() => desktop.Transcripts == 2);
        desktop.PushDelta(chatId, "answer", 3, "lo");
        desktop.PushDelta(chatId, "answer", 5, " wor");
        await WaitUntilAsync(() => shell.Client.Stats.Snapshot().Deltas[StreamDeltaDisposition.Held] == 2);

        // The desktop captured the read after "lo"; " wor" arrived after the capture.
        gates[1].SetResult(Transcript(chatId, null, "Hello", streaming: true));
        await WaitUntilAsync(() => AnswerText(shell) == "Hello wor");
        await Task.Delay(100);
        Assert.Equal(2, desktop.Transcripts);
    }

    [Fact]
    public async Task AReadThatLandsMidStreamNeverShortensTheAnswerOrStartsAnotherRead()
    {
        using var temp = new TempDirectory();
        var chatId = Guid.NewGuid();
        var gates = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<RemoteTranscript>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var desktop = new EfficiencyDesktop
        {
            Snapshot = new RemoteSnapshot { HostName = "TEST-PC", IsConnected = true, ActiveChatId = chatId }
        };
        desktop.TranscriptResponder = (_, _) => gates[Math.Min(desktop.Transcripts, gates.Length) - 1].Task;
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => desktop.Transcripts == 1);
        gates[0].SetResult(Transcript(chatId, null, "Hello wor", streaming: true));
        await WaitUntilAsync(() => AnswerText(shell) == "Hello wor");

        desktop.PushDelta(chatId, "answer", 9, "ld");
        await WaitUntilAsync(() => AnswerText(shell) == "Hello world");

        desktop.Push(RemoteProtocol.Events.TranscriptInvalidated, JsonSerializer.Serialize(
            new RemoteTranscriptInvalidated { ChatId = chatId, Revision = 2 },
            RemoteJsonContext.Default.RemoteTranscriptInvalidated));
        await WaitUntilAsync(() => desktop.Transcripts == 2);
        desktop.PushDelta(chatId, "answer", 11, "!");
        await WaitUntilAsync(() => AnswerText(shell) == "Hello world!");

        // That read was captured before "!": it must not take the text back.
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Chat.TranscriptApplied += () => applied.TrySetResult();
        gates[1].SetResult(Transcript(chatId, null, "Hello world", streaming: true, revision: 2));
        await applied.Task.WaitAsync(WaitTimeout);
        Assert.Equal("Hello world!", AnswerText(shell));

        desktop.PushDelta(chatId, "answer", 12, " Bye");
        await WaitUntilAsync(() => AnswerText(shell) == "Hello world! Bye");
        await Task.Delay(100);
        Assert.Equal(2, desktop.Transcripts);
    }

    [Fact]
    public async Task ConnectingDoesNotRepeatTheSubscriptionTheStreamAlreadyCarries()
    {
        using var temp = new TempDirectory();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());

        await shell.StartAsync();
        await WaitUntilAsync(() => shell.BootstrapSnapshotCount == 1);
        await Task.Delay(100);

        Assert.Empty(desktop.Subscriptions);
        Assert.Contains("streamMs=150", desktop.LastEventQuery, StringComparison.Ordinal);
        Assert.Contains("compactStatus=true", desktop.LastEventQuery, StringComparison.Ordinal);
        Assert.Contains("rowPatches=true", desktop.LastEventQuery, StringComparison.Ordinal);
        var stats = shell.Client.Stats.Snapshot();
        Assert.Equal(2, stats.RequestCount);
        Assert.Equal(1, stats.RequestsTo(RemoteProtocol.Routes.Hello));
        Assert.Equal(1, stats.RequestsTo(RemoteProtocol.Routes.Events));
    }

    [Fact]
    public async Task AScopeChangedWhileTheStreamRegistersStillReachesTheDesktop()
    {
        using var temp = new TempDirectory();
        var desktop = new EfficiencyDesktop
        {
            BootstrapGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());

        var start = shell.StartAsync();
        await WaitUntilAsync(() => desktop.Events == 1);

        // The user opens the chat list before the desktop has registered the new stream, so the
        // desktop acknowledges the update without applying it.
        shell.IsDrawerOpen = true;
        await WaitUntilAsync(() => desktop.DroppedSubscriptions.Count == 1);
        Assert.True(desktop.DroppedSubscriptions[0].IncludeChatList);

        desktop.BootstrapGate!.SetResult();
        await start.WaitAsync(WaitTimeout);
        await WaitUntilAsync(() => desktop.Subscriptions.Count == 1);
        Assert.True(desktop.Subscriptions[^1].IncludeChatList);
    }

    [Fact]
    public async Task AKeptStreamIsNotTimedOutWhileTheAppIsAway()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, time);
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == 1);

        // The last frame is 40 s old when the app leaves; the desktop then writes nothing.
        time.Advance(TimeSpan.FromSeconds(40));
        await shell.NotifyApplicationDeactivatedAsync();
        await WaitUntilAsync(() => desktop.Subscriptions.Count == 1);
        time.Advance(TimeSpan.FromSeconds(55));
        await Task.Delay(100);
        Assert.Equal(1, desktop.Events);

        await shell.NotifyApplicationActivatedAsync();
        await WaitUntilAsync(() => desktop.Subscriptions.Count == 2);
        await WaitUntilAsync(() => shell.Client.ReceivedFrameCount >= 2);
        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(100);
        Assert.Equal(1, desktop.Events);
        Assert.Equal(1, desktop.Hellos);
        Assert.True(shell.IsConnected);
    }

    [Fact]
    public async Task AForegroundStreamThatGoesSilentIsReplaced()
    {
        using var temp = new TempDirectory();
        var time = new ManualTimeProvider();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, time);
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == 1);

        // A foreground phone is pinged every minute, so 90 s of silence means a dead link.
        time.Advance(TimeSpan.FromSeconds(91));
        for (var step = 0; step < 100 && desktop.Events < 2; step++)
        {
            await Task.Delay(5);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await WaitUntilAsync(() => desktop.Events == 2 && shell.BootstrapSnapshotCount == 2);
    }

    [Fact]
    public async Task ALivePushDoesNotDownloadAFilteredListAgain()
    {
        using var temp = new TempDirectory();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == 1);

        shell.ChatList.ProjectFilterId = Guid.NewGuid();
        await WaitUntilAsync(() => desktop.ChatPages == 1 && !shell.ChatList.IsRefreshing);

        for (var push = 0; push < 3; push++)
        {
            desktop.Push(RemoteProtocol.Events.Chats, JsonSerializer.Serialize(
                ChatPage(Guid.NewGuid(), $"Busy chat {push}", running: true, isPatch: true),
                RemoteJsonContext.Default.RemoteChatPage));
        }

        await WaitUntilAsync(() => shell.Client.Stats.Snapshot().FrameCount >= 4);
        await Task.Delay(100);
        Assert.Equal(1, desktop.ChatPages);
    }

    [Fact]
    public async Task QuickScopeChangesOnASlowLinkAreAllSent()
    {
        using var temp = new TempDirectory();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == 1);
        await Task.Delay(50);
        Assert.Empty(desktop.Subscriptions);

        var answers = new List<TaskCompletionSource>();
        desktop.SubscriptionAnswer = () =>
        {
            var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (answers)
                answers.Add(answer);
            return answer.Task;
        };

        // The user opens the chat list and closes it again before the first update is answered:
        // the second one restores the scope the stream started with.
        shell.IsDrawerOpen = true;
        await WaitUntilAsync(() => AnswerCount() == 1);
        shell.IsDrawerOpen = false;
        await WaitUntilAsync(() => AnswerCount() == 2);

        // The first answer arrives while the second update still travels; the desktop will end on
        // the second, so opening the list again must be sent too.
        lock (answers)
            answers[0].SetResult();
        await Task.Delay(200);
        shell.IsDrawerOpen = true;
        await WaitUntilAsync(() => AnswerCount() == 3);
        lock (answers)
        {
            foreach (var answer in answers)
                answer.TrySetResult();
        }

        Assert.Equal([true, false, true], desktop.Subscriptions.Select(subscription => subscription.IncludeChatList));

        int AnswerCount()
        {
            lock (answers)
                return answers.Count;
        }
    }

    [Fact]
    public async Task ReturningToSearchReloadsTheSearchResults()
    {
        using var temp = new TempDirectory();
        var desktop = new EfficiencyDesktop();
        await using var shell = CreateShell(desktop, temp.Path, new ManualTimeProvider());
        await shell.StartAsync();
        await WaitUntilAsync(() => shell.IsConnected && shell.BootstrapSnapshotCount == 1);
        shell.ShowPageCommand.Execute("Search");
        await WaitUntilAsync(() => desktop.ChatPages == 1 && !shell.SearchChatList.IsRefreshing);

        // A result the PC deleted while the app was away for a moment.
        shell.SearchChatList.Apply(ChatPage(Guid.NewGuid(), "Deleted on the PC", running: false, isPatch: false));
        Assert.Single(shell.SearchChatList.Groups.SelectMany(group => group.Chats));

        await shell.NotifyApplicationDeactivatedAsync();
        await shell.NotifyApplicationActivatedAsync();

        await WaitUntilAsync(() => !shell.SearchChatList.Groups.SelectMany(group => group.Chats).Any());
        Assert.Equal(1, desktop.Events);
    }

    [Fact]
    public async Task AnOlderChatListReadDoesNotUndoANewerPush()
    {
        var chatId = Guid.NewGuid();
        var sink = new GatedChatPageSink();
        using var list = new ChatListViewModel(sink);

        var read = list.RefreshFromServerAsync();
        await WaitUntilAsync(() => sink.Requests == 1);

        // The desktop served the read while the chat was still running; the push saying it
        // finished arrives before that older page does.
        list.ApplyLive(ChatPage(chatId, "Finished title", running: false, isPatch: true));
        sink.Page.SetResult(ChatPage(chatId, "Working title", running: true, isPatch: false));
        await read.WaitAsync(WaitTimeout);

        var row = Assert.Single(list.Groups.SelectMany(group => group.Chats));
        Assert.False(row.IsRunning);
        Assert.Equal("Finished title", row.Title);
    }

    [Fact]
    public void ConnectionActivityReadsNaturallyInSettings()
    {
        var stats = new RemoteTransportStats(new ManualTimeProvider());
        stats.RecordRequest(RemoteProtocol.Routes.Events);
        stats.RecordFrame(RemoteProtocol.Events.Snapshot, 2048);
        stats.RecordStreamConnected();

        Assert.Equal("1 request, 2 KB received, 1 live update, 0 reconnects", stats.Snapshot().Summary);

        stats.RecordRequest(RemoteProtocol.Routes.Transcript);
        stats.RecordTranscript(unchanged: true);
        stats.RecordStreamConnected();
        stats.RecordStreamConnected();

        Assert.Equal(
            "2 requests, 2 KB received, 1 live update, 2 reconnects, 1 of 1 chat reload already up to date",
            stats.Snapshot().Summary);
    }

    [Fact]
    public void AnOmittedPlanKeepsThePlanThePhoneHolds()
    {
        var chat = new MobileChatViewModel(new NullSink());
        var chatId = Guid.NewGuid();
        chat.Reset(chatId, "Plan");
        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, PlanContent = "1. Keep me" });
        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, IsPlanOmitted = true, StatusText = "Working" });

        Assert.Equal("1. Keep me", chat.PlanContent);
        Assert.Equal("Working", chat.StatusText);

        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, PlanContent = null });
        Assert.Null(chat.PlanContent);
    }

    [Theory]
    [InlineData("Hello", 5, " world", "Applied", "Hello world")]
    [InlineData("Hello wor", 5, " world", "Applied", "Hello world")]
    [InlineData("Hello world", 5, " world", "Ignored", "Hello world")]
    [InlineData("Hello", 7, "rld", "Gap", "Hello")]
    [InlineData("Hello", 2, "xyz!", "Gap", "Hello")]
    [InlineData("Hello world", -1, "Hello", "Ignored", "Hello world")]
    [InlineData("Hello", -1, "Hello world", "Applied", "Hello world")]
    [InlineData("Hello world", -1, "Rewritten", "Applied", "Rewritten")]
    public void StreamChunksMergeByPositionWithoutLosingOrRepeatingText(
        string current, int offset, string text, string expected, string result)
    {
        var outcome = MobileChatViewModel.MergeStreamChunk(
            current,
            new RemoteStreamDelta { Offset = offset, Text = text },
            RemoteProtocol.MobileAssistantTextLimit,
            out var merged);

        Assert.Equal(expected, outcome.ToString());
        Assert.Equal(result, merged);
    }

    private static RemoteChatPage ChatPage(Guid chatId, string title, bool running, bool isPatch) => new()
    {
        TotalCount = 1,
        IsPatch = isPatch,
        Groups =
        [
            new RemoteChatGroup
            {
                Label = "Today",
                Chats =
                [
                    new RemoteChat
                    {
                        Id = chatId,
                        Title = title,
                        IsRunning = running,
                        IsSessionActive = running,
                        UpdatedAt = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero)
                    }
                ]
            }
        ]
    };

    private sealed class GatedChatPageSink : IRemoteCommandSink, IRemoteChatPageSink
    {
        private int _requests;

        public TaskCompletionSource<RemoteChatPage?> Page { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests => Volatile.Read(ref _requests);

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });

        public Task<RemoteChatPage?> GetChatPageAsync(
            int offset,
            int limit,
            string? query,
            Guid? projectId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Page.Task.WaitAsync(cancellationToken);
        }
    }

    private static string? AnswerText(MobileShellViewModel shell) =>
        shell.Chat.Turns.SelectMany(turn => turn.Items).OfType<AssistantItemViewModel>().FirstOrDefault()?.SourceText;

    private static RemoteTranscript Transcript(
        Guid chatId,
        string? version,
        string? answer,
        bool streaming = false,
        long revision = 1)
    {
        var items = new List<RemoteTranscriptItem>
        {
            new() { Id = "user", Kind = RemoteProtocol.ItemKinds.User, Text = "Hi" }
        };
        if (answer is not null)
        {
            items.Add(new RemoteTranscriptItem
            {
                Id = "answer",
                Kind = RemoteProtocol.ItemKinds.Assistant,
                Text = answer,
                IsStreaming = streaming
            });
        }

        return new RemoteTranscript
        {
            ChatId = chatId,
            Title = "Chat",
            Revision = revision,
            TranscriptVersion = version,
            WindowEndMessageIndex = items.Count,
            TotalRawMessageCount = items.Count,
            IsLatestWindow = true,
            Turns = [new RemoteTranscriptTurn { Id = "turn", Items = items }],
            Status = new RemoteChatStatus
            {
                ChatId = chatId,
                IsBusy = streaming,
                IsStreaming = streaming,
                HasUnreadMessages = false
            }
        };
    }

    private static MobileShellViewModel CreateShell(
        EfficiencyDesktop desktop,
        string directory,
        ManualTimeProvider time,
        IRemoteNetworkMonitor? network = null,
        TimeSpan? giveUpAfter = null)
    {
        var store = new MobileSettingsStore(directory);
        store.Save(new MobileConnectionSettings
        {
            DeviceId = "device",
            DeviceName = "Phone",
            BaseUrl = "http://lumi.test:47653",
            Token = "token",
            HostName = "TEST-PC"
        });
        var client = new LumiRemoteClient(
            "device",
            "Phone",
            desktop,
            requestDeadline: WaitTimeout,
            uploadDeadline: WaitTimeout,
            routeVerifier: new TrustedRouteVerifier(),
            time: time);
        var connection = new RemoteConnectionOptions
        {
            Time = time,
            Network = network ?? StaticNetworkMonitor.Available,
            Random = static () => 0.5,
            GiveUpAfter = giveUpAfter ?? RemoteConnectionOptions.Default.GiveUpAfter
        };
        return new MobileShellViewModel(
            client,
            store: store,
            post: action => action(),
            connection: connection);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected connection state was not reached.");
            await Task.Delay(5);
        }
    }

    /// <summary>A desktop fake that speaks the remote wire and counts everything the phone asks for.</summary>
    private sealed class EfficiencyDesktop : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RemoteEventSubscription> _subscriptions = [];
        private readonly List<RemoteEventSubscription> _droppedSubscriptions = [];
        private readonly List<string?> _transcriptKnown = [];
        private PushStream? _stream;
        private bool _registered;
        private int _hellos;
        private int _events;
        private int _snapshots;
        private int _transcripts;
        private int _chatPages;

        public List<string> Capabilities { get; } =
        [
            RemoteProtocol.Capabilities.ScopedEventsV1,
            RemoteProtocol.Capabilities.CompactTranscriptV1,
            RemoteProtocol.Capabilities.ConditionalTranscriptV1,
            RemoteProtocol.Capabilities.ResumePingV1
        ];

        public RemoteSnapshot Snapshot { get; init; } = new() { HostName = "TEST-PC", IsConnected = true };
        public Func<Guid, string?, Task<RemoteTranscript>>? TranscriptResponder { get; set; }
        public Func<RemoteCommand, Task<RemoteCommandResult>>? CommandResponder { get; init; }
        public bool PingOnForegroundReturn { get; init; } = true;
        public volatile bool FailEvents;

        /// <summary>
        /// While set, a new stream is not yet registered: its bootstrap waits for this gate, and
        /// subscription updates are acknowledged but dropped, as the real desktop does.
        /// </summary>
        public TaskCompletionSource? BootstrapGate { get; set; }

        /// <summary>
        /// When set, each subscription update is recorded at once but answered only when the task
        /// this returns completes, as on a slow link.
        /// </summary>
        public Func<Task>? SubscriptionAnswer { get; set; }

        public IReadOnlyList<RemoteEventSubscription> DroppedSubscriptions
        {
            get
            {
                lock (_gate)
                    return [.. _droppedSubscriptions];
            }
        }

        public ConcurrentQueue<RemoteCommand> Commands { get; } = new();
        public int Hellos => Volatile.Read(ref _hellos);
        public int Events => Volatile.Read(ref _events);
        public int Snapshots => Volatile.Read(ref _snapshots);
        public int Transcripts => Volatile.Read(ref _transcripts);
        public int ChatPages => Volatile.Read(ref _chatPages);
        public string LastEventQuery { get; private set; } = "";
        public List<string>? LastUnchangedSections { get; private set; }

        public IReadOnlyList<RemoteEventSubscription> Subscriptions
        {
            get
            {
                lock (_gate)
                    return [.. _subscriptions];
            }
        }

        public IReadOnlyList<string?> TranscriptKnown
        {
            get
            {
                lock (_gate)
                    return [.. _transcriptKnown];
            }
        }

        public void Push(string eventName, string json)
        {
            PushStream? stream;
            lock (_gate)
                stream = _stream;
            stream?.Push(new RemoteEventFrame(eventName, json).ToWire());
        }

        public void PushDelta(Guid chatId, string itemId, int offset, string text) =>
            Push(RemoteProtocol.Events.StreamDelta, JsonSerializer.Serialize(
                new RemoteStreamDelta { ChatId = chatId, ItemId = itemId, Offset = offset, Text = text },
                RemoteJsonContext.Default.RemoteStreamDelta));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            switch (uri.AbsolutePath)
            {
                case RemoteProtocol.Routes.Hello:
                    Interlocked.Increment(ref _hellos);
                    return Json(
                        new RemoteHello { Capabilities = [.. Capabilities], HostName = "TEST-PC", IsPaired = true },
                        RemoteJsonContext.Default.RemoteHello);

                case RemoteProtocol.Routes.Events:
                {
                    Interlocked.Increment(ref _events);
                    LastEventQuery = Uri.UnescapeDataString(uri.Query);
                    if (FailEvents)
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

                    var stream = new PushStream();
                    lock (_gate)
                    {
                        _stream?.Complete();
                        _stream = stream;
                        _registered = false;
                    }

                    var bootstrap = new RemoteEventFrame(
                        RemoteProtocol.Events.Snapshot,
                        JsonSerializer.Serialize(
                            BuildSnapshot(QueryValue(uri, "known")),
                            RemoteJsonContext.Default.RemoteSnapshot)).ToWire();
                    void Register()
                    {
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_stream, stream))
                                return;
                            _registered = true;
                        }

                        stream.Push(bootstrap);
                    }

                    if (BootstrapGate is { } bootstrapGate)
                        _ = bootstrapGate.Task.ContinueWith(_ => Register(), TaskScheduler.Default);
                    else
                        Register();
                    var content = new StreamContent(stream);
                    content.Headers.ContentType = new("text/event-stream");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }

                case RemoteProtocol.Routes.Snapshot:
                    Interlocked.Increment(ref _snapshots);
                    return Json(BuildSnapshot(QueryValue(uri, "known")), RemoteJsonContext.Default.RemoteSnapshot);

                case RemoteProtocol.Routes.Subscription:
                {
                    var subscription = JsonSerializer.Deserialize(
                        await request.Content!.ReadAsStringAsync(cancellationToken),
                        RemoteJsonContext.Default.RemoteEventSubscription)!;
                    RemoteEventSubscription? previous;
                    lock (_gate)
                    {
                        if (!_registered)
                        {
                            _droppedSubscriptions.Add(subscription);
                            return Json("{}");
                        }

                        previous = _subscriptions.LastOrDefault();
                        _subscriptions.Add(subscription);
                    }

                    if (SubscriptionAnswer is { } answer)
                        await answer().WaitAsync(cancellationToken);
                    if (PingOnForegroundReturn && previous is { IsForeground: false } && subscription.IsForeground)
                        Push(RemoteProtocol.Events.Ping, "{}");
                    return Json("{}");
                }

                case RemoteProtocol.Routes.Transcript:
                {
                    Interlocked.Increment(ref _transcripts);
                    var chatId = Guid.Parse(QueryValue(uri, "chatId")!);
                    var known = QueryValue(uri, "known");
                    lock (_gate)
                        _transcriptKnown.Add(known);
                    var transcript = TranscriptResponder is { } responder
                        ? await responder(chatId, known).WaitAsync(cancellationToken)
                        : new RemoteTranscript { ChatId = chatId, Status = new RemoteChatStatus { ChatId = chatId } };
                    return Json(transcript, RemoteJsonContext.Default.RemoteTranscript);
                }

                case RemoteProtocol.Routes.Chats:
                    Interlocked.Increment(ref _chatPages);
                    return Json(new RemoteChatPage
                    {
                        Query = QueryValue(uri, "q") is { Length: > 0 } query ? query : null,
                        ProjectId = Guid.TryParse(QueryValue(uri, "projectId"), out var projectId) ? projectId : null
                    }, RemoteJsonContext.Default.RemoteChatPage);

                case RemoteProtocol.Routes.Command:
                {
                    var command = JsonSerializer.Deserialize(
                        await request.Content!.ReadAsStringAsync(cancellationToken),
                        RemoteJsonContext.Default.RemoteCommand)!;
                    Commands.Enqueue(command);
                    var result = CommandResponder is { } responder
                        ? await responder(command).WaitAsync(cancellationToken)
                        : new RemoteCommandResult { Ok = true, RequestId = command.RequestId };
                    result.RequestId ??= command.RequestId;
                    return Json(result, RemoteJsonContext.Default.RemoteCommandResult);
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private RemoteSnapshot BuildSnapshot(string? knownQuery)
        {
            var known = RemoteProtocol.SnapshotSections.ParseKnown(knownQuery);
            var snapshot = new RemoteSnapshot
            {
                HostName = Snapshot.HostName,
                IsConnected = Snapshot.IsConnected,
                ActiveChatId = Snapshot.ActiveChatId,
                ActiveChat = Snapshot.ActiveChat,
                Capabilities = [.. Capabilities],
                Chats = Snapshot.Chats,
                Library = Snapshot.Library,
                Settings = Snapshot.Settings,
                ChatsVersion = "c1",
                LibraryVersion = "l1",
                SettingsVersion = "s1"
            };
            var unchanged = new List<string>();
            if (known.GetValueOrDefault(RemoteProtocol.SnapshotSections.Chats) == "c1")
            {
                snapshot.Chats = new RemoteChatPage();
                unchanged.Add(RemoteProtocol.SnapshotSections.Chats);
            }

            if (known.GetValueOrDefault(RemoteProtocol.SnapshotSections.Library) == "l1")
            {
                snapshot.Library = new RemoteLibrary();
                unchanged.Add(RemoteProtocol.SnapshotSections.Library);
            }

            if (known.GetValueOrDefault(RemoteProtocol.SnapshotSections.Settings) == "s1")
            {
                snapshot.Settings = new RemoteSettings();
                unchanged.Add(RemoteProtocol.SnapshotSections.Settings);
            }

            snapshot.UnchangedSections = unchanged.Count > 0 ? unchanged : null;
            LastUnchangedSections = snapshot.UnchangedSections;
            return snapshot;
        }

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator > 0 && pair[..separator] == name)
                    return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }

            return null;
        }

        private static HttpResponseMessage Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value, typeInfo), Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class PushStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Push(string wire) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(wire));

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (!_chunks.Reader.TryRead(out _current))
                    continue;
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();
            base.Dispose(disposing);
        }
    }

    private sealed class TrustedRouteVerifier : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => true;
    }

    private sealed class NullSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lumi-mobile-efficiency-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
