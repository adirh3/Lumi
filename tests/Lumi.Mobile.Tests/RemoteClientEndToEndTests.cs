using System.Text.Json;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// End-to-end coverage of the phone half: real sockets, real HTTP, real SSE framing, real JSON,
/// driving the real view models. Only the desktop is substituted, and it speaks the real protocol.
/// </summary>
public class RemoteClientEndToEndTests
{
    private static MobileShellViewModel CreateShell()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("n"));

        return new MobileShellViewModel(
            new LumiRemoteClient("device-1", "Test Phone"),
            new LumiDiscoveryClient(),
            new MobileSettingsStore(dir),
            // Tests have no Avalonia dispatcher: run posted work inline so assertions are deterministic.
            action => action());
    }

    private static async Task WaitAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }

    [Fact]
    public async Task Pairing_RejectsAWrongCodeAndAcceptsTheRightOne()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Start();

        await using var shell = CreateShell();

        shell.Connect.ManualAddress = desktop.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);

        Assert.Equal(ConnectStep.EnterCode, shell.Connect.Step);
        Assert.Equal("TEST-PC", shell.Connect.TargetHostName);

        shell.Connect.PairingCode = "000000";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);

        Assert.Equal(ConnectStep.EnterCode, shell.Connect.Step);
        Assert.Contains("not correct", shell.Connect.ErrorText);
        Assert.False(shell.IsPaired);

        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);

        Assert.True(shell.IsPaired);
        Assert.Equal("TEST-PC", shell.HostName);
    }

    [Fact]
    public async Task PairedSession_UsesTheFirstSseSnapshotWithoutAnAutomaticSnapshotGet()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            HostName = "TEST-PC",
            IsConnected = true,
            Chats = OneChatPage(new RemoteChat
            {
                Id = chatId,
                Title = "Weekend plans",
                Preview = "Let's go"
            }),
            Library = new RemoteLibrary
            {
                Skills = [new RemoteSkill { Id = Guid.NewGuid(), Name = "Document Creator" }]
            },
            Settings = new RemoteSettings { UserName = "Adir", AvailableModels = ["claude-opus-5"] }
        };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);

        Assert.Single(shell.ChatList.Groups);
        Assert.Equal("Today", shell.ChatList.Groups[0].Label);
        Assert.Equal("Weekend plans", shell.ChatList.Groups[0].Chats[0].Title);
        Assert.Equal("Adir", shell.UserName);
        Assert.Contains("claude-opus-5", shell.Chat.AvailableModels);
        Assert.Equal(1, desktop.EventRequestCount);
        Assert.Equal(0, desktop.SnapshotRequestCount);

        shell.Library.Section = LibrarySection.Skills;
        Assert.Equal("Document Creator", shell.Library.Entries[0].Name);
    }

    [Fact]
    public async Task ServerPagedHistoryLoadsMoreAndSearchesBeyondTheBootstrapPage()
    {
        await using var desktop = new FakeLumiDesktop();
        var chats = Enumerable.Range(0, 500)
            .Select(index => new RemoteChat
            {
                Id = Guid.NewGuid(),
                Title = $"Chat {index:D3}",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-index)
            })
            .ToList();
        desktop.ChatCatalog = [new RemoteChatGroup { Label = "History", Chats = chats }];
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = new RemoteChatPage
            {
                TotalCount = chats.Count,
                HasMore = true,
                Groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "History",
                        Chats = [.. chats.Take(RemoteProtocol.ChatPageSize)]
                    }
                ]
            }
        };
        desktop.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, desktop);

        Assert.Equal(RemoteProtocol.ChatPageSize, shell.ChatList.VisibleChatCount);
        await shell.ChatList.LoadMoreChatsCommand.ExecuteAsync(null);
        await WaitAsync(
            () => shell.ChatList.VisibleChatCount == RemoteProtocol.ChatPageSize * 2,
            "the second chat page");

        shell.ChatList.SearchText = "Chat 499";
        await WaitAsync(
            () =>
            {
                var visible = shell.ChatList.Groups.SelectMany(group => group.Chats).ToList();
                return visible.Count == 1 && visible[0].Title == "Chat 499";
            },
            "a server-side search result outside the bootstrap page");
        Assert.Equal(1, shell.ChatList.MatchingChatCount);
    }

    [Fact]
    public async Task ChangingHostsCancelsAnInFlightChatPageWithoutSurfacingAnError()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = Guid.NewGuid(), Title = "Chat" })
        };
        desktop.ChatRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.ReleaseChatResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, desktop);

        var pageRequest = shell.GetChatPageAsync(
            0,
            RemoteProtocol.ChatPageSize,
            query: null,
            projectId: null,
            CancellationToken.None);
        await desktop.ChatRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await shell.ForgetPcLocallyCommand.ExecuteAsync(null);
            var page = await pageRequest.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Null(page);
            Assert.False(shell.IsPaired);
        }
        finally
        {
            desktop.ReleaseChatResponse.TrySetResult();
        }
    }

    [Fact]
    public async Task InitialSnapshot_AdoptsTheDesktopsActiveChat()
    {
        await using var desktop = new FakeLumiDesktop();
        var activeChatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            ActiveChatId = activeChatId,
            ActiveChat = new RemoteChat { Id = activeChatId, Title = "Desktop active" },
            Chats = OneChatPage(new RemoteChat { Id = activeChatId, Title = "Desktop active" })
        };
        desktop.Transcript = new RemoteTranscript
        {
            ChatId = activeChatId,
            Title = "Desktop active",
            Revision = 1
        };
        desktop.Start();

        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        await WaitAsync(() => shell.Chat.ChatId == activeChatId, "the initial active chat to be adopted");
        await WaitAsync(() => desktop.TranscriptRequestCount == 1, "the active transcript to be fetched once");

        Assert.Equal(activeChatId, shell.Chat.ChatId);
        Assert.Equal(activeChatId, shell.ChatList.SelectedChatId);
        Assert.Equal("Desktop active", shell.Chat.Title);
        Assert.Equal(1, desktop.EventRequestCount);
        Assert.Equal(0, desktop.SnapshotRequestCount);
        Assert.Equal(1, desktop.TranscriptRequestCount);
    }

    [Fact]
    public async Task LaterSnapshot_DoesNotHijackAnExplicitBlankChatOrItsStagedState()
    {
        await using var desktop = new FakeLumiDesktop();
        var desktopChatId = Guid.NewGuid();
        desktop.Snapshot = Snapshot("Before snapshot");
        desktop.Transcript = new RemoteTranscript
        {
            ChatId = desktopChatId,
            Title = "Desktop active",
            Revision = 1
        };
        desktop.Start();

        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        await desktop.SubscriberConnected.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(() => shell.Chat.ChatId == desktopChatId, "the initial active chat to be adopted");

        shell.ChatList.NewChatCommand.Execute(null);
        shell.Chat.PromptText = "phone-only draft";
        shell.Chat.Model = "gpt-5.6";
        shell.Chat.Quality = "high";
        shell.Chat.ContextWindowTier = "Long context";
        shell.Chat.ProjectName = "Mobile project";
        await shell.Chat.AttachFileAsync("draft.txt", new byte[] { 1, 2, 3 });

        var stagedAttachment = Assert.Single(shell.Chat.Attachments);

        desktop.Snapshot = Snapshot("After snapshot");
        await desktop.PushAsync(
            RemoteProtocol.Events.Snapshot,
            JsonSerializer.Serialize(desktop.Snapshot, RemoteJsonContext.Default.RemoteSnapshot));
        await WaitAsync(() => shell.UserName == "After snapshot", "the later snapshot to be applied");

        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        Assert.Equal("phone-only draft", shell.Chat.PromptText);
        Assert.Equal("gpt-5.6", shell.Chat.Model);
        Assert.Equal("high", shell.Chat.Quality);
        Assert.Equal("Long context", shell.Chat.ContextWindowTier);
        Assert.Equal("Mobile project", shell.Chat.ProjectName);
        Assert.Equal(stagedAttachment, Assert.Single(shell.Chat.Attachments));
        Assert.True(shell.Chat.HasPendingConfiguration);

        RemoteSnapshot Snapshot(string userName) => new()
        {
            ActiveChatId = desktopChatId,
            ActiveChat = new RemoteChat { Id = desktopChatId, Title = "Desktop active" },
            Chats = OneChatPage(new RemoteChat { Id = desktopChatId, Title = "Desktop active" }),
            Settings = new RemoteSettings
            {
                UserName = userName,
                PreferredModel = "claude-opus-5",
                AvailableModels = ["claude-opus-5", "gpt-5.6"],
                ModelReasoningEfforts = ["gpt-5.6=low,medium,high"],
                ModelContextWindowTiers = ["gpt-5.6=Default,Long context"]
            }
        };
    }

    [Fact]
    public async Task OpeningAChat_LoadsTheTranscriptAndNavigatesToTheDetailPane()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Trip" })
        };
        desktop.Transcript = new RemoteTranscript
        {
            ChatId = chatId,
            Title = "Trip",
            Revision = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "t1",
                    Items =
                    [
                        new RemoteTranscriptItem { Id = "u1", Kind = RemoteProtocol.ItemKinds.User, Text = "Hi" },
                        new RemoteTranscriptItem { Id = "a1", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "Hello!" }
                    ]
                }
            ]
        };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);

        // Compact phone: picking a chat from the drawer must land on the conversation and dismiss
        // the drawer, so the user sees what they asked for instead of the list they asked from.
        shell.UpdateLayout(393, 852);
        shell.IsDrawerOpen = true;
        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);

        await WaitAsync(() => shell.Chat.Turns.Count > 0, "the transcript to load");

        Assert.Equal(chatId, shell.Chat.ChatId);
        Assert.True(shell.IsChatPage);
        Assert.False(shell.IsDrawerOpen);

        var items = shell.Chat.Turns[0].Items;
        Assert.IsType<UserTurnItemViewModel>(items[0]);
        Assert.IsType<AssistantItemViewModel>(items[1]);
        Assert.Equal("Hello!", ((AssistantItemViewModel)items[1]).Text);
    }

    [Fact]
    public async Task LiveStream_AppliesStatusAndStreamingDeltasWithoutRefetching()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Live" })
        };
        desktop.Transcript = new RemoteTranscript
        {
            ChatId = chatId,
            Title = "Live",
            Revision = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "t1",
                    Items = [new RemoteTranscriptItem { Id = "a1", Kind = RemoteProtocol.ItemKinds.Assistant, Text = "" }]
                }
            ]
        };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);
        await desktop.SubscriberConnected.WaitAsync(TimeSpan.FromSeconds(10));

        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);
        await WaitAsync(() => shell.Chat.Turns.Count > 0, "the transcript to load");

        await desktop.PushAsync(RemoteProtocol.Events.ChatStatus, JsonSerializer.Serialize(
            new RemoteChatStatus { ChatId = chatId, IsBusy = true, IsStreaming = true, StatusText = "Thinking" },
            RemoteJsonContext.Default.RemoteChatStatus));

        await WaitAsync(() => shell.Chat.IsStreaming, "the streaming flag to arrive");
        Assert.Equal("Thinking", shell.Chat.StatusText);

        await desktop.PushAsync(RemoteProtocol.Events.StreamDelta, JsonSerializer.Serialize(
            new RemoteStreamDelta { ChatId = chatId, ItemId = "a1", Text = "Hello from your PC" },
            RemoteJsonContext.Default.RemoteStreamDelta));

        await WaitAsync(
            () => shell.Chat.Turns[0].Items[0] is AssistantItemViewModel { Text.Length: > 0 },
            "the streaming delta to land");

        Assert.Equal("Hello from your PC", ((AssistantItemViewModel)shell.Chat.Turns[0].Items[0]).Text);
    }

    [Fact]
    public async Task TranscriptInvalidation_MakesThePhoneRefetchTheTranscript()
    {
        // Regression: the desktop broadcast {"chatId":…,"revision":…} while the client parsed the
        // payload as a bare GUID. Every invalidation was silently dropped, so a phone watching a live
        // chat sat on a stale transcript forever while the PC happily finished the turn.
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Live" })
        };
        desktop.Transcript = TranscriptWith(chatId, 1, ("u1", RemoteProtocol.ItemKinds.User, "Reply with exactly: PHONE_E2E_OK"));
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);
        await desktop.SubscriberConnected.WaitAsync(TimeSpan.FromSeconds(10));

        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);

        // Settle the open's own fetch before changing what the desktop would serve next.
        await WaitAsync(() => shell.Chat.Turns.Count == 1 && shell.Chat.Turns[0].Items.Count == 1,
            "the initial transcript to load");

        // The desktop finishes the turn: new content plus the invalidation that announces it.
        desktop.Transcript = TranscriptWith(chatId, 2,
            ("u1", RemoteProtocol.ItemKinds.User, "Reply with exactly: PHONE_E2E_OK"),
            ("a1", RemoteProtocol.ItemKinds.Assistant, "PHONE_E2E_OK"));

        await desktop.PushAsync(RemoteProtocol.Events.TranscriptInvalidated, JsonSerializer.Serialize(
            new RemoteTranscriptInvalidated { ChatId = chatId, Revision = 2 },
            RemoteJsonContext.Default.RemoteTranscriptInvalidated));

        await WaitAsync(() => shell.Chat.Turns[0].Items.Count == 2,
            "the phone to refetch after the invalidation");

        var refreshed = shell.Chat.Turns[0].Items;
        Assert.IsType<UserTurnItemViewModel>(refreshed[0]);
        Assert.Equal("PHONE_E2E_OK", Assert.IsType<AssistantItemViewModel>(refreshed[1]).Text);
    }

    [Fact]
    public async Task TranscriptInvalidation_ForAnotherChatIsIgnored()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Live" })
        };
        desktop.Transcript = TranscriptWith(chatId, 1, ("u1", RemoteProtocol.ItemKinds.User, "Only me"));
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);
        await desktop.SubscriberConnected.WaitAsync(TimeSpan.FromSeconds(10));

        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);
        await WaitAsync(() => shell.Chat.Turns.Count == 1 && shell.Chat.Turns[0].Items.Count == 1,
            "the initial transcript to load");

        desktop.Transcript = TranscriptWith(chatId, 2,
            ("u1", RemoteProtocol.ItemKinds.User, "Only me"),
            ("a1", RemoteProtocol.ItemKinds.Assistant, "leaked"));

        await desktop.PushAsync(RemoteProtocol.Events.TranscriptInvalidated, JsonSerializer.Serialize(
            new RemoteTranscriptInvalidated { ChatId = Guid.NewGuid(), Revision = 2 },
            RemoteJsonContext.Default.RemoteTranscriptInvalidated));

        await Task.Delay(400);
        Assert.Single(shell.Chat.Turns[0].Items);
    }

    private static RemoteTranscript TranscriptWith(
        Guid chatId, long revision, params (string Id, string Kind, string Text)[] items) =>
        new()
        {
            ChatId = chatId,
            Title = "Live",
            Revision = revision,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "t1",
                    Items = [.. items.Select(i => new RemoteTranscriptItem { Id = i.Id, Kind = i.Kind, Text = i.Text })]
                }
            ]
        };

    [Fact]
    public async Task SendingAMessage_IssuesTheRealCommandAndFlipsTheComposerToBusy()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Send" })
        };
        desktop.Transcript = new RemoteTranscript { ChatId = chatId, Title = "Send", Revision = 1 };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);
        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);
        await WaitAsync(() => !shell.Chat.IsLoading, "the chat to open");

        shell.Chat.PromptText = "  What's on my calendar?  ";
        await shell.Chat.SendCommand.ExecuteAsync(null);

        Assert.True(shell.Chat.IsBusy);
        Assert.Equal("", shell.Chat.PromptText);

        RemoteCommand? sent;
        lock (desktop.ReceivedCommands)
            sent = desktop.ReceivedCommands.LastOrDefault(c => c.Action == RemoteProtocol.Actions.SendMessage);

        Assert.NotNull(sent);
        Assert.Equal("What's on my calendar?", sent!.Get("message"));
        Assert.Equal(chatId.ToString(), sent.Get("chatId"));
    }

    /// <summary>
    /// Pressing Send mid-turn must steer the live turn, not bounce off it. The phone requests that
    /// explicitly with a <c>steer</c> flag and immediately labels the optimistic bubble so a tool
    /// that can only consume steering at its next safe boundary does not look like a frozen send.
    /// </summary>
    [Fact]
    public async Task SendingWhileBusy_AsksTheDesktopToSteerInsteadOfRacingIt()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Steer" })
        };
        desktop.Transcript = new RemoteTranscript { ChatId = chatId, Title = "Steer", Revision = 1 };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);
        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);
        await WaitAsync(() => !shell.Chat.IsLoading, "the chat to open");

        // A quiet chat must not ask to steer — steering aborts whatever is running.
        shell.Chat.PromptText = "first";
        await shell.Chat.SendCommand.ExecuteAsync(null);
        Assert.NotEqual(true, LastSend(desktop)!.GetBool("steer"));

        shell.Chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, IsBusy = true });
        Assert.True(shell.Chat.IsBusy);

        shell.Chat.PromptText = "steered";
        await shell.Chat.SendCommand.ExecuteAsync(null);

        var sent = LastSend(desktop);
        Assert.Equal("steered", sent!.Get("message"));
        Assert.Equal(true, sent.GetBool("steer"));

        var steeringBubble = shell.Chat.Turns
            .SelectMany(turn => turn.Items)
            .OfType<UserTurnItemViewModel>()
            .Last(item => item.Text == "steered");
        Assert.True(steeringBubble.HasSteerStatus);
        Assert.Equal("Steering...", steeringBubble.SteerStatusText);

        static RemoteCommand? LastSend(FakeLumiDesktop desktop)
        {
            lock (desktop.ReceivedCommands)
                return desktop.ReceivedCommands.LastOrDefault(c => c.Action == RemoteProtocol.Actions.SendMessage);
        }
    }

    /// <summary>
    /// "New chat" must not create anything on the PC. It used to fire <c>create_chat</c> straight
    /// away, so every tap left an empty "New Chat" in history — and tapping a few times while
    /// deciding what to ask littered the list. A new chat is an intent; it exists once you speak.
    /// </summary>
    [Fact]
    public async Task NewChat_CreatesNothingUntilTheFirstMessage()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = Guid.NewGuid(), Title = "Existing" })
        };
        desktop.Start();

        await using var shell = CreateShell();
        await PairAsync(shell, desktop);

        shell.ChatList.NewChatCommand.Execute(null);

        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        Assert.Empty(Received(desktop, RemoteProtocol.Actions.CreateChat));

        Assert.Empty(desktop.ReceivedCommands);

        shell.Chat.PromptText = "now it exists";
        await shell.Chat.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(Received(desktop, RemoteProtocol.Actions.SendMessage));
        Assert.Equal("now it exists", sent.Get("message"));
        Assert.Null(sent.Get("chatId"));
    }

    /// <summary>
    /// A chat started while a project is selected has to land in that project. With creation
    /// deferred, the project can no longer ride on <c>create_chat</c> — it travels with the send
    /// that creates the chat instead.
    /// </summary>
    [Fact]
    public async Task NewChatInAProject_CarriesTheProjectOnTheCreatingSend()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Snapshot = new RemoteSnapshot
        {
            Library = new RemoteLibrary
            {
                Projects = [new RemoteProject { Id = Guid.NewGuid(), Name = "Lumi" }]
            }
        };
        var projectId = desktop.Snapshot.Library.Projects[0].Id;
        desktop.Start();

        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        await WaitAsync(() => shell.Projects.Count > 0, "the project list to arrive");

        shell.SelectProjectCommand.Execute(shell.Projects[0]);
        shell.ChatList.NewChatCommand.Execute(null);

        shell.Chat.PromptText = "scoped";
        await shell.Chat.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(Received(desktop, RemoteProtocol.Actions.SendMessage));
        Assert.Equal(projectId.ToString(), sent.Get("projectId"));
    }

    /// <summary>
    /// Attaching uploads immediately and the path travels with the next message, because Lumi reads
    /// files by path. Uploading at send time would mean discovering a failed upload after the
    /// message had already gone.
    /// </summary>
    [Fact]
    public async Task AttachedFile_UploadsOnPickAndIsNamedInTheMessage()
    {
        await using var desktop = new FakeLumiDesktop();
        var chatId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = chatId, Title = "Files" })
        };
        desktop.Transcript = new RemoteTranscript { ChatId = chatId, Title = "Files", Revision = 1 };
        desktop.Start();

        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        shell.ChatList.OpenChatCommand.Execute(shell.ChatList.Groups[0].Chats[0]);
        await WaitAsync(() => !shell.Chat.IsLoading, "the chat to open");

        await shell.Chat.AttachFileAsync("report.pdf", new byte[] { 1, 2, 3 });

        Assert.True(shell.Chat.HasAttachments);
        var staged = Assert.Single(shell.Chat.Attachments);
        Assert.Equal("report.pdf", staged.FileName);
        Assert.Equal(desktop.UploadedPath, staged.Path);

        shell.Chat.PromptText = "what is in this";
        await shell.Chat.SendCommand.ExecuteAsync(null);

        var sent = Assert.Single(Received(desktop, RemoteProtocol.Actions.SendMessage));
        Assert.Contains("what is in this", sent.Get("message"));
        Assert.Contains(staged.Path, sent.Get("message"));

        // Staged files belong to the message that carried them.
        Assert.False(shell.Chat.HasAttachments);
    }

    /// <summary>
    /// The only caller of <see cref="MobileChatViewModel.AttachFileAsync"/> is an <c>async void</c>
    /// event handler, so an exception escaping this method does not surface as an error message — it
    /// is rethrown on the synchronization context and kills the app.
    ///
    /// <para>Cancellation is the realistic trigger: the transport deliberately rethrows
    /// <see cref="OperationCanceledException"/> rather than converting it to an error response, so
    /// backgrounding the app or disconnecting mid-upload throws straight through this method.</para>
    /// </summary>
    [Theory]
    [InlineData("cancelled")]
    [InlineData("unexpected")]
    public async Task AttachFailingMidUpload_ReportsInsteadOfThrowing(string failure)
    {
        var sink = new ThrowingSink(
            failure == "cancelled"
                ? new OperationCanceledException()
                : new InvalidOperationException("transport blew up"));

        var chat = new MobileChatViewModel(sink);

        var exception = await Record.ExceptionAsync(
            () => chat.AttachFileAsync("report.pdf", new byte[] { 1, 2, 3 }));

        Assert.Null(exception);
        Assert.False(chat.HasAttachments);
        Assert.False(chat.IsUploading);
        Assert.False(string.IsNullOrWhiteSpace(chat.ErrorText));
    }

    private sealed class ThrowingSink(Exception failure) : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            throw failure;
    }

    /// <summary>A sink whose send never completes, standing in for a slow or lossy phone network.</summary>
    private sealed class NeverCompletingSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            new TaskCompletionSource<RemoteCommandResult>().Task;

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            new TaskCompletionSource<RemoteUploadResponse>().Task;
    }

    private sealed class DeferredSendSink : IRemoteCommandSink
    {
        private readonly TaskCompletionSource<RemoteCommandResult> _sendCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RemoteCommand> Commands { get; } = [];

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return command.Action == RemoteProtocol.Actions.SendMessage
                ? _sendCompletion.Task
                : Task.FromResult(new RemoteCommandResult { Ok = true });
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });

        public void CompleteSend(Guid chatId) =>
            _sendCompletion.SetResult(new RemoteCommandResult { Ok = true, ChatId = chatId });
    }

    /// <summary>
    /// Tapping send must show progress on the very next frame, with no network in the path.
    ///
    /// <para>The real reply needs an HTTP round trip, an SSE invalidation and a transcript refetch —
    /// three hops on a phone's Wi-Fi. Waiting for any of them before showing the thinking indicator
    /// means staring at an unchanged screen, unable to tell whether the tap even registered.</para>
    /// </summary>
    [Fact]
    public async Task Sending_ShowsProgressBeforeTheNetworkAnswers()
    {
        var chat = new MobileChatViewModel(new NeverCompletingSink())
        {
            ChatId = Guid.NewGuid(),
            PromptText = "hello"
        };

        Assert.False(chat.ShowThinking);

        // Deliberately NOT awaited: the send never completes, which is the whole point — everything
        // asserted below has to be true while the request is still in flight.
        _ = chat.SendCommand.ExecuteAsync(null);

        Assert.True(
            chat.ShowThinking,
            "the shared tail indicator must appear in the same optimistic frame as the user's message");
        Assert.True(chat.IsBusy);
        Assert.Equal("", chat.PromptText);

        // And the user's own message must already be on screen.
        Assert.Contains(
            chat.Turns.SelectMany(turn => turn.Items),
            item => item is UserTurnItemViewModel { Text: "hello" });
        Assert.DoesNotContain(
            chat.Turns.SelectMany(turn => turn.Items),
            item => item is AssistantItemViewModel { Text: "" });
    }

    [Fact]
    public async Task Sending_RetainsOptimisticMessageAcrossEqualRevisionTranscript()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new NeverCompletingSink())
        {
            ChatId = chatId
        };
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 5,
            IsLatestWindow = true
        });
        chat.PromptText = "hello";

        _ = chat.SendCommand.ExecuteAsync(null);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 5,
            IsLatestWindow = true
        });

        Assert.Contains(
            chat.Turns.SelectMany(turn => turn.Items),
            item => item is UserTurnItemViewModel { Text: "hello" });
        Assert.True(chat.ShowThinking);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 6,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "authoritative",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "user-1",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = "hello"
                        }
                    ]
                }
            ]
        });

        Assert.Single(
            chat.Turns.SelectMany(turn => turn.Items).OfType<UserTurnItemViewModel>(),
            item => item.Text == "hello");
    }

    [Fact]
    public async Task Sending_KeepsProgressUntilVisibleResponseActivity()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new NeverCompletingSink())
        {
            ChatId = chatId
        };
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 5,
            IsLatestWindow = true
        });
        chat.PromptText = "hello";

        _ = chat.SendCommand.ExecuteAsync(null);
        Assert.True(chat.ShowThinking);

        chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = chatId,
            IsBusy = true
        });
        Assert.True(chat.ShowThinking);

        // Busy can return to idle before the transcript carrying the first visible response row.
        // The progress affordance must bridge that event-ordering gap.
        chat.ApplyStatus(new RemoteChatStatus
        {
            ChatId = chatId,
            IsBusy = false,
            IsStreaming = false
        });
        Assert.True(chat.IsBusy);
        Assert.True(chat.ShowThinking);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 6,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "authoritative",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "user-1",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = "hello"
                        }
                    ]
                }
            ]
        });
        Assert.True(chat.ShowThinking);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 7,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "authoritative",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "user-1",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = "hello"
                        },
                        new RemoteTranscriptItem
                        {
                            Id = "reasoning-1",
                            Kind = RemoteProtocol.ItemKinds.Reasoning,
                            Text = "Checking the request"
                        }
                    ]
                }
            ]
        });

        Assert.False(chat.ShowThinking);
    }

    [Fact]
    public void ExistingAssistantHistory_DoesNotDismissNewTurnProgress()
    {
        var chatId = Guid.NewGuid();
        var chat = new MobileChatViewModel(new NeverCompletingSink())
        {
            ChatId = chatId
        };
        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 5,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "old-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "old-assistant",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = "Earlier answer"
                        }
                    ]
                }
            ]
        });

        chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, IsBusy = true });
        Assert.True(chat.ShowThinking);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 6,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "old-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "old-assistant",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = "Earlier answer"
                        }
                    ]
                }
            ],
            Status = new RemoteChatStatus { ChatId = chatId, IsBusy = true }
        });
        Assert.True(chat.ShowThinking);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = chatId,
            Revision = 7,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "old-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "old-assistant",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = "Earlier answer"
                        },
                        new RemoteTranscriptItem
                        {
                            Id = "new-reasoning",
                            Kind = RemoteProtocol.ItemKinds.Reasoning,
                            Text = "New work"
                        }
                    ]
                }
            ],
            Status = new RemoteChatStatus { ChatId = chatId, IsBusy = true }
        });

        Assert.False(chat.ShowThinking);
    }

    [Fact]
    public async Task StoppingFirstBlankChatSend_StopsTheCreatedDesktopTurn()
    {
        var sink = new DeferredSendSink();
        var chat = new MobileChatViewModel(sink)
        {
            PromptText = "hello"
        };

        var send = chat.SendCommand.ExecuteAsync(null);
        Assert.True(chat.ShowThinking);

        await chat.StopCommand.ExecuteAsync(null);
        Assert.False(chat.IsBusy);
        Assert.False(chat.ShowThinking);

        var createdChatId = Guid.NewGuid();
        sink.CompleteSend(createdChatId);
        await send;

        Assert.Contains(
            sink.Commands,
            command =>
                command.Action == RemoteProtocol.Actions.StopGeneration
                && command.Arguments.TryGetValue("chatId", out var chatId)
                && chatId == createdChatId.ToString());
    }

    /// <summary>
    /// A file past the protocol ceiling must be refused before it is encoded. Base64 costs about
    /// 1.33x the payload on top of the payload itself, so "encode first, check later" is how a phone
    /// gets killed for memory rather than shown a message.
    /// </summary>
    [Fact]
    public async Task AttachTooLarge_IsRefusedBeforeEncoding()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Start();

        await using var shell = CreateShell();
        await PairAsync(shell, desktop);

        var oversize = new byte[RemoteProtocol.MaxUploadBytes + 1];
        var refused = await shell.Client.UploadAsync("huge.bin", oversize, CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Contains("too large", refused.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(desktop.UploadedPath);
    }

    private static List<RemoteCommand> Received(FakeLumiDesktop desktop, string action)
    {
        lock (desktop.ReceivedCommands)
            return desktop.ReceivedCommands.Where(command => command.Action == action).ToList();
    }

    [Fact]
    public async Task LibraryEdit_RoundTripsThroughConfigureFeature()
    {
        await using var desktop = new FakeLumiDesktop();
        var skillId = Guid.NewGuid();
        desktop.Snapshot = new RemoteSnapshot
        {
            Library = new RemoteLibrary
            {
                Skills = [new RemoteSkill { Id = skillId, Name = "Trip planner", Content = "old" }]
            }
        };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);

        shell.Library.Section = LibrarySection.Skills;
        await shell.Library.BeginEditCommand.ExecuteAsync(shell.Library.Entries[0]);
        Assert.True(shell.Library.IsEditing);
        Assert.Equal("Trip planner", shell.Library.EditName);

        shell.Library.EditBody = "new content";
        await shell.Library.SaveCommand.ExecuteAsync(null);

        Assert.False(shell.Library.IsEditing);

        RemoteCommand? sent;
        lock (desktop.ReceivedCommands)
            sent = desktop.ReceivedCommands.LastOrDefault(c => c.Action == RemoteProtocol.Actions.ConfigureFeature);

        Assert.NotNull(sent);
        Assert.Equal(RemoteProtocol.Resources.Skills, sent!.Get("resource"));
        Assert.Equal("update", sent.Get("featureAction"));
        Assert.Equal(skillId.ToString(), sent.Get("identifier"));
        Assert.Equal("new content", sent.Get("content"));
    }

    [Fact]
    public async Task Discovery_FindsTheDesktopOverUdp()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Start();
        var discoveryPort = FakeLumiDesktop.GetFreeUdpPort();
        desktop.StartDiscovery(discoveryPort);

        var discovery = new LumiDiscoveryClient(discoveryPort);
        var found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(found, b => b.Port == desktop.Port && b.HostName == "TEST-PC");
    }

    [Fact]
    public async Task Unpairing_ClearsLocalStateAndReturnsToTheConnectScreen()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Snapshot = new RemoteSnapshot
        {
            Chats = OneChatPage(new RemoteChat { Id = Guid.NewGuid(), Title = "X" })
        };
        desktop.Start();

        await using var shell = CreateShell();

        await PairAsync(shell, desktop);
        Assert.NotEmpty(shell.ChatList.Groups);

        await shell.ForgetPcCommand.ExecuteAsync(null);

        Assert.False(shell.IsPaired);
        Assert.False(shell.IsConnected);
        Assert.Empty(shell.ChatList.Groups);
        Assert.Equal(Guid.Empty, shell.Chat.ChatId);
    }

    [Fact]
    public async Task FailedRevocationKeepsCredentialsUntilTheUserExplicitlyRemovesThemLocally()
    {
        await using var desktop = new FakeLumiDesktop
        {
            CommandResultFactory = command =>
                command?.Action == RemoteProtocol.Actions.RevokeDevice
                    ? new RemoteCommandResult { Ok = false, Error = "Disk is full." }
                    : new RemoteCommandResult { Ok = true, Message = "ok" }
        };
        desktop.Start();
        await using var shell = CreateShell();
        await PairAsync(shell, desktop);
        var token = shell.Client.Token;

        await shell.ForgetPcCommand.ExecuteAsync(null);

        Assert.True(shell.IsPaired);
        Assert.Equal(token, shell.Client.Token);
        Assert.Contains("Disk is full", shell.ConnectionMessage);

        await shell.ForgetPcLocallyCommand.ExecuteAsync(null);
        Assert.False(shell.IsPaired);
        Assert.Null(shell.Client.Token);
    }

    [Fact]
    public async Task PairedCredentials_SurviveARestart()
    {
        await using var desktop = new FakeLumiDesktop();
        desktop.Start();

        var dir = Path.Combine(Path.GetTempPath(), "lumi-mobile-tests", Guid.NewGuid().ToString("n"));
        var store = new MobileSettingsStore(dir);

        await using (var first = new MobileShellViewModel(
                         new LumiRemoteClient("device-1", "Test Phone"),
                         new LumiDiscoveryClient(),
                         store,
                         action => action()))
        {
            first.Connect.ManualAddress = desktop.BaseUrl;
            await first.Connect.ConnectManuallyCommand.ExecuteAsync(null);
            first.Connect.PairingCode = "123456";
            await first.Connect.SubmitCodeCommand.ExecuteAsync(null);
            Assert.True(first.IsPaired);
        }

        await using var second = new MobileShellViewModel(
            new LumiRemoteClient("device-1", "Test Phone"),
            new LumiDiscoveryClient(),
            new MobileSettingsStore(dir),
            action => action());

        Assert.True(second.IsPaired);
        Assert.Equal("TEST-PC", second.HostName);
    }

    private static RemoteChatPage OneChatPage(RemoteChat chat) => new()
    {
        TotalCount = 1,
        Groups = [new RemoteChatGroup { Label = "Today", Chats = [chat] }]
    };

    private static async Task PairAsync(MobileShellViewModel shell, FakeLumiDesktop desktop)
    {
        var bootstrapCount = shell.BootstrapSnapshotCount;
        shell.Connect.ManualAddress = desktop.BaseUrl;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(shell.IsPaired);
        await WaitAsync(
            () => shell.BootstrapSnapshotCount > bootstrapCount,
            "the first SSE snapshot to be applied");
    }
}
