using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class TranscriptRefreshStateMachineTests
{
    private const string BaseUrl = "http://lumi.test:47653";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PairingBootstrap_UsesOneSseZeroSnapshotGetsAndOneTranscriptGet()
    {
        var chatId = Guid.NewGuid();
        var snapshot = Snapshot(chatId, "Paired active");
        var handler = new ControlledRemoteHandler
        {
            Snapshot = snapshot,
            TranscriptResponder = (_, _) => Task.FromResult(
                Transcript(chatId, "Paired transcript", revision: 1, 0, 1, 1, ["paired"]))
        };

        await using var shell = CreateShell(handler);
        shell.Connect.ManualAddress = BaseUrl;
        shell.Connect.AllowInsecureLanDiscovery = true;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);

        Assert.True(shell.IsPaired);
        Assert.Equal(1, await handler.NextEventRequestAsync());

        handler.LiveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, snapshot));
        var transcript = await handler.NextTranscriptRequestAsync();
        Assert.Equal(chatId, transcript.ChatId);
        Assert.Null(transcript.BeforeMessageIndex);
        await WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.Title),
            () => shell.Chat.Title == "Paired transcript");

        Assert.Equal(1, handler.EventRequestCount);
        Assert.Equal(0, handler.SnapshotRequestCount);
        Assert.Equal(1, handler.TranscriptRequestCount);

        var catalogApplied = WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.StatusText),
            () => shell.Chat.StatusText == "catalog-applied");
        handler.LiveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, snapshot));
        handler.LiveEvents.Push(Frame(
            RemoteProtocol.Events.ChatStatus,
            new RemoteChatStatus { ChatId = chatId, StatusText = "catalog-applied" }));
        await catalogApplied;

        Assert.Equal(1, handler.TranscriptRequestCount);

        await shell.RefreshSnapshotAsync();

        Assert.Equal(1, handler.SnapshotRequestCount);
        Assert.Equal(1, handler.TranscriptRequestCount);
    }

    [Fact]
    public async Task StoredCredentialStartup_UsesOneSseZeroSnapshotGetsAndOneTranscriptGet()
    {
        var chatId = Guid.NewGuid();
        var snapshot = Snapshot(chatId, "Stored active");
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (_, _) => Task.FromResult(
                Transcript(chatId, "Stored transcript", revision: 1, 0, 1, 1, ["stored"]))
        };
        var storePath = Path.Combine(
            Path.GetTempPath(),
            "lumi-mobile-startup-tests",
            Guid.NewGuid().ToString("n"));
        var store = new MobileSettingsStore(storePath);
        store.Save(new MobileConnectionSettings
        {
            DeviceId = "stored-device",
            DeviceName = "Stored Phone",
            BaseUrl = BaseUrl,
            Token = "test-token",
            HostName = "TEST-PC"
        });
        var client = new LumiRemoteClient(
            "stored-device",
            "Stored Phone",
            handler,
            requestDeadline: TestTimeout,
            uploadDeadline: TestTimeout);

        await using var shell = new MobileShellViewModel(
            client,
            new LumiDiscoveryClient(),
            store,
            action => action());

        Assert.True(shell.IsPaired);
        await shell.StartAsync();
        Assert.Equal(1, await handler.NextEventRequestAsync());

        handler.LiveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, snapshot));
        var transcript = await handler.NextTranscriptRequestAsync();
        Assert.Equal(chatId, transcript.ChatId);
        Assert.Null(transcript.BeforeMessageIndex);
        await WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.Title),
            () => shell.Chat.Title == "Stored transcript");

        Assert.Equal(1, handler.EventRequestCount);
        Assert.Equal(0, handler.SnapshotRequestCount);
        Assert.Equal(1, handler.TranscriptRequestCount);
    }

    [Fact]
    public async Task ReconnectBootstrap_UsesOneNewSseZeroSnapshotGetsAndOneTranscriptGet()
    {
        var chatId = Guid.NewGuid();
        var snapshot = Snapshot(chatId, "Current");
        var liveEvents = new AsyncSseStream();
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (request, _) => Task.FromResult(
                Transcript(
                    chatId,
                    $"Reconnect {request.Ordinal}",
                    revision: request.Ordinal,
                    0,
                    1,
                    1,
                    [$"reconnect-{request.Ordinal}"]))
        };
        handler.EventResponseFactory = ordinal => ordinal == 1
            ? ControlledRemoteHandler.EventResponse(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(Frame(RemoteProtocol.Events.Snapshot, snapshot)),
                    writable: false))
            : ControlledRemoteHandler.EventResponse(liveEvents);

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "Current");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "Current", revision: 0, 0, 1, 1, ["current"]));

        try
        {
            await shell.Client.StartEventStreamAsync();
            Assert.Equal(1, await handler.NextEventRequestAsync());
            var initial = await handler.NextTranscriptRequestAsync();
            Assert.Equal(1, initial.Ordinal);

            Assert.Equal(2, await handler.NextEventRequestAsync());
            liveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, snapshot));
            var reconnect = await handler.NextTranscriptRequestAsync();
            Assert.Equal(2, reconnect.Ordinal);
            Assert.Equal(chatId, reconnect.ChatId);

            Assert.Equal(2, handler.EventRequestCount);
            Assert.Equal(0, handler.SnapshotRequestCount);
            Assert.Equal(2, handler.TranscriptRequestCount);

            var catalogApplied = WaitForPropertyAsync(
                shell.Chat,
                nameof(MobileChatViewModel.StatusText),
                () => shell.Chat.StatusText == "reconnect-catalog-applied");
            liveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, snapshot));
            liveEvents.Push(Frame(
                RemoteProtocol.Events.ChatStatus,
                new RemoteChatStatus
                {
                    ChatId = chatId,
                    StatusText = "reconnect-catalog-applied"
                }));
            await catalogApplied;

            Assert.Equal(2, handler.TranscriptRequestCount);
        }
        finally
        {
            liveEvents.Complete();
        }
    }

    [Fact]
    public async Task PairReconnectSnapshotAndInvalidationBurst_CoalescesToOneTrailingGetAndRejectsStaleApply()
    {
        var chatId = Guid.NewGuid();
        var stale = Transcript(chatId, "STALE", revision: 1, 0, 100, 100, ["stale-turn"]);
        var fresh = Transcript(chatId, "FRESH", revision: 2, 0, 100, 100, ["fresh-turn"]);
        var firstResponse = new TaskCompletionSource<RemoteTranscript>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var liveEvents = new AsyncSseStream();
        var snapshot = Snapshot(chatId, "Refresh state");
        var handler = new ControlledRemoteHandler
        {
            Snapshot = snapshot,
            TranscriptResponder = (request, _) =>
                request.Ordinal == 1
                    ? firstResponse.Task
                    : Task.FromResult(fresh)
        };
        handler.EventResponseFactory = ordinal => ordinal == 1
            ? ControlledRemoteHandler.EventResponse(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(Frame(RemoteProtocol.Events.Snapshot, snapshot)),
                    writable: false))
            : ControlledRemoteHandler.EventResponse(liveEvents);

        await using var shell = CreateShell(handler);
        var observedTitles = new ConcurrentQueue<string>();
        shell.Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MobileChatViewModel.Title))
                observedTitles.Enqueue(shell.Chat.Title);
        };

        try
        {
            await PairAsync(shell);

            var active = await handler.NextTranscriptRequestAsync();
            Assert.Equal(chatId, active.ChatId);
            Assert.Null(active.BeforeMessageIndex);

            Assert.Equal(1, await handler.NextEventRequestAsync());
            Assert.Equal(2, await handler.NextEventRequestAsync());

            Assert.Equal(0, handler.SnapshotRequestCount);

            var burstDrained = WaitForPropertyAsync(
                shell.Chat,
                nameof(MobileChatViewModel.StatusText),
                () => shell.Chat.StatusText == "burst-drained");

            liveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, snapshot));
            for (var revision = 2; revision <= 33; revision++)
            {
                liveEvents.Push(Frame(
                    RemoteProtocol.Events.TranscriptInvalidated,
                    new RemoteTranscriptInvalidated { ChatId = chatId, Revision = revision }));
            }

            // This observable marker is behind every invalidation in the same SSE stream, so reaching
            // it proves the complete burst has entered the refresh state machine before we unblock GET 1.
            liveEvents.Push(Frame(
                RemoteProtocol.Events.ChatStatus,
                new RemoteChatStatus { ChatId = chatId, StatusText = "burst-drained" }));
            await burstDrained;

            Assert.Equal(1, handler.TranscriptRequestCount);
            Assert.Equal(1, handler.ActiveTranscriptRequests);
            Assert.Equal(1, handler.MaxActiveTranscriptRequests);

            var freshApplied = WaitForPropertyAsync(
                shell.Chat,
                nameof(MobileChatViewModel.Title),
                () => shell.Chat.Title == "FRESH");

            firstResponse.TrySetResult(stale);

            var trailing = await handler.NextTranscriptRequestAsync();
            Assert.Equal(2, trailing.Ordinal);
            Assert.Equal(chatId, trailing.ChatId);
            Assert.Null(trailing.BeforeMessageIndex);
            await freshApplied;

            Assert.Equal(2, handler.TranscriptRequestCount);
            Assert.Equal(0, handler.ActiveTranscriptRequests);
            Assert.Equal(1, handler.MaxActiveTranscriptRequests);
            Assert.DoesNotContain("STALE", observedTitles);
            Assert.Equal("fresh-turn", Assert.Single(shell.Chat.Turns).Id);
        }
        finally
        {
            firstResponse.TrySetResult(stale);
            liveEvents.Complete();
        }
    }

    [Fact]
    public async Task NewServerEpoch_AcceptsALowerRevisionWhileLegacyResponsesStayMonotonic()
    {
        var chatId = Guid.NewGuid();
        var handler = new ControlledRemoteHandler();
        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "Epoch");

        shell.Chat.ApplyTranscript(
            Transcript(
                chatId,
                "OLD-EPOCH",
                revision: 500,
                0,
                1,
                1,
                ["old"],
                revisionEpoch: "server-a"));
        shell.Chat.ApplyTranscript(
            Transcript(
                chatId,
                "NEW-EPOCH",
                revision: 1,
                0,
                1,
                1,
                ["new"],
                revisionEpoch: "server-b"));

        Assert.Equal("NEW-EPOCH", shell.Chat.Title);
        Assert.Equal("new", Assert.Single(shell.Chat.Turns).Id);

        // Missing epochs are the legacy protocol shape. They never reset an established sequence.
        shell.Chat.ApplyTranscript(
            Transcript(
                chatId,
                "LEGACY-STALE",
                revision: 0,
                0,
                1,
                1,
                ["legacy-stale"]));

        Assert.Equal("NEW-EPOCH", shell.Chat.Title);
        Assert.Equal("new", Assert.Single(shell.Chat.Turns).Id);
    }

    [Fact]
    public async Task SwitchingChats_CancelsOrIgnoresThePriorWindowAndOnlyAppliesTheNewChat()
    {
        var oldChatId = Guid.NewGuid();
        var newChatId = Guid.NewGuid();
        var staleWindow = Transcript(oldChatId, "STALE-A", revision: 7, 100, 200, 300, ["stale-a"]);
        var newWindow = Transcript(newChatId, "CHAT-B", revision: 1, 0, 20, 20, ["chat-b"]);
        var oldResponse = new TaskCompletionSource<RemoteTranscript>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (request, _) =>
                request.ChatId == oldChatId
                    ? oldResponse.Task
                    : Task.FromResult(newWindow)
        };

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(oldChatId, "Chat A");
        shell.Chat.ApplyTranscript(
            Transcript(oldChatId, "Chat A", revision: 7, 200, 300, 300, ["latest-a"]));
        shell.ChatList.Apply(
        [
            new RemoteChatGroup
            {
                Label = "Today",
                Chats = [new RemoteChat { Id = newChatId, Title = "Chat B" }]
            }
        ]);

        var observedTitles = new ConcurrentQueue<string>();
        shell.Chat.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MobileChatViewModel.Title))
                observedTitles.Enqueue(shell.Chat.Title);
        };

        try
        {
            var priorWindow = shell.LoadEarlierActivityCommand.ExecuteAsync(null);
            var oldRequest = await handler.NextTranscriptRequestAsync();
            Assert.Equal(oldChatId, oldRequest.ChatId);
            Assert.Equal(200, oldRequest.BeforeMessageIndex);
            Assert.Equal(RemoteProtocol.TranscriptWindowRawMessageLimit, oldRequest.MaxMessages);

            var newChatApplied = WaitForPropertyAsync(
                shell.Chat,
                nameof(MobileChatViewModel.Title),
                () => shell.Chat.Title == "CHAT-B");

            var newChat = Assert.Single(Assert.Single(shell.ChatList.Groups).Chats);
            shell.ChatList.OpenChatCommand.Execute(newChat);

            // The fake deliberately ignores the cancelled request token and returns a valid old page.
            // The shell must still reject it by surface generation/chat identity.
            oldResponse.TrySetResult(staleWindow);

            var newRequest = await handler.NextTranscriptRequestAsync();
            Assert.Equal(newChatId, newRequest.ChatId);
            Assert.Null(newRequest.BeforeMessageIndex);
            Assert.Equal(RemoteProtocol.InitialTranscriptWindowRawMessageLimit, newRequest.MaxMessages);

            await newChatApplied;
            await priorWindow;

            Assert.Equal(newChatId, shell.Chat.ChatId);
            Assert.Equal("CHAT-B", shell.Chat.Title);
            Assert.Equal("chat-b", Assert.Single(shell.Chat.Turns).Id);
            Assert.DoesNotContain("STALE-A", observedTitles);
            Assert.Equal(2, handler.TranscriptRequestCount);
            Assert.Equal(1, handler.MaxActiveTranscriptRequests);
        }
        finally
        {
            oldResponse.TrySetResult(staleWindow);
        }
    }

    [Theory]
    [InlineData(404, "Chat not found.")]
    [InlineData(500, "Projection failed.")]
    public async Task TranscriptHttpError_KeepsTheCurrentPageAndDisplaysTheServerError(
        int statusCode,
        string serverError)
    {
        var chatId = Guid.NewGuid();
        var handler = new ControlledRemoteHandler
        {
            TranscriptHttpResponder = (_, _) => Task.FromResult(
                ControlledRemoteHandler.ErrorResponse(
                    (HttpStatusCode)statusCode,
                    serverError))
        };

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "Current page");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "Current page", revision: 4, 200, 300, 300, ["current"]));

        await shell.LoadEarlierActivityCommand.ExecuteAsync(null);

        AssertTurnIds(shell, "current");
        Assert.Equal(200, shell.Chat.WindowStartMessageIndex);
        Assert.Equal(300, shell.Chat.WindowEndMessageIndex);
        Assert.Equal(serverError, shell.Chat.TranscriptErrorText);
        Assert.Equal(serverError, shell.Client.StateMessage);
        Assert.Equal(RemoteLinkState.Error, shell.Client.State);
        Assert.False(shell.Chat.IsLoading);
    }

    [Theory]
    [InlineData(404, "Chat not found.")]
    [InlineData(500, "Projection failed.")]
    public async Task TranscriptHttpError_DoesNotDemoteAHealthyEventStream(
        int statusCode,
        string serverError)
    {
        var handler = new ControlledRemoteHandler
        {
            TranscriptHttpResponder = (_, _) => Task.FromResult(
                ControlledRemoteHandler.ErrorResponse(
                    (HttpStatusCode)statusCode,
                    serverError))
        };
        await using var client = new LumiRemoteClient("device", "Phone", handler);
        client.Configure(BaseUrl, "test-token");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (state, _) =>
        {
            if (state == RemoteLinkState.Connected)
                connected.TrySetResult();
        };

        await client.StartEventStreamAsync();
        Assert.Equal(1, await handler.NextEventRequestAsync());
        handler.LiveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, new RemoteSnapshot()));
        await connected.Task.WaitAsync(TestTimeout);
        var transcript = await client.GetTranscriptAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(transcript);
        Assert.Equal(RemoteLinkState.Connected, client.State);
        Assert.Equal(serverError, client.StateMessage);
    }

    [Fact]
    public async Task TranscriptForTheWrongChat_DoesNotMutateNavigationOrTheCurrentPage()
    {
        var chatId = Guid.NewGuid();
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (_, _) => Task.FromResult(
                Transcript(
                    Guid.NewGuid(),
                    "WRONG CHAT",
                    revision: 10,
                    100,
                    200,
                    300,
                    ["wrong"]))
        };

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "Current page");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "Current page", revision: 9, 200, 300, 300, ["current"]));

        await shell.LoadEarlierActivityCommand.ExecuteAsync(null);

        AssertTurnIds(shell, "current");
        Assert.Equal(200, shell.Chat.WindowStartMessageIndex);
        Assert.Equal(300, shell.Chat.WindowEndMessageIndex);
        Assert.Contains(
            "different chat",
            shell.Chat.TranscriptErrorText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OlderWindow_LiveDeltaAndInvalidationOnlyMarkNewerActivity()
    {
        var chatId = Guid.NewGuid();
        var handler = new ControlledRemoteHandler();
        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "Older activity");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "Older activity", revision: 4, 0, 100, 300, ["older-turn"]));

        var connectedMarkedNewer = WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.HasNewerActivity),
            () => shell.Chat.HasNewerActivity);

        await shell.Client.StartEventStreamAsync();
        Assert.Equal(1, await handler.NextEventRequestAsync());
        handler.LiveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, new RemoteSnapshot()));
        await connectedMarkedNewer;

        // The connection's bootstrap snapshot is also a freshness hint for an old page. Clear it so
        // each later live event is independently observable below.
        shell.Chat.HasNewerActivity = false;
        Assert.Equal(0, handler.TranscriptRequestCount);

        var deltaMarkedNewer = WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.HasNewerActivity),
            () => shell.Chat.HasNewerActivity);
        handler.LiveEvents.Push(Frame(
            RemoteProtocol.Events.StreamDelta,
            new RemoteStreamDelta
            {
                ChatId = chatId,
                ItemId = "older-turn-item",
                Text = "new tail text"
            }));
        await deltaMarkedNewer;

        AssertOlderWindowWasNotMovedOrMutated(shell);
        Assert.Equal(0, handler.TranscriptRequestCount);

        shell.Chat.HasNewerActivity = false;
        var invalidationMarkedNewer = WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.HasNewerActivity),
            () => shell.Chat.HasNewerActivity);
        handler.LiveEvents.Push(Frame(
            RemoteProtocol.Events.TranscriptInvalidated,
            new RemoteTranscriptInvalidated { ChatId = chatId, Revision = 5 }));
        await invalidationMarkedNewer;

        AssertOlderWindowWasNotMovedOrMutated(shell);
        Assert.Equal(0, handler.TranscriptRequestCount);
    }

    [Fact]
    public async Task InvalidationBurst_DoesNotSupersedeBlockedEarlierNavigation()
    {
        var chatId = Guid.NewGuid();
        var earlier = Transcript(
            chatId,
            "History",
            revision: 12,
            100,
            200,
            300,
            ["earlier"]);
        var blockedEarlier = new TaskCompletionSource<RemoteTranscript>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (_, _) => blockedEarlier.Task
        };

        await using var shell = CreateConfiguredShell(handler);
        var connected = WaitForPropertyAsync(
            shell,
            nameof(MobileShellViewModel.IsConnected),
            () => shell.IsConnected);
        await shell.Client.StartEventStreamAsync();
        Assert.Equal(1, await handler.NextEventRequestAsync());
        handler.LiveEvents.Push(Frame(RemoteProtocol.Events.Snapshot, new RemoteSnapshot()));
        await connected;

        // A marker read from the event stream can only arrive after the Connected callback has
        // finished issuing its empty-chat resync work.
        var streamReady = WaitForPropertyAsync(
            shell.Chat,
            nameof(MobileChatViewModel.StatusText),
            () => shell.Chat.StatusText == "stream-ready");
        handler.LiveEvents.Push(Frame(
            RemoteProtocol.Events.ChatStatus,
            new RemoteChatStatus { StatusText = "stream-ready" }));
        await streamReady;

        try
        {
            shell.Chat.Reset(chatId, "History");
            shell.Chat.ApplyTranscript(
                Transcript(chatId, "History", revision: 11, 200, 300, 300, ["latest"]));

            var navigation = shell.LoadEarlierActivityCommand.ExecuteAsync(null);
            var request = await handler.NextTranscriptRequestAsync();
            Assert.Equal(200, request.BeforeMessageIndex);

            var burstDrained = WaitForPropertyAsync(
                shell.Chat,
                nameof(MobileChatViewModel.StatusText),
                () => shell.Chat.StatusText == "navigation-burst-drained");
            for (var revision = 12; revision < 40; revision++)
            {
                handler.LiveEvents.Push(Frame(
                    RemoteProtocol.Events.TranscriptInvalidated,
                    new RemoteTranscriptInvalidated
                    {
                        ChatId = chatId,
                        RevisionEpoch = "server-a",
                        Revision = revision
                    }));
            }

            handler.LiveEvents.Push(Frame(
                RemoteProtocol.Events.ChatStatus,
                new RemoteChatStatus
                {
                    ChatId = chatId,
                    StatusText = "navigation-burst-drained"
                }));
            await burstDrained;

            Assert.Equal(1, handler.TranscriptRequestCount);
            Assert.True(shell.Chat.HasNewerActivity);

            blockedEarlier.TrySetResult(earlier);
            await navigation;

            AssertTurnIds(shell, "earlier");
            Assert.False(shell.Chat.IsLatestWindow);
            Assert.True(shell.Chat.HasNewerActivity);
            Assert.Equal(1, handler.TranscriptRequestCount);
            Assert.Equal(new int?[] { 200 }, handler.RequestedBeforeMessageIndices.ToArray());
        }
        finally
        {
            blockedEarlier.TrySetResult(earlier);
        }
    }

    [Fact]
    public async Task DeferredUpdate_AfterLatestNavigationApplies_QueuesOneTrailingLatestGet()
    {
        var chatId = Guid.NewGuid();
        var navigationResponse = new TaskCompletionSource<RemoteTranscript>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var trailingResponse = new TaskCompletionSource<RemoteTranscript>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (request, _) => request.Ordinal switch
            {
                1 => navigationResponse.Task,
                2 => trailingResponse.Task,
                _ => throw new InvalidOperationException($"Unexpected request {request.Ordinal}.")
            }
        };

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "History");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "History", revision: 10, 100, 200, 300, ["older"]));

        var navigation = shell.ReturnToLatestCommand.ExecuteAsync(null);
        var latestRequest = await handler.NextTranscriptRequestAsync();
        Assert.Null(latestRequest.BeforeMessageIndex);

        var deferred = shell.RefreshTranscriptAsync();
        Assert.True(shell.Chat.HasNewerActivity);
        Assert.Equal(1, handler.TranscriptRequestCount);

        navigationResponse.TrySetResult(
            Transcript(
                chatId,
                "History",
                revision: 11,
                200,
                300,
                300,
                ["captured-before-update"]));

        var trailingRequest = await handler.NextTranscriptRequestAsync();
        Assert.Null(trailingRequest.BeforeMessageIndex);
        AssertTurnIds(shell, "captured-before-update");
        Assert.True(shell.Chat.IsLatestWindow);
        Assert.True(shell.Chat.HasNewerActivity);
        Assert.Equal(2, handler.TranscriptRequestCount);

        trailingResponse.TrySetResult(
            Transcript(
                chatId,
                "History",
                revision: 12,
                201,
                301,
                301,
                ["fresh-latest"]));
        await Task.WhenAll(navigation, deferred);

        AssertTurnIds(shell, "fresh-latest");
        Assert.True(shell.Chat.IsLatestWindow);
        Assert.False(shell.Chat.HasNewerActivity);
        Assert.Equal(new int?[] { null, null }, handler.RequestedBeforeMessageIndices.ToArray());
    }

    [Fact]
    public async Task EarlierNewerAndLatestNavigation_UsesExclusiveCursorsAndReplacesTurns()
    {
        var chatId = Guid.NewGuid();
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (request, _) => Task.FromResult(request.BeforeMessageIndex switch
            {
                100 => Transcript(chatId, "History", revision: 9, 0, 100, 300, ["oldest"]),
                200 => Transcript(chatId, "History", revision: 9, 100, 200, 300, ["middle-1", "middle-2"]),
                null => Transcript(chatId, "History", revision: 9, 200, 300, 300, ["latest"]),
                _ => throw new InvalidOperationException(
                    $"Unexpected beforeMessageIndex={request.BeforeMessageIndex}.")
            })
        };

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "History");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "History", revision: 9, 200, 300, 300, ["latest"]));

        await ExecuteAndAssertRequestAsync(
            shell.LoadEarlierActivityCommand.ExecuteAsync(null),
            handler,
            expectedBefore: 200);
        AssertTurnIds(shell, "middle-1", "middle-2");

        await ExecuteAndAssertRequestAsync(
            shell.LoadEarlierActivityCommand.ExecuteAsync(null),
            handler,
            expectedBefore: 100);
        AssertTurnIds(shell, "oldest");

        await ExecuteAndAssertRequestAsync(
            shell.LoadNewerActivityCommand.ExecuteAsync(null),
            handler,
            expectedBefore: 200);
        AssertTurnIds(shell, "middle-1", "middle-2");

        await ExecuteAndAssertRequestAsync(
            shell.ReturnToLatestCommand.ExecuteAsync(null),
            handler,
            expectedBefore: null);
        AssertTurnIds(shell, "latest");

        Assert.Equal(
            new int?[] { 200, 100, 200, null },
            handler.RequestedBeforeMessageIndices.ToArray());
        Assert.True(shell.Chat.IsLatestWindow);
        Assert.False(shell.Chat.HasLaterMessages);
        Assert.False(shell.Chat.HasNewerActivity);
    }

    [Fact]
    public async Task NewerNavigation_UsesThePreviousWindowEndAfterMessagesAppend()
    {
        var chatId = Guid.NewGuid();
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (request, _) => Task.FromResult(request.BeforeMessageIndex switch
            {
                200 => Transcript(
                    chatId,
                    "History",
                    revision: 10,
                    100,
                    200,
                    300,
                    ["earlier"]),
                300 => Transcript(
                    chatId,
                    "History",
                    revision: 11,
                    200,
                    300,
                    350,
                    ["stable-newer"]),
                null => Transcript(
                    chatId,
                    "History",
                    revision: 11,
                    250,
                    350,
                    350,
                    ["live-latest"]),
                _ => throw new InvalidOperationException(
                    $"Unexpected beforeMessageIndex={request.BeforeMessageIndex}.")
            })
        };

        await using var shell = CreateConfiguredShell(handler);
        shell.Chat.Reset(chatId, "History");
        shell.Chat.ApplyTranscript(
            Transcript(chatId, "History", revision: 10, 200, 300, 300, ["old-latest"]));

        await ExecuteAndAssertRequestAsync(
            shell.LoadEarlierActivityCommand.ExecuteAsync(null),
            handler,
            expectedBefore: 200);
        AssertTurnIds(shell, "earlier");

        // Fifty messages arrive while the user is reading [100,200]. Newer must return to the exact
        // old tail [200,300], not jump to the moving latest [250,350].
        await ExecuteAndAssertRequestAsync(
            shell.LoadNewerActivityCommand.ExecuteAsync(null),
            handler,
            expectedBefore: 300);
        AssertTurnIds(shell, "stable-newer");
        Assert.False(shell.Chat.IsLatestWindow);

        await ExecuteAndAssertRequestAsync(
            shell.ReturnToLatestCommand.ExecuteAsync(null),
            handler,
            expectedBefore: null);
        AssertTurnIds(shell, "live-latest");

        Assert.Equal(
            new int?[] { 200, 300, null },
            handler.RequestedBeforeMessageIndices.ToArray());
    }

    [Fact]
    public async Task CompletingARefreshWhileResettingTheChat_DoesNotRaceCtsDisposal()
    {
        const int iterations = 250;
        var pendingResponses =
            Channel.CreateUnbounded<(TranscriptRequest Request, TaskCompletionSource<RemoteTranscript> Response)>();
        var handler = new ControlledRemoteHandler
        {
            TranscriptResponder = (request, _) =>
            {
                var response = new TaskCompletionSource<RemoteTranscript>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pendingResponses.Writer.TryWrite((request, response));
                return response.Task;
            }
        };

        await using var shell = CreateConfiguredShell(handler);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var chatId = Guid.NewGuid();
            shell.Chat.Reset(chatId, $"Chat {iteration}");
            shell.Chat.ApplyTranscript(
                Transcript(
                    chatId,
                    $"Chat {iteration}",
                    revision: iteration + 1,
                    100,
                    200,
                    200,
                    [$"latest-{iteration}"]));

            var navigation = shell.LoadEarlierActivityCommand.ExecuteAsync(null);
            var request = await handler.NextTranscriptRequestAsync();
            var pending = await pendingResponses.Reader
                .ReadAsync()
                .AsTask()
                .WaitAsync(TestTimeout);
            Assert.Equal(request, pending.Request);

            using var start = new ManualResetEventSlim();
            var complete = Task.Run(() =>
            {
                start.Wait();
                pending.Response.TrySetResult(
                    Transcript(
                        chatId,
                        $"Chat {iteration}",
                        revision: iteration + 1,
                        0,
                        100,
                        200,
                        [$"earlier-{iteration}"]));
            });
            var reset = Task.Run(() =>
            {
                start.Wait();
                shell.ChatList.NewChatCommand.Execute(null);
            });

            start.Set();
            await Task.WhenAll(complete, reset);
            await navigation;
            Assert.Equal(Guid.Empty, shell.Chat.ChatId);
        }

        Assert.Equal(iterations, handler.TranscriptRequestCount);
        Assert.Equal(1, handler.MaxActiveTranscriptRequests);
    }

    [Fact]
    public async Task StartingTheEventStreamWhileDisposing_DoesNotRestartDisposedClient()
    {
        const int racers = 32;
        var firstStream = new AsyncSseStream();
        var handler = new ControlledRemoteHandler
        {
            EventResponseFactory = _ =>
                ControlledRemoteHandler.EventResponse(firstStream)
        };
        await using var client = new LumiRemoteClient("device", "Phone", handler);
        client.Configure(BaseUrl, "test-token");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (state, _) =>
        {
            if (state == RemoteLinkState.Connected)
                connected.TrySetResult();
        };
        await client.StartEventStreamAsync();
        Assert.Equal(1, await handler.NextEventRequestAsync());
        firstStream.Push(Frame(RemoteProtocol.Events.Snapshot, new RemoteSnapshot()));
        await connected.Task.WaitAsync(TestTimeout);

        using var start = new ManualResetEventSlim();
        var starts = Enumerable.Range(0, racers)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                await client.StartEventStreamAsync();
            }))
            .ToArray();
        var dispose = Task.Run(async () =>
        {
            start.Wait();
            await client.DisposeAsync();
        });

        start.Set();
        await Task.WhenAll(starts.Append(dispose));
        var requestsAfterDispose = handler.EventRequestCount;

        await client.StartEventStreamAsync();

        Assert.Equal(requestsAfterDispose, handler.EventRequestCount);
        Assert.Equal(RemoteLinkState.Disconnected, client.State);
    }

    private static async Task ExecuteAndAssertRequestAsync(
        Task command,
        ControlledRemoteHandler handler,
        int? expectedBefore)
    {
        var request = await handler.NextTranscriptRequestAsync();
        Assert.Equal(expectedBefore, request.BeforeMessageIndex);
        await command;
    }

    private static void AssertOlderWindowWasNotMovedOrMutated(MobileShellViewModel shell)
    {
        Assert.True(shell.Chat.HasNewerActivity);
        Assert.False(shell.Chat.IsLatestWindow);
        Assert.Equal(0, shell.Chat.WindowStartMessageIndex);
        Assert.Equal(100, shell.Chat.WindowEndMessageIndex);
        Assert.Equal(300, shell.Chat.TotalRawMessageCount);

        var item = Assert.IsType<AssistantItemViewModel>(
            Assert.Single(Assert.Single(shell.Chat.Turns).Items));
        Assert.Equal("older-turn", item.Text);
    }

    private static void AssertTurnIds(MobileShellViewModel shell, params string[] expected) =>
        Assert.Equal(expected, shell.Chat.Turns.Select(turn => turn.Id));

    private static MobileShellViewModel CreateShell(ControlledRemoteHandler handler)
    {
        var storePath = Path.Combine(
            Path.GetTempPath(),
            "lumi-mobile-refresh-tests",
            Guid.NewGuid().ToString("n"));
        var client = new LumiRemoteClient(
            "refresh-state-device",
            "Refresh State Phone",
            handler,
            requestDeadline: TestTimeout,
            uploadDeadline: TestTimeout);

        return new MobileShellViewModel(
            client,
            new LumiDiscoveryClient(),
            new MobileSettingsStore(storePath),
            action => action());
    }

    private static MobileShellViewModel CreateConfiguredShell(ControlledRemoteHandler handler)
    {
        var shell = CreateShell(handler);
        shell.Client.Configure(BaseUrl, "test-token");
        return shell;
    }

    private static async Task PairAsync(MobileShellViewModel shell)
    {
        shell.Connect.ManualAddress = BaseUrl;
        shell.Connect.AllowInsecureLanDiscovery = true;
        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);
        Assert.Equal(ConnectStep.EnterCode, shell.Connect.Step);

        shell.Connect.PairingCode = "123456";
        await shell.Connect.SubmitCodeCommand.ExecuteAsync(null);
        Assert.True(shell.IsPaired);
    }

    private static RemoteSnapshot Snapshot(Guid chatId, string title) =>
        new()
        {
            HostName = "TEST-PC",
            IsConnected = true,
            ActiveChatId = chatId,
            ActiveChat = new RemoteChat { Id = chatId, Title = title },
            Chats = new RemoteChatPage
            {
                TotalCount = 1,
                Groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "Today",
                        Chats = [new RemoteChat { Id = chatId, Title = title }]
                    }
                ]
            }
        };

    private static RemoteTranscript Transcript(
        Guid chatId,
        string title,
        long revision,
        int windowStart,
        int windowEnd,
        int total,
        IReadOnlyList<string> turnIds,
        string? revisionEpoch = null) =>
        new()
        {
            ChatId = chatId,
            Title = title,
            RevisionEpoch = revisionEpoch,
            Revision = revision,
            WindowStartMessageIndex = windowStart,
            WindowEndMessageIndex = windowEnd,
            TotalRawMessageCount = total,
            HasEarlierMessages = windowStart > 0,
            HasLaterMessages = windowEnd < total,
            IsLatestWindow = windowEnd == total,
            Turns =
            [
                .. turnIds.Select(turnId => new RemoteTranscriptTurn
                {
                    Id = turnId,
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = $"{turnId}-item",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            Text = turnId
                        }
                    ]
                })
            ],
            Status = new RemoteChatStatus { ChatId = chatId }
        };

    private static string Frame<T>(
        string eventName,
        T value)
    {
        var json = value switch
        {
            RemoteSnapshot snapshot =>
                JsonSerializer.Serialize(snapshot, RemoteJsonContext.Default.RemoteSnapshot),
            RemoteChatStatus status =>
                JsonSerializer.Serialize(status, RemoteJsonContext.Default.RemoteChatStatus),
            RemoteStreamDelta delta =>
                JsonSerializer.Serialize(delta, RemoteJsonContext.Default.RemoteStreamDelta),
            RemoteTranscriptInvalidated invalidated =>
                JsonSerializer.Serialize(
                    invalidated,
                    RemoteJsonContext.Default.RemoteTranscriptInvalidated),
            _ => throw new InvalidOperationException($"Unsupported event payload {typeof(T).Name}.")
        };

        return new RemoteEventFrame(eventName, json).ToWire();
    }

    private static async Task WaitForPropertyAsync(
        INotifyPropertyChanged source,
        string propertyName,
        Func<bool> condition)
    {
        if (condition())
            return;

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if ((args.PropertyName is null || args.PropertyName == propertyName) && condition())
                reached.TrySetResult();
        };

        source.PropertyChanged += handler;
        try
        {
            if (condition())
                reached.TrySetResult();

            await reached.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            source.PropertyChanged -= handler;
        }
    }

    private sealed record TranscriptRequest(
        int Ordinal,
        Guid ChatId,
        int? BeforeMessageIndex,
        int MaxMessages);

    private sealed class ControlledRemoteHandler : HttpMessageHandler
    {
        private readonly Channel<TranscriptRequest> _transcriptRequests =
            Channel.CreateUnbounded<TranscriptRequest>();
        private readonly Channel<int> _eventRequests = Channel.CreateUnbounded<int>();
        private readonly Channel<RemoteCommand> _commands = Channel.CreateUnbounded<RemoteCommand>();
        private int _transcriptRequestCount;
        private int _snapshotRequestCount;
        private int _eventRequestCount;
        private int _activeTranscriptRequests;
        private int _maxActiveTranscriptRequests;

        public RemoteSnapshot Snapshot { get; set; } = new();

        public AsyncSseStream LiveEvents { get; } = new();

        public Func<TranscriptRequest, CancellationToken, Task<RemoteTranscript>> TranscriptResponder
        {
            get;
            set;
        } = (_, _) => Task.FromResult(new RemoteTranscript());

        public Func<TranscriptRequest, CancellationToken, Task<HttpResponseMessage>>?
            TranscriptHttpResponder { get; set; }

        public Func<int, HttpResponseMessage>? EventResponseFactory { get; set; }

        public ConcurrentQueue<int?> RequestedBeforeMessageIndices { get; } = new();

        public int TranscriptRequestCount => Volatile.Read(ref _transcriptRequestCount);

        public int ActiveTranscriptRequests => Volatile.Read(ref _activeTranscriptRequests);

        public int MaxActiveTranscriptRequests => Volatile.Read(ref _maxActiveTranscriptRequests);

        public int EventRequestCount => Volatile.Read(ref _eventRequestCount);

        public int SnapshotRequestCount => Volatile.Read(ref _snapshotRequestCount);

        public Task<TranscriptRequest> NextTranscriptRequestAsync() =>
            _transcriptRequests.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public Task<int> NextEventRequestAsync() =>
            _eventRequests.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public Task<RemoteCommand> NextCommandAsync() =>
            _commands.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            switch (path)
            {
                case RemoteProtocol.Routes.Hello:
                    return JsonResponse(
                        JsonSerializer.Serialize(
                            new RemoteHello
                            {
                                HostName = "TEST-PC",
                                UserName = "Tester",
                                IsPaired = false
                            },
                            RemoteJsonContext.Default.RemoteHello));

                case RemoteProtocol.Routes.Pair:
                    return JsonResponse(
                        JsonSerializer.Serialize(
                            new RemotePairResponse
                            {
                                Ok = true,
                                Token = "test-token",
                                HostName = "TEST-PC",
                                UserName = "Tester"
                            },
                            RemoteJsonContext.Default.RemotePairResponse));

                case RemoteProtocol.Routes.Snapshot:
                {
                    Interlocked.Increment(ref _snapshotRequestCount);
                    return JsonResponse(
                        JsonSerializer.Serialize(Snapshot, RemoteJsonContext.Default.RemoteSnapshot));
                }

                case RemoteProtocol.Routes.Transcript:
                    return await HandleTranscriptAsync(request, cancellationToken).ConfigureAwait(false);

                case RemoteProtocol.Routes.Command:
                {
                    var body = await request.Content!
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var command = JsonSerializer.Deserialize(
                        body,
                        RemoteJsonContext.Default.RemoteCommand);
                    if (command is not null)
                        _commands.Writer.TryWrite(command);

                    return JsonResponse(
                        JsonSerializer.Serialize(
                            new RemoteCommandResult { Ok = true },
                            RemoteJsonContext.Default.RemoteCommandResult));
                }

                case RemoteProtocol.Routes.Events:
                {
                    var ordinal = Interlocked.Increment(ref _eventRequestCount);
                    _eventRequests.Writer.TryWrite(ordinal);
                    return EventResponseFactory?.Invoke(ordinal) ?? EventResponse(LiveEvents);
                }

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private async Task<HttpResponseMessage> HandleTranscriptAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var ordinal = Interlocked.Increment(ref _transcriptRequestCount);
            var chatId = Guid.Parse(QueryValue(request.RequestUri!, "chatId")!);
            var beforeValue = QueryValue(request.RequestUri!, "beforeMessageIndex");
            int? beforeMessageIndex = beforeValue is null ? null : int.Parse(beforeValue);
            var limitValue = QueryValue(request.RequestUri!, "limit");
            var maxMessages = limitValue is null
                ? RemoteProtocol.TranscriptWindowRawMessageLimit
                : int.Parse(limitValue);
            var transcriptRequest = new TranscriptRequest(
                ordinal,
                chatId,
                beforeMessageIndex,
                maxMessages);

            RequestedBeforeMessageIndices.Enqueue(beforeMessageIndex);
            var active = Interlocked.Increment(ref _activeTranscriptRequests);
            UpdateMaximum(ref _maxActiveTranscriptRequests, active);
            _transcriptRequests.Writer.TryWrite(transcriptRequest);

            try
            {
                if (TranscriptHttpResponder is not null)
                {
                    return await TranscriptHttpResponder(transcriptRequest, cancellationToken)
                        .ConfigureAwait(false);
                }

                var transcript = await TranscriptResponder(transcriptRequest, cancellationToken)
                    .ConfigureAwait(false);
                return JsonResponse(
                    JsonSerializer.Serialize(
                        transcript,
                        RemoteJsonContext.Default.RemoteTranscript));
            }
            finally
            {
                Interlocked.Decrement(ref _activeTranscriptRequests);
            }
        }

        public static HttpResponseMessage EventResponse(Stream stream)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        public static HttpResponseMessage ErrorResponse(
            HttpStatusCode statusCode,
            string message) =>
            new(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new RemoteCommandResult { Ok = false, Error = message },
                        RemoteJsonContext.Default.RemoteCommandResult),
                    Encoding.UTF8,
                    "application/json")
            };

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var segment in uri.Query.TrimStart('?')
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = segment.IndexOf('=');
                var key = separator < 0 ? segment : segment[..separator];
                if (!string.Equals(
                        Uri.UnescapeDataString(key),
                        name,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                return separator < 0
                    ? ""
                    : Uri.UnescapeDataString(segment[(separator + 1)..]);
            }

            return null;
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximum);
                if (candidate <= current)
                    return;

                if (Interlocked.CompareExchange(ref maximum, candidate, current) == current)
                    return;
            }
        }
    }

    private sealed class AsyncSseStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });
        private byte[]? _current;
        private int _currentOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Push(string wire)
        {
            if (!_chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(wire)))
                throw new InvalidOperationException("The SSE stream is already closed.");
        }

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _currentOffset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;

                if (!_chunks.Reader.TryRead(out _current))
                    continue;

                _currentOffset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
            _current.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Complete();

            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

[Collection("Headless mobile UI")]
public sealed class BoundedTranscriptRenderRegressionTests
{
    [Fact]
    public async Task PathologicalMaximumWindow_KeepsViewModelsAndRenderedToolControlsBounded()
    {
        Assert.Equal(100, RemoteProtocol.TranscriptWindowRawMessageLimit);
        Assert.Equal(128 * 1024, RemoteProtocol.TranscriptWindowTextBudgetCharacters);

        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(
                    store: session.NewStore(),
                    post: action => action());
                shell.IsPaired = true;
                var chatId = Guid.NewGuid();
                shell.Chat.Reset(chatId, "Maximum bounded window");
                shell.Chat.ApplyTranscript(PathologicalTranscript(chatId, revision: 1));

                window = new Window
                {
                    Width = 412,
                    Height = 892,
                    Content = new ChatDetailView { DataContext = shell }
                };
                window.Show();
                Pump(window);

                // Replacing the same bounded response must update the existing graph, not append a
                // second set of rows or controls.
                shell.Chat.ApplyTranscript(PathologicalTranscript(chatId, revision: 2));
                Pump(window);

                var turn = Assert.Single(shell.Chat.Turns);
                var group = Assert.IsType<ToolGroupItemViewModel>(Assert.Single(turn.Items));
                Assert.Equal(RemoteProtocol.TranscriptWindowRawMessageLimit, group.Tools.Count);

                var toolControls = window.GetVisualDescendants()
                    .OfType<StrataAiToolCall>()
                    .ToList();
                Assert.Equal(RemoteProtocol.TranscriptWindowRawMessageLimit, toolControls.Count);

                var totalControls = window.GetVisualDescendants().OfType<Control>().Count() + 1;
                var maximumExpectedControls =
                    RemoteProtocol.TranscriptWindowRawMessageLimit * 20 + 500;
                Assert.True(
                    totalControls <= maximumExpectedControls,
                    $"A 100-message bounded window materialized {totalControls:N0} controls.");
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    private static RemoteTranscript PathologicalTranscript(Guid chatId, long revision)
    {
        var toolCount = RemoteProtocol.TranscriptWindowRawMessageLimit;
        var sourceTextPerTool = RemoteProtocol.TranscriptWindowTextBudgetCharacters / toolCount;
        var sourceText = new string('x', sourceTextPerTool);
        Assert.True(sourceText.Length * toolCount <= RemoteProtocol.TranscriptWindowTextBudgetCharacters);

        return new RemoteTranscript
        {
            ChatId = chatId,
            Title = "Maximum bounded window",
            Revision = revision,
            WindowStartMessageIndex = 0,
            WindowEndMessageIndex = toolCount,
            TotalRawMessageCount = toolCount,
            IsLatestWindow = true,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "tool-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "tool-group",
                            Kind = RemoteProtocol.ItemKinds.ToolGroup,
                            Label = $"{toolCount} steps",
                            Tools =
                            [
                                .. Enumerable.Range(0, toolCount).Select(index => new RemoteToolCall
                                {
                                    Id = $"tool-{index}",
                                    Name = "test-tool",
                                    DisplayName = $"Tool {index}",
                                    Input = sourceText,
                                    Status = "Completed"
                                })
                            ]
                        }
                    ]
                }
            ],
            Status = new RemoteChatStatus { ChatId = chatId }
        };
    }

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }
}
