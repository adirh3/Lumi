using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The whole phone feature end to end: the real desktop server, over a real socket, driven by the
/// real mobile client and the real mobile view models. Nothing is substituted — if pairing,
/// authorization, projection, live push or a remote command breaks, one of these fails.
///
/// <para>Everything runs on a pumped Avalonia UI thread because the command router marshals to
/// <c>Dispatcher.UIThread</c>, exactly as it does in the shipping app.</para>
/// </summary>
[Collection("Headless UI")]
public sealed class RemoteEndToEndTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required DataStore DataStore { get; init; }
        public required MainViewModel Main { get; init; }
        public required LumiRemoteServer Server { get; init; }
        public required LumiRemoteClient Client { get; init; }
        public required MobileShellViewModel Shell { get; init; }

        public string BaseUrl => $"http://127.0.0.1:{Server.Port}";

        public async ValueTask DisposeAsync()
        {
            await Shell.DisposeAsync();
            await Server.DisposeAsync();
            Main.Dispose();
        }
    }

    /// <summary>
    /// Builds a desktop + phone pair and runs <paramref name="body"/> to completion on the UI thread.
    ///
    /// <para><c>HeadlessUnitTestSession.Dispatch(Func&lt;Task&gt;)</c> cannot be used for this. It
    /// abandons an async body at its first yielding await and returns, so the test finished green in
    /// ~400 ms while every assertion past the first <c>await</c> — which here is all of them, since
    /// each test starts by pairing over a real socket — never ran at all. The body is started
    /// explicitly and the dispatcher is pumped until it genuinely finishes; pumping is required
    /// because the server marshals commands through <c>Dispatcher.UIThread.InvokeAsync</c>, so the
    /// UI thread must keep servicing work while the body waits on it.</para>
    /// </summary>
    private static async Task RunAsync(
        Func<Rig, Task> body,
        Action<AppData>? seed = null,
        Func<IReadOnlySet<IPAddress>>? tailscaleAddressProvider = null)
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                var work = RunBodyAsync();
                PumpUntilComplete(work, TimeSpan.FromMinutes(2));
                work.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);

        failure?.Throw();
        return;

        async Task RunBodyAsync()
        {
            Rig? rig = null;
            try
            {
                rig = CreateRig(seed, tailscaleAddressProvider);
                await body(rig);
            }
            finally
            {
                if (rig is not null)
                {
                    // MainViewModel initializes the shared test Copilot service in the background.
                    // Give that startup a chance to finish before tearing down this rig; otherwise
                    // the next real send can coalesce onto work abandoned by the previous test.
                    var settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                    while (rig.Main.IsConnecting && DateTime.UtcNow < settleDeadline)
                        await Task.Delay(20);

                    await rig.DisposeAsync();
                }
            }
        }
    }

    /// <summary>Runs queued UI-thread work until <paramref name="task"/> completes.</summary>
    private static void PumpUntilComplete(Task task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The end-to-end body did not finish within the timeout.");

            Thread.Sleep(1);
        }
    }

    private static Rig CreateRig(
        Action<AppData>? seed,
        Func<IReadOnlySet<IPAddress>>? tailscaleAddressProvider)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false,
                UserName = "Adir",
                RemoteAccessEnabled = true,
                // MUST be an explicit free port, never 0. LumiRemoteServer.Start treats a
                // non-positive port as "use the production default" (47653) and only falls back to
                // an ephemeral one if that bind fails — so 0 here made every test deterministically
                // seize the real companion port. A phone paired to the developer's own Lumi then
                // reconnected into the test server, which knows no devices, got a 401 and silently
                // dropped its pairing token.
                RemoteAccessPort = FreePort()
            }
        };

        seed?.Invoke(data);

        var dataStore = new DataStore(data);
        var main = new MainViewModel(
            dataStore,
            TestCopilot.Shared,
            new UpdateService(),
            initializeCopilotOnStartup: false);
        var server = tailscaleAddressProvider is null
            ? new LumiRemoteServer(dataStore, main)
            : new LumiRemoteServer(dataStore, main, tailscaleAddressProvider);
        main.SettingsVM.AttachRemoteServer(server);
        Assert.False(server.IsRunning);
        server.Start();

        var client = new LumiRemoteClient("test-device", "Test Phone");
        var shell = new MobileShellViewModel(
            client,
            new LumiDiscoveryClient(),
            new MobileSettingsStore(NewTempDir()));

        return new Rig
        {
            DataStore = dataStore,
            Main = main,
            Server = server,
            Client = client,
            Shell = shell
        };
    }

    private static string NewTempDir() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumi-remote-e2e", Guid.NewGuid().ToString("n"));

    /// <summary>Reserves a port from the OS ephemeral range so parallel runs never collide.</summary>
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static async Task PairAsync(Rig rig)
    {
        var code = rig.Server.BeginPairing();
        var bootstrapSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(RemoteEventFrame frame)
        {
            if (frame.Event == RemoteProtocol.Events.Snapshot)
                bootstrapSnapshot.TrySetResult();
        }

        rig.Shell.Connect.ManualAddress = rig.BaseUrl;
        await rig.Shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);

        rig.Client.FrameReceived += OnFrame;
        try
        {
            rig.Shell.Connect.PairingCode = code;
            await rig.Shell.Connect.SubmitCodeCommand.ExecuteAsync(null);

            Assert.True(rig.Shell.IsPaired, rig.Shell.Connect.ErrorText ?? "pairing failed");
            await bootstrapSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            rig.Client.FrameReceived -= OnFrame;
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting for: {because}");
    }

    /// <summary>
    /// Waits until <paramref name="counter"/> stops moving, then returns its settled value. Lets a
    /// test measure only the events its own action caused, instead of ones still in flight.
    /// </summary>
    private static async Task<int> SettleAsync(Func<int> counter)
    {
        var last = counter();
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow - stableSince < TimeSpan.FromMilliseconds(400))
        {
            await Task.Delay(20);
            var current = counter();
            if (current == last)
                continue;

            last = current;
            stableSince = DateTime.UtcNow;
        }

        return last;
    }

    /// <summary>
    /// The rig must never bind <see cref="RemoteProtocol.DefaultPort"/>. It used to: the seed set
    /// <c>RemoteAccessPort = FreePort()</c> believing that meant "ask the OS for a free port", but
    /// <c>LumiRemoteServer.Start</c> reads a non-positive value as "use the production default".
    /// Whenever that port happened to be free, the test server claimed the real companion port, and
    /// a phone paired to the developer's own Lumi reconnected into a server with no known devices,
    /// took a 401 and threw its pairing token away. Running the suite silently unpaired a real phone.
    /// </summary>
    [Fact]
    public Task TheTestServerNeverBindsTheRealCompanionPort() => RunAsync(rig =>
    {
        Assert.NotEqual(RemoteProtocol.DefaultPort, rig.Server.Port);
        Assert.True(rig.Server.Port > 0);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TailscaleAddressesCanRecoverAfterStartupWithoutRestartingLumi()
    {
        var address = IPAddress.Parse("100.85.249.111");
        var available = 0;
        var probes = 0;
        IReadOnlySet<IPAddress> Provider()
        {
            Interlocked.Increment(ref probes);
            return Volatile.Read(ref available) == 1
                ? new HashSet<IPAddress> { address }
                : new HashSet<IPAddress>();
        }

        return RunAsync(
            async rig =>
            {
                await WaitAsync(() => Volatile.Read(ref probes) > 0, "the initial Tailscale probe");
                Assert.Empty(rig.Server.VerifiedTailscaleAddresses);

                Volatile.Write(ref available, 1);
                await rig.Server.RefreshTailscaleAddressesNowAsync();

                Assert.Contains(address, rig.Server.VerifiedTailscaleAddresses);
            },
            tailscaleAddressProvider: Provider);
    }

    [Fact]
    public Task RestartingTheServerStartsANewTranscriptRevisionEpoch()
    {
        var chatId = Guid.NewGuid();
        return RunAsync(
            async rig =>
            {
                var firstHello = Assert.IsType<RemoteHello>(
                    await rig.Client.HelloAsync(rig.BaseUrl, CancellationToken.None));
                var pair = await rig.Client.PairAsync(
                    rig.BaseUrl,
                    rig.Server.BeginPairing(),
                    CancellationToken.None);
                Assert.True(pair.Ok, pair.Error);

                var firstTranscript = Assert.IsType<RemoteTranscript>(
                    await rig.Client.GetTranscriptAsync(chatId, CancellationToken.None));
                Assert.Equal(firstHello.InstanceId, firstTranscript.RevisionEpoch);

                rig.Server.Stop();
                rig.Server.Start();

                var secondHello = Assert.IsType<RemoteHello>(
                    await rig.Client.HelloAsync(rig.BaseUrl, CancellationToken.None));
                var secondTranscript = Assert.IsType<RemoteTranscript>(
                    await rig.Client.GetTranscriptAsync(chatId, CancellationToken.None));

                Assert.NotEqual(firstHello.InstanceId, secondHello.InstanceId);
                Assert.Equal(secondHello.InstanceId, secondTranscript.RevisionEpoch);
                Assert.NotEqual(firstTranscript.RevisionEpoch, secondTranscript.RevisionEpoch);
            },
            data =>
            {
                data.Chats.Add(new Chat
                {
                    Id = chatId,
                    Title = "Restart epoch",
                    Messages =
                    [
                        new ChatMessage
                        {
                            Id = Guid.NewGuid(),
                            Role = "assistant",
                            Content = "survives restart",
                            Timestamp = DateTime.UtcNow
                        }
                    ]
                });
            });
    }

    /// <summary>
    /// The whole composer-configuration surface, end to end. Before this existed the phone could
    /// only send text: it inherited whatever model, agent, project and skills the PC happened to be
    /// set to, with no way to see or change any of them. Each picker is exercised in one test
    /// because they share a single <c>configure_chat</c> round trip — if the routing breaks, all of
    /// them break together, and separate tests would only report the same failure four times.
    /// </summary>
    [Fact]
    public Task PhoneCanConfigureTheModelAgentProjectAndSkillsOfAChat() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Configurable"),
            "the chat list to arrive");

        var chat = rig.DataStore.Data.Chats.First(c => c.Title == "Configurable");
        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Configurable"));

        // The catalogs must reach the phone, or its pickers would be empty and nothing below is
        // reachable by a real user.
        await WaitAsync(() => rig.Shell.Chat.AvailableAgents.Any(a => a.Name == "Scout"),
            "the agent catalog to reach the phone");
        Assert.Contains(rig.Shell.Chat.AvailableSkills, s => s.Name == "Deploy");
        Assert.Contains(rig.Shell.Chat.AvailableProjects, p => p.Name == "Apollo");

        // ── Model ──
        rig.Shell.Chat.Model = "claude-opus-5";
        await WaitAsync(() => rig.Main.ChatVM.CurrentChat?.Id == chat.Id,
            "configuration to activate the chat on the PC");
        await WaitAsync(() => rig.Main.ChatVM.SelectedModel == "claude-opus-5",
            "the model chosen on the phone to reach the PC");

        // ── Agent ──
        rig.Shell.Chat.AgentName = "Scout";
        await WaitAsync(() => rig.Main.ChatVM.SelectedAgentName == "Scout",
            "the agent chosen on the phone to reach the PC");

        // ── Project ──
        rig.Shell.Chat.ProjectName = "Apollo";
        await WaitAsync(() => rig.Main.ChatVM.SelectedProjectName == "Apollo",
            "the project chosen on the phone to reach the PC");

        // ── Skill add, via the same collection the composer's "+" menu writes into ──
        rig.Shell.Chat.SkillChips.Add(new StrataTheme.Controls.StrataComposerChip("Deploy"));
        await WaitAsync(
            () => rig.Main.ChatVM.ActiveSkillChips
                .OfType<StrataTheme.Controls.StrataComposerChip>()
                .Any(c => c.Name == "Deploy"),
            "the skill added on the phone to reach the PC");

        // ── Skill remove ──
        await rig.Shell.Chat.RemoveSkillCommand.ExecuteAsync(
            new StrataTheme.Controls.StrataComposerChip("Deploy"));
        await WaitAsync(
            () => !rig.Main.ChatVM.ActiveSkillChips
                .OfType<StrataTheme.Controls.StrataComposerChip>()
                .Any(c => c.Name == "Deploy"),
            "the skill removed on the phone to clear on the PC");
    },
    seed: data =>
    {
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Configurable", UpdatedAt = DateTimeOffset.Now });
        data.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Apollo" });
        data.Skills.Add(new Skill { Id = Guid.NewGuid(), Name = "Deploy", IconGlyph = "🚀" });
        data.Agents.Add(new LumiAgent { Id = Guid.NewGuid(), Name = "Scout", IconGlyph = "🛰" });
    });

    /// <summary>
    /// The reverse direction: configuration changed on the PC must show up on the phone. Without the
    /// hub subscriptions added for this, the phone's pickers kept whatever they were showing when the
    /// chat was opened and quietly disagreed with the machine actually running the turn.
    /// </summary>
    [Fact]
    public Task ConfigurationChangedOnTheDesktopReachesThePhone() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Watched"),
            "the chat list to arrive");

        var chat = rig.DataStore.Data.Chats.First(c => c.Title == "Watched");
        Assert.True(await rig.Main.OpenChatByIdAsync(chat.Id));
        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Watched"));
        await WaitAsync(() => rig.Shell.Chat.ChatId == chat.Id, "the chat to open on the phone");

        // Drive the desktop the way its own composer does. The name must be a real agent: the
        // desktop reconciles an unknown one straight back to null, so a made-up value would prove
        // nothing about the push.
        rig.Main.ChatVM.SelectAgentByName("Navigator");
        await WaitAsync(() => rig.Shell.Chat.AgentName == "Navigator",
            "the agent picked on the PC to reach the phone");

        // Settle before the next step. The agent change leaves a status frame in flight, and that
        // frame also carries the rest of the configuration — so without this the assertion below
        // could be satisfied by the agent's frame rather than by its own trigger.
        var statusFrames = 0;
        rig.Client.FrameReceived += frame =>
        {
            if (frame.Event == RemoteProtocol.Events.ChatStatus)
                Interlocked.Increment(ref statusFrames);
        };
        await SettleAsync(() => Volatile.Read(ref statusFrames));

        // Reasoning effort is the strict case: picking it on the desktop changes no message, no
        // chat metadata and no transcript, so the only thing that can carry it to the phone is the
        // hub watching this property. Agent, project and skill changes all happen to rebuild the
        // transcript as well, which would mask a missing subscription.
        rig.Main.ChatVM.SelectedQuality = "Thorough";
        await WaitAsync(() => rig.Shell.Chat.Quality == "Thorough",
            "the reasoning effort picked on the PC to reach the phone");

        // Active skills are a collection, so they raise CollectionChanged rather than
        // PropertyChanged — a separate hub subscription that is easy to forget.
        rig.Main.ChatVM.ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip("Desk skill"));
        await WaitAsync(() => rig.Shell.Chat.SkillChips.Any(c => c.Name == "Desk skill"),
            "the skill attached on the PC to reach the phone");
    },
    seed: data =>
    {
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Watched", UpdatedAt = DateTimeOffset.Now });
        data.Agents.Add(new LumiAgent { Id = Guid.NewGuid(), Name = "Navigator", IconGlyph = "🧭" });
    });

    /// <summary>
    /// A phone opens on an empty surface, so the user reaches the model / agent pickers before any
    /// chat exists. Those choices used to be dropped on the floor — <c>configure_chat</c> needs a
    /// chatId, and there wasn't one — which made every picker dead on a new chat. They must now be
    /// held and replayed the moment the first message brings a chat into being.
    /// </summary>
    [Fact]
    public Task ConfiguringAnEmptySurfaceAppliesOnceTheChatExists() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");
        await WaitAsync(() => rig.Shell.Chat.AvailableAgents.Any(a => a.Name == "Scout"),
            "the agent catalog to reach the phone");

        // Nothing is open: this is the launch surface.
        Assert.False(rig.Shell.Chat.HasChat);

        rig.Shell.Chat.Model = "claude-opus-5";
        rig.Shell.Chat.AgentName = "Scout";
        Assert.True(rig.Shell.Chat.HasPendingConfiguration);

        // Sending with no chat open makes the desktop create one.
        rig.Shell.Chat.PromptText = "hello";
        await rig.Shell.Chat.SendCommand.ExecuteAsync(null);
        Assert.True(string.IsNullOrWhiteSpace(rig.Shell.Chat.ErrorText), rig.Shell.Chat.ErrorText);

        await WaitAsync(() => rig.Main.ChatVM.CurrentChat is not null, "the desktop to create a chat");
        await WaitAsync(() => rig.Shell.Chat.ChatId != Guid.Empty, "the phone to adopt the new chat");

        // The staged choices must land on the chat that was just created.
        await WaitAsync(() => rig.Main.ChatVM.SelectedModel == "claude-opus-5",
            "the model staged before the chat existed to be applied");
        await WaitAsync(() => rig.Main.ChatVM.SelectedAgentName == "Scout",
            "the agent staged before the chat existed to be applied");
        Assert.False(rig.Shell.Chat.HasPendingConfiguration);
    },
    seed: data => data.Agents.Add(new LumiAgent { Id = Guid.NewGuid(), Name = "Scout", IconGlyph = "🛰" }));

    /// <summary>
    /// announce_file is how Lumi hands over a produced file. It used to fall through into the
    /// generic tool group, which buried the deliverable inside a collapsed JSON card — on a phone
    /// that meant a file the user asked for simply never appeared.
    /// </summary>
    [Fact]
    public Task AnnouncedFilesArriveAsAFileChipNotACollapsedToolCard() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Delivered"),
            "the chat list to arrive");

        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Delivered"));
        await WaitAsync(() => rig.Shell.Chat.Turns.Count > 0, "the transcript to arrive");

        var file = Assert.IsType<FileItemViewModel>(
            rig.Shell.Chat.Turns.SelectMany(t => t.Items).SingleOrDefault(i => i is FileItemViewModel));

        Assert.Equal("report.docx", Assert.Single(file.Files).FileName);
        Assert.NotNull(Assert.Single(file.Files).MessageId);
    },
    seed: data =>
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Delivered", UpdatedAt = DateTimeOffset.Now };
        chat.Messages.Add(new ChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "make me a report" });
        chat.Messages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "tool",
            ToolName = "announce_file",
            Content = """{"filePath":"C:\\Users\\adirh\\Documents\\report.docx"}"""
        });
        chat.MessageCount = chat.Messages.Count;
        data.Chats.Add(chat);
    });

    /// <summary>
    /// The plan is chat-level state. Appending it to the last transcript turn pinned a full-size
    /// card to the bottom of every refresh, pushing the conversation up and re-rendering the whole
    /// plan on every token.
    /// </summary>
    [Fact]
    public Task PlanTravelsAsChatStateNotAsATranscriptRow() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Planned"),
            "the chat list to arrive");

        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Planned"));
        await WaitAsync(() => rig.Shell.Chat.Turns.Count > 0, "the transcript to arrive");

        Assert.DoesNotContain(
            rig.Shell.Chat.Turns.SelectMany(t => t.Items),
            i => string.Equals(i.Kind, "plan", StringComparison.Ordinal));
    },
    seed: data =>
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Planned",
            UpdatedAt = DateTimeOffset.Now,
            PlanContent = "# Plan\n\n- step one\n- step two"
        };
        chat.Messages.Add(new ChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "go" });
        chat.MessageCount = chat.Messages.Count;
        data.Chats.Add(chat);
    });

    /// <summary>
    /// A chat created remotely must not inherit a reasoning effort its model cannot accept.
    /// <c>create_chat</c> copied <c>Settings.ReasoningEffort</c> verbatim, so with the common "auto"
    /// model every first message from the phone failed at the SDK with "Reasoning effort is not
    /// supported when using the auto model" — the conversation opened, then died on send.
    /// </summary>
    [Fact]
    public Task RemotelyCreatedChatsDoNotInheritAnUnsupportedReasoningEffort() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        // Exercises the server's creation path without OPENING the chat: opening spins up a Copilot
        // session on the shared test instance, which the next test then waits behind. The assertion
        // is about what was persisted, so the data store is the right place to read it.
        await rig.Shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.CreateChat).With("open", "false"));
        await WaitAsync(() => rig.DataStore.Data.Chats.Count > 0, "the chat to be created");

        var chat = rig.DataStore.Data.Chats[0];
        Assert.Equal("auto", chat.LastModelUsed);

        // "auto" advertises no reasoning efforts, so the chat must carry none.
        Assert.True(
            string.IsNullOrEmpty(chat.LastReasoningEffortUsed),
            $"expected no effort for the auto model, got '{chat.LastReasoningEffortUsed}'");
    },
    seed: data =>
    {
        data.Settings.PreferredModel = "auto";
        data.Settings.ReasoningEffort = "high";
    });

    [Fact]
    public Task ModelCapabilitiesArrivingAfterPairing_RefreshBlankChatRunSettings() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        // This is the real startup order: the phone can pair before the SDK model catalog finishes.
        // A one-shot snapshot left the blank chat without effort/context controls for the whole run.
        const string modelId = "qa-capability-model";
        rig.Main.ChatVM.UpdateModelCapabilities(
        [
            new ModelInfo
            {
                Id = modelId,
                SupportedReasoningEfforts = ["low", "medium", "high"],
                DefaultReasoningEffort = "medium"
            }
        ],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modelId });
        rig.Main.ChatVM.ApplyAvailableModels([modelId], modelId);

        await WaitAsync(
            () => rig.Shell.Chat.AvailableModels.Contains(modelId),
            "the late model catalog to reach the phone");

        rig.Shell.Chat.Model = modelId;
        await WaitAsync(() => rig.Shell.Chat.HasQualityLevels, "reasoning levels to appear");
        await WaitAsync(() => rig.Shell.Chat.HasContextWindowTiers, "context-window tiers to appear");

        Assert.Contains("Low", rig.Shell.Chat.QualityLevels);
        Assert.Contains("Medium", rig.Shell.Chat.QualityLevels);
        Assert.Contains("High", rig.Shell.Chat.QualityLevels);
        Assert.Contains("Default", rig.Shell.Chat.ContextWindowTiers);
        Assert.Contains("Long", rig.Shell.Chat.ContextWindowTiers);
    });

    /// <summary>
    /// The command response is the phone's acceptance contract. Returning success before Lumi has
    /// passed BYOK/preflight clears the mobile composer even though no message was accepted.
    /// </summary>
    [Fact]
    public Task RejectedRemoteSend_RestoresTheMobileDraft() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(group => group.Chats).Any(chat => chat.Title == "Blocked"),
            "the blocked chat to arrive");

        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(group => group.Chats).Single(chat => chat.Title == "Blocked"));
        await WaitAsync(() => rig.Shell.Chat.ChatId != Guid.Empty, "the chat to open");

        var chat = rig.DataStore.Data.Chats.Single(candidate => candidate.Title == "Blocked");
        var messagesBefore = chat.Messages.Count;

        rig.Shell.Chat.PromptText = "do not lose this";
        await rig.Shell.Chat.SendCommand.ExecuteAsync(null);

        Assert.Equal("do not lose this", rig.Shell.Chat.PromptText);
        Assert.False(string.IsNullOrWhiteSpace(rig.Shell.Chat.ErrorText));
        Assert.Equal(messagesBefore, chat.Messages.Count);
    },
    seed: data =>
    {
        data.Settings.UseBYOKOnly = true;
        data.Settings.PreferredModel = "gpt-5-mini";
        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Blocked",
            UpdatedAt = DateTimeOffset.Now,
            LastModelUsed = "gpt-5-mini"
        });
    });

    /// <summary>
    /// A new chat on the phone has no id yet — creation is deferred to the first send. The command
    /// therefore carries no <c>chatId</c>, and the server used to fall back to "whichever chat the
    /// desktop currently has open", which posted the message into an unrelated conversation. The
    /// desktop always has *some* chat open, so this was not an edge case: every new chat sent from
    /// the phone landed in the wrong place and appended to real history.
    /// </summary>
    [Fact]
    public Task ANewChatFromThePhoneNeverPostsIntoTheDesktopsOpenChat() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Existing"),
            "the chat list to arrive");

        var existing = rig.DataStore.Data.Chats.Single(c => c.Title == "Existing");

        // Put the desktop in exactly the state that used to hijack the message.
        await rig.Main.OpenChatByIdAsync(existing.Id);
        Assert.Equal(existing.Id, rig.Main.ChatVM.CurrentChat?.Id);

        var messagesBefore = existing.Messages.Count;
        var chatsBefore = rig.DataStore.Data.Chats.Count;

        // What the phone sends for a deferred new chat: a message and no chat id.
        var sendResult = await rig.Shell.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("message", "LUMI_NEW_CHAT_TARGET_PROBE")
                .With("newChat", "true"));
        Assert.True(sendResult.Ok, sendResult.Error);

        await WaitAsync(
            () => rig.DataStore.Data.Chats.Count > chatsBefore,
            "a new chat to be created");

        // The message is appended asynchronously once the chat exists, so wait for it to land rather
        // than sampling the instant after creation.
        await WaitAsync(
            () => rig.DataStore.Data.Chats.Any(c =>
                c.Id != existing.Id
                && c.Messages.Any(m => m.Content is { } text && text.Contains("LUMI_NEW_CHAT_TARGET_PROBE"))),
            "the message to land in the newly created chat");

        // The chat the desktop had open must be untouched.
        Assert.Equal(messagesBefore, existing.Messages.Count);
        Assert.DoesNotContain(
            existing.Messages,
            m => m.Content is { } content && content.Contains("LUMI_NEW_CHAT_TARGET_PROBE"));
    },
    seed: data =>
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Existing", UpdatedAt = DateTimeOffset.Now };
        chat.Messages.Add(new ChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "an earlier conversation" });
        chat.MessageCount = chat.Messages.Count;
        data.Chats.Add(chat);
    });

    /// <summary>
    /// Selecting a project must FILTER the chat list to it, not just tag the next message — that is
    /// what a project means on Lumi desktop and in ChatGPT. A new chat started while a project is
    /// selected must also land inside it, or picking a project then tapping New would silently drop
    /// the user back out of the workspace they just entered.
    /// </summary>
    [Fact]
    public Task SelectingAProjectFiltersTheChatListAndScopesNewChats() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Count() == 3,
            "all three chats to arrive");
        await WaitAsync(() => rig.Shell.Projects.Any(p => p.Name == "Apollo"),
            "the project list to reach the drawer");

        // Filter to Apollo: only its chats survive.
        rig.Shell.SelectProjectCommand.Execute(rig.Shell.Projects.First(p => p.Name == "Apollo"));

        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Count() == 2,
            "the project-filtered chat page");
        var visible = rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Select(c => c.Title).ToList();
        Assert.Equal(["Apollo one", "Apollo two"], visible.Order().ToList());
        Assert.True(rig.Shell.HasActiveProject);

        // A new chat started under the filter belongs to that project. Creation is deferred to the
        // first message, so the project has to survive that gap and ride on the creating send —
        // asserted here by driving the same command the phone would. Not opened: that would spin up
        // a Copilot session on the shared test instance and stall the next test.
        rig.Shell.ChatList.NewChatCommand.Execute(null);
        Assert.Equal("Apollo", rig.Shell.Chat.ProjectName);
        Assert.Equal(rig.Shell.ActiveProjectId?.ToString(), rig.Shell.Chat.ProjectValue);

        var before = rig.DataStore.Data.Chats.Count;
        await rig.Shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.CreateChat)
            .With("projectId", rig.Shell.Chat.ProjectValue)
            .With("open", "false"));
        await WaitAsync(() => rig.DataStore.Data.Chats.Count > before, "the chat to be created");

        var created = rig.DataStore.Data.Chats[^1];
        var apollo = rig.DataStore.Data.Projects.First(p => p.Name == "Apollo");
        Assert.Equal(apollo.Id, created.ProjectId);

        // Clearing brings everything back.
        rig.Shell.ClearProjectCommand.Execute(null);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Loose chat"),
            "the unfiltered list to return");
        Assert.False(rig.Shell.HasActiveProject);
    },
    seed: data =>
    {
        var apollo = new Project { Id = Guid.NewGuid(), Name = "Apollo" };
        data.Projects.Add(apollo);
        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(), Title = "Apollo one",
            ProjectId = apollo.Id, UpdatedAt = DateTimeOffset.Now
        });
        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(), Title = "Apollo two",
            ProjectId = apollo.Id, UpdatedAt = DateTimeOffset.Now
        });
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Loose chat", UpdatedAt = DateTimeOffset.Now });
    });

    [Fact]
    public Task DuplicateDisplayNamesRouteByStableProjectAndLumiIds() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        var projects = rig.DataStore.Data.Projects.Where(project => project.Name == "Duplicate").ToList();
        var lumis = rig.DataStore.Data.Agents.Where(agent => agent.Name == "Duplicate Lumi").ToList();
        Assert.Equal(2, projects.Count);
        Assert.Equal(2, lumis.Count);

        var selectedProject = projects[1];
        var selectedLumi = lumis[1];
        await WaitAsync(
            () => rig.Shell.Projects.Any(project => project.Id == selectedProject.Id),
            "duplicate projects to reach the phone");

        rig.Shell.SelectProjectCommand.Execute(
            rig.Shell.Projects.Single(project => project.Id == selectedProject.Id));
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(group => group.Chats)
                .Select(chat => chat.Title)
                .SequenceEqual(["Second duplicate"]),
            "the selected duplicate project page");

        var before = rig.DataStore.Data.Chats.Count;
        var result = await rig.Shell.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.CreateChat)
                .With("projectId", selectedProject.Id.ToString())
                .With("agentId", selectedLumi.Id.ToString())
                .With("open", "false"));

        Assert.True(result.Ok, result.Error);
        await WaitAsync(() => rig.DataStore.Data.Chats.Count > before, "the id-targeted chat creation");
        var created = rig.DataStore.Data.Chats[^1];
        Assert.Equal(selectedProject.Id, created.ProjectId);
        Assert.Equal(selectedLumi.Id, created.AgentId);
    },
    seed: data =>
    {
        var firstProject = new Project { Id = Guid.NewGuid(), Name = "Duplicate" };
        var secondProject = new Project { Id = Guid.NewGuid(), Name = "Duplicate" };
        data.Projects.Add(firstProject);
        data.Projects.Add(secondProject);
        data.Agents.Add(new LumiAgent { Id = Guid.NewGuid(), Name = "Duplicate Lumi" });
        data.Agents.Add(new LumiAgent { Id = Guid.NewGuid(), Name = "Duplicate Lumi" });
        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(),
            Title = "First duplicate",
            ProjectId = firstProject.Id,
            UpdatedAt = DateTimeOffset.Now
        });
        data.Chats.Add(new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Second duplicate",
            ProjectId = secondProject.Id,
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-1)
        });
    });

    /// <summary>
    /// The phone owns its own project lens. The desktop's ChatGroups are filtered VIEW state, so using
    /// them as the wire source made the phone inherit whichever project the desktop sidebar happened
    /// to show. Clearing an empty mobile project could then recover only the active chat until restart.
    /// </summary>
    [Fact]
    public Task MobileHistoryIsIndependentOfTheDesktopsProjectFilter() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(group => group.Chats).Count() == 3,
            "all chats to arrive");

        var emptyProject = rig.DataStore.Data.Projects.Single(project => project.Name == "Empty");
        rig.Main.SelectedProjectFilter = emptyProject.Id;
        rig.Main.RefreshChatList();
        Assert.Empty(rig.Main.ChatGroups);

        // A fresh mobile snapshot must still contain the full index, not the desktop sidebar's empty
        // view. This is the exact state that previously survived Refresh and required an app restart.
        await rig.Shell.RefreshSnapshotAsync();
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(group => group.Chats).Count() == 3,
            "mobile history to remain complete");

        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            rig.Shell.ChatList.Groups
                .SelectMany(group => group.Chats)
                .Select(chat => chat.Title)
                .Order()
                .ToArray());
    },
    seed: data =>
    {
        data.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Empty" });
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Alpha", UpdatedAt = DateTimeOffset.Now });
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Beta", UpdatedAt = DateTimeOffset.Now });
        data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Gamma", UpdatedAt = DateTimeOffset.Now });
    });

    /// <summary>
    /// The user's own message must appear the instant they tap send. The real one only arrives after
    /// an HTTP round trip, an SSE invalidation and a transcript refetch — three network hops — and a
    /// screen that does not change on tap reads as a dropped input.
    /// </summary>
    [Fact]
    public Task SendingShowsTheMessageImmediatelyAndTheServerCopyReplacesIt() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Echo"),
            "the chat list to arrive");

        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Echo"));
        await WaitAsync(() => rig.Shell.Chat.ChatId != Guid.Empty, "the chat to open");

        rig.Shell.Chat.PromptText = "instant please";
        var send = rig.Shell.Chat.SendCommand.ExecuteAsync(null);

        // Synchronously after the command starts — before any network hop can have completed —
        // the message is already on screen.
        Assert.Contains(
            rig.Shell.Chat.Turns.SelectMany(t => t.Items),
            i => i is UserTurnItemViewModel { Text: "instant please" });

        await send;

        // And once the server's own transcript lands it must appear exactly once, not twice.
        //
        // Applied directly rather than waited for: getting there via a real reply needs a live
        // Copilot turn, and whether that finishes inside the timeout depends on what the PREVIOUS
        // test left the shared instance doing — which made this assertion fail or pass according to
        // test order rather than according to the behaviour it is checking. The frame below is the
        // same one the SSE path delivers, so the contract under test is unchanged.
        rig.Shell.Chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = rig.Shell.Chat.ChatId,
            Revision = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "server-turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "server-user",
                            Kind = RemoteProtocol.ItemKinds.User,
                            Text = "instant please"
                        }
                    ]
                }
            ]
        });

        Assert.Equal(
            1,
            rig.Shell.Chat.Turns
                .SelectMany(t => t.Items)
                .Count(i => i is UserTurnItemViewModel { Text: "instant please" }));
    },
    seed: data => data.Chats.Add(new Chat { Id = Guid.NewGuid(), Title = "Echo", UpdatedAt = DateTimeOffset.Now }));

    /// <summary>
    /// The transcript-level "thinking" dots and the streaming assistant row must never both show:
    /// together they read as Lumi thinking twice.
    /// </summary>
    [Fact]
    public void ThinkingIndicatorYieldsToTheStreamingRow()
    {
        var chat = new MobileChatViewModel(new NullSink());

        chat.ApplyStatus(new RemoteChatStatus { IsBusy = true, IsStreaming = false });
        Assert.True(chat.ShowThinking);

        chat.ApplyTranscript(new RemoteTranscript
        {
            ChatId = Guid.Empty,
            Revision = 1,
            Turns =
            [
                new RemoteTranscriptTurn
                {
                    Id = "turn",
                    Items =
                    [
                        new RemoteTranscriptItem
                        {
                            Id = "assistant",
                            Kind = RemoteProtocol.ItemKinds.Assistant,
                            IsStreaming = true
                        }
                    ]
                }
            ],
            Status = new RemoteChatStatus { IsBusy = true, IsStreaming = true }
        });
        Assert.False(chat.ShowThinking);

        chat.ApplyStatus(new RemoteChatStatus { IsBusy = false, IsStreaming = false });
        Assert.False(chat.ShowThinking);
    }

    /// <summary>
    /// The context meter's inputs. A percentage alone is not enough: the phone renders a token
    /// count and grades the meter's colour, so both the fraction and the bucket must be right.
    /// </summary>
    [Fact]
    public void ContextMeterReportsUsagePressureAndTokenCounts()
    {
        var chat = new MobileChatViewModel(new NullSink());

        chat.ApplyStatus(new RemoteChatStatus { ContextCurrentTokens = 46_812, ContextTokenLimit = 200_000 });
        Assert.Equal(23, chat.ContextPercent);
        Assert.Equal("46.8K / 200K tokens", chat.ContextDetailText);
        Assert.False(chat.IsContextWarn);
        Assert.False(chat.IsContextCritical);

        chat.ApplyStatus(new RemoteChatStatus { ContextCurrentTokens = 140_000, ContextTokenLimit = 200_000 });
        Assert.True(chat.IsContextWarn);
        Assert.False(chat.IsContextCritical);

        chat.ApplyStatus(new RemoteChatStatus { ContextCurrentTokens = 190_000, ContextTokenLimit = 200_000 });
        Assert.False(chat.IsContextWarn);
        Assert.True(chat.IsContextCritical);

        // An unknown limit must render nothing rather than a misleading 0%.
        chat.ApplyStatus(new RemoteChatStatus { ContextCurrentTokens = 500, ContextTokenLimit = 0 });
        Assert.False(chat.HasContextUsage);
        Assert.Equal("", chat.ContextUsageText);
    }

    private sealed class NullSink : IRemoteCommandSink
    {
        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command) =>
            Task.FromResult(new RemoteCommandResult { Ok = true });

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true, Path = $@"C:\Temp\{fileName}", FileName = fileName });
    }

    [Fact]
    public Task Server_AnswersHelloBeforePairingAndRefusesEverythingElse() => RunAsync(async rig =>
    {
        var hello = await rig.Client.HelloAsync(rig.BaseUrl, CancellationToken.None);

        Assert.NotNull(hello);
        Assert.Equal(RemoteProtocol.Version, hello!.ProtocolVersion);
        Assert.Equal(Environment.MachineName, hello.HostName);
        Assert.False(hello.IsPaired);

        // No token yet: the data routes must not answer.
        rig.Client.Configure(rig.BaseUrl, null);
        Assert.Null(await rig.Client.GetSnapshotAsync(CancellationToken.None));
    });

    [Theory]
    [InlineData("update_settings")]
    [InlineData("navigate")]
    [InlineData("move_chat")]
    public Task RemovedRemoteActions_AreRejectedAsUnknown(string action) => RunAsync(async rig =>
    {
        await PairAsync(rig);

        var result = await rig.Shell.SendCommandAsync(new RemoteCommand(action));

        Assert.False(result.Ok);
        Assert.Contains("Unknown remote action", result.Error, StringComparison.Ordinal);
    });

    [Fact]
    public Task Pairing_RequiresTheCodeAndIssuesASingleUseToken() => RunAsync(async rig =>
    {
        var code = rig.Server.BeginPairing();
        Assert.Matches("^[0-9]{6}$", code);
        Assert.True(rig.Main.SettingsVM.IsRemotePairing);
        Assert.Equal(code, rig.Main.SettingsVM.RemotePairingCode);
        Assert.Equal(Loc.Get("Remote_PairStop"), rig.Main.SettingsVM.RemotePairActionText);

        var wrongCode = code == "000000" ? "999999" : "000000";
        var wrong = await rig.Client.PairAsync(rig.BaseUrl, wrongCode, CancellationToken.None);
        Assert.False(wrong.Ok);
        Assert.NotNull(wrong.Error);

        var right = await rig.Client.PairAsync(rig.BaseUrl, code, CancellationToken.None);
        Assert.True(right.Ok);
        Assert.False(string.IsNullOrWhiteSpace(right.Token));

        // The code is burned on use, so a shoulder-surfer cannot pair a second device with it.
        Assert.Null(rig.Server.ActivePairingCode);
        await WaitAsync(
            () => !rig.Main.SettingsVM.IsRemotePairing,
            "Settings to clear a consumed pairing code");
        Assert.Empty(rig.Main.SettingsVM.RemotePairingCode);
        Assert.Equal(Loc.Get("Remote_PairButton"), rig.Main.SettingsVM.RemotePairActionText);
        Assert.False((await rig.Client.PairAsync(rig.BaseUrl, code, CancellationToken.None)).Ok);

        Assert.Contains(rig.DataStore.Data.Settings.RemotePairedDevices, d => d.DeviceName == "Test Phone");
    });

    [Fact]
    public Task PairingCodeAllowsOnlyOneConcurrentValidConnection() => RunAsync(async rig =>
    {
        var code = rig.Server.BeginPairing();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 24)
            .Select(index => PairFromDeviceAsync(index))
            .ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Ok);
        Assert.Single(rig.DataStore.Data.Settings.RemotePairedDevices);
        Assert.Null(rig.Server.ActivePairingCode);
        return;

        async Task<RemotePairResponse> PairFromDeviceAsync(int index)
        {
            await start.Task;
            await using var client = new LumiRemoteClient($"race-device-{index}", $"Race Phone {index}");
            return await client.PairAsync(rig.BaseUrl, code, CancellationToken.None);
        }
    });

    [Fact]
    public Task PairingFailedAttemptLimitIsResetByANewCode() => RunAsync(async rig =>
    {
        var firstCode = rig.Server.BeginPairing();
        var wrongCode = firstCode == "000000" ? "999999" : "000000";

        for (var attempt = 1; attempt <= LumiRemoteServer.PairingFailedAttemptLimit; attempt++)
        {
            var response = await rig.Client.PairAsync(rig.BaseUrl, wrongCode, CancellationToken.None);
            Assert.False(response.Ok);

            if (attempt < LumiRemoteServer.PairingFailedAttemptLimit)
                Assert.Contains("not correct", response.Error, StringComparison.OrdinalIgnoreCase);
            else
                Assert.Contains("too many", response.Error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(rig.Server.ActivePairingCode);
        await WaitAsync(
            () => !rig.Main.SettingsVM.IsRemotePairing,
            "Settings to clear a locked-out pairing code");
        Assert.Empty(rig.Main.SettingsVM.RemotePairingCode);
        Assert.Equal(Loc.Get("Remote_PairButton"), rig.Main.SettingsVM.RemotePairActionText);

        var replacementCode = rig.Server.BeginPairing();
        var paired = await rig.Client.PairAsync(rig.BaseUrl, replacementCode, CancellationToken.None);

        Assert.True(paired.Ok, paired.Error);
        Assert.Single(rig.DataStore.Data.Settings.RemotePairedDevices);
    });

    [Fact]
    public Task PairingPresentationExpiresAtTheServerDeadline() => RunAsync(rig =>
    {
        var code = rig.Server.BeginPairing();
        Assert.Equal(code, rig.Main.SettingsVM.RemotePairingCode);
        Assert.True(rig.Main.SettingsVM.IsRemotePairing);

        rig.Main.SettingsVM.RefreshRemoteState(
            DateTimeOffset.UtcNow + RemoteProtocol.PairingCodeLifetime + TimeSpan.FromSeconds(1));

        Assert.Null(rig.Server.ActivePairingCode);
        Assert.False(rig.Main.SettingsVM.IsRemotePairing);
        Assert.Empty(rig.Main.SettingsVM.RemotePairingCode);
        Assert.Equal(Loc.Get("Remote_PairButton"), rig.Main.SettingsVM.RemotePairActionText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task OversizedOrdinaryRequestReceivesAJson413Response() => RunAsync(async rig =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rig.Server.Port, timeout.Token);
        await using var stream = client.GetStream();

        var header = Encoding.ASCII.GetBytes(
            $"POST {RemoteProtocol.Routes.Command} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{rig.Server.Port}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {RemoteHttpListener.OrdinaryRequestBodyLimitBytes + 1}\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");
        await stream.WriteAsync(header, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var wireResponse = await reader.ReadToEndAsync(timeout.Token);

        Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", wireResponse, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/json", wireResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Access-Control-Allow-Origin", wireResponse, StringComparison.OrdinalIgnoreCase);

        var bodyStart = wireResponse.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(bodyStart >= 0);
        var error = JsonSerializer.Deserialize(
            wireResponse[(bodyStart + 4)..],
            RemoteJsonContext.Default.RemoteCommandResult);
        Assert.NotNull(error);
        Assert.False(error!.Ok);
        Assert.Contains("Pair this device", error.Error, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task RawUploadAboveTheOrdinaryBodyLimitIsAccepted() => RunAsync(async rig =>
    {
        var code = rig.Server.BeginPairing();
        var paired = await rig.Client.PairAsync(rig.BaseUrl, code, CancellationToken.None);
        Assert.True(paired.Ok, paired.Error);

        var bytes = new byte[9 * 1024 * 1024];
        bytes[0] = 0x2A;
        bytes[^1] = 0x7F;
        Assert.True(bytes.LongLength > RemoteHttpListener.OrdinaryRequestBodyLimitBytes);

        var uploaded = await rig.Client.UploadAsync(
            $"route-aware-{Guid.NewGuid():N}.bin",
            bytes,
            CancellationToken.None);

        Assert.True(uploaded.Ok, uploaded.Error);
        Assert.NotNull(uploaded.Path);
        try
        {
            Assert.True(File.Exists(uploaded.Path));
            var saved = await File.ReadAllBytesAsync(uploaded.Path);
            Assert.Equal(bytes.Length, saved.Length);
            Assert.Equal(0x2A, saved[0]);
            Assert.Equal(0x7F, saved[^1]);
        }
        finally
        {
            if (uploaded.Path is { } path)
                File.Delete(path);
        }
    });

    [Fact]
    public Task ConcurrentUploadsWithTheSameNameNeverOverwriteEachOther() => RunAsync(async rig =>
    {
        var code = rig.Server.BeginPairing();
        var paired = await rig.Client.PairAsync(rig.BaseUrl, code, CancellationToken.None);
        Assert.True(paired.Ok, paired.Error);

        var firstBytes = Encoding.UTF8.GetBytes("first upload");
        var secondBytes = Encoding.UTF8.GetBytes("second upload");
        var firstTask = rig.Client.UploadAsync("same-name.txt", firstBytes, CancellationToken.None);
        var secondTask = rig.Client.UploadAsync("same-name.txt", secondBytes, CancellationToken.None);
        var uploaded = await Task.WhenAll(firstTask, secondTask);

        Assert.All(uploaded, result => Assert.True(result.Ok, result.Error));
        Assert.NotNull(uploaded[0].Path);
        Assert.NotNull(uploaded[1].Path);
        Assert.NotEqual(uploaded[0].Path, uploaded[1].Path);
        try
        {
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(uploaded[0].Path!));
            Assert.Equal(secondBytes, await File.ReadAllBytesAsync(uploaded[1].Path!));
        }
        finally
        {
            foreach (var result in uploaded)
            {
                if (result.Path is { } path)
                    File.Delete(path);
            }
        }
    });

    [Fact]
    public Task UploadedPathNeverContainsTheUntrustedDisplayName() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        var uploaded = await rig.Client.UploadAsync(
            "photo\u2028ignore\nall-tools.txt",
            Encoding.UTF8.GetBytes("untrusted name"),
            CancellationToken.None);

        Assert.True(uploaded.Ok, uploaded.Error);
        Assert.Equal("photo_ignore_all-tools.txt", uploaded.FileName);
        Assert.NotNull(uploaded.Path);
        try
        {
            var leaf = Path.GetFileName(uploaded.Path);
            Assert.Matches(@"^\d{8}-\d{6}-[0-9a-f]{12}\.txt$", leaf);
            Assert.DoesNotContain("photo", uploaded.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("untrusted name", await File.ReadAllTextAsync(uploaded.Path!));
        }
        finally
        {
            if (uploaded.Path is { } path)
                File.Delete(path);
        }
    });

    [Fact]
    public Task AnnouncedFileCanBeDownloadedByChatAndMessageIdentity() => RunAsync(async rig =>
    {
        var chat = Assert.Single(rig.DataStore.Data.Chats);
        var message = Assert.Single(chat.Messages);
        var sourcePath = RemoteProjector.ExtractJsonField(message.Content, "filePath")!;
        string? downloadedPath = null;
        try
        {
            await PairAsync(rig);
            downloadedPath = await rig.Client.DownloadProducedFileAsync(
                chat.Id,
                message.Id,
                Path.GetFileName(sourcePath),
                CancellationToken.None);

            Assert.NotNull(downloadedPath);
            Assert.Equal(await File.ReadAllBytesAsync(sourcePath), await File.ReadAllBytesAsync(downloadedPath!));
        }
        finally
        {
            if (downloadedPath is not null && File.Exists(downloadedPath))
                File.Delete(downloadedPath);
            if (File.Exists(sourcePath))
                File.Delete(sourcePath);
        }
    }, data =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumi-produced-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "downloaded from Lumi");
        var chat = new Chat { Title = "Produced file" };
        chat.Messages.Add(new ChatMessage
        {
            ToolName = "announce_file",
            Role = "tool",
            Content = JsonSerializer.Serialize(new { filePath = path })
        });
        chat.MessageCount = 1;
        data.Chats.Add(chat);
    });

    [Fact]
    public Task TimedOutRetryWithTheSameRequestIdDoesNotDuplicateASteeredMessage() => RunAsync(async rig =>
    {
        var code = rig.Server.BeginPairing();
        var paired = await rig.Client.PairAsync(rig.BaseUrl, code, CancellationToken.None);
        Assert.True(paired.Ok, paired.Error);

        var chat = Assert.Single(rig.DataStore.Data.Chats);
        rig.Main.ChatVM.CurrentChat = chat;
        var runtimes = GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(
            rig.Main.ChatVM,
            "_runtimeStates");
        runtimes[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            TurnInProgress = true,
            IsStreaming = true
        };
        var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
        {
            RequestId = Guid.NewGuid().ToString("N")
        }
            .With("chatId", chat.Id.ToString())
            .With("message", "only once")
            .With("steer", "true");

        await SendAndAbandonTrackedCommandAsync(rig, command);
        var retried = await rig.Client.SendCommandAsync(command, CancellationToken.None);
        var completedRetry = await rig.Client.SendCommandAsync(command, CancellationToken.None);

        Assert.True(retried.Ok, retried.Error);
        Assert.True(completedRetry.Ok, completedRetry.Error);
        Assert.Equal(command.RequestId, retried.RequestId);
        Assert.Equal(retried.ChatId, completedRetry.ChatId);
        Assert.Single(chat.Messages, message => message.Content == "only once");
    },
    seed: data =>
    {
        var chat = new Chat { Title = "Busy chat", UpdatedAt = DateTimeOffset.Now };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "initial" });
        chat.MessageCount = 1;
        data.Chats.Add(chat);
    });

    [Fact]
    public Task TimedOutNewChatRetryReturnsTheOriginalFailureWithoutCreatingAnotherBlankChat() =>
        RunAsync(async rig =>
        {
            var code = rig.Server.BeginPairing();
            var paired = await rig.Client.PairAsync(rig.BaseUrl, code, CancellationToken.None);
            Assert.True(paired.Ok, paired.Error);
            var command = new RemoteCommand(RemoteProtocol.Actions.SendMessage)
            {
                RequestId = Guid.NewGuid().ToString("N")
            }
                .With("newChat", "true")
                .With("message", "must stay private")
                .With("model", "gpt-5-mini");

            await SendAndAbandonTrackedCommandAsync(rig, command);
            var retried = await rig.Client.SendCommandAsync(command, CancellationToken.None);
            var completedRetry = await rig.Client.SendCommandAsync(command, CancellationToken.None);

            Assert.False(retried.Ok);
            Assert.False(completedRetry.Ok);
            Assert.NotNull(retried.ChatId);
            Assert.Equal(retried.ChatId, completedRetry.ChatId);
            Assert.Equal(command.RequestId, retried.RequestId);
            var chat = Assert.Single(rig.DataStore.Data.Chats);
            Assert.Equal(retried.ChatId, chat.Id);
            Assert.Empty(chat.Messages);
        },
        seed: data =>
        {
            data.Settings.UseBYOKOnly = true;
            data.Settings.PreferredModel = "gpt-5-mini";
        });

    [Fact]
    public Task PairedPhone_SeesTheDesktopChatsProjectsAndLibrary() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Weekend plans"),
            "the desktop's chats to reach the phone");

        var chat = rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Weekend plans");
        Assert.Equal("Lumi", chat.ProjectName);
        Assert.Equal("Let's go hiking this weekend.", chat.Preview);
        Assert.Equal("Adir", rig.Shell.UserName);
        Assert.Equal(Environment.MachineName, rig.Shell.HostName);

        rig.Shell.Library.Section = LibrarySection.Skills;
        Assert.Contains(rig.Shell.Library.Entries, e => e.Name == "Document Creator");

        rig.Shell.Library.Section = LibrarySection.Memories;
        Assert.Contains(rig.Shell.Library.Entries, e => e.Name == "Likes");

        rig.Shell.Library.Section = LibrarySection.Projects;
        Assert.Contains(rig.Shell.Library.Entries, e => e.Name == "Lumi");
    },
    seed: data =>
    {
        var projectId = Guid.NewGuid();
        data.Projects.Add(new Project { Id = projectId, Name = "Lumi", Instructions = "Be great" });
        data.Skills.Add(new Skill { Id = Guid.NewGuid(), Name = "Document Creator", Content = "..." });
        data.Memories.Add(new Memory { Id = Guid.NewGuid(), Key = "Likes", Content = "burgers" });
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Title = "Weekend plans",
            ProjectId = projectId,
            UpdatedAt = DateTimeOffset.Now
        };
        chat.Messages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "assistant",
            Content = "Let's go hiking this weekend."
        });
        chat.MessageCount = chat.Messages.Count;
        data.Chats.Add(chat);
    });

    [Fact]
    public Task OpeningAChatFromThePhoneRendersTheRealTranscript() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "History"),
            "the chat list to arrive");

        var item = rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "History");
        rig.Shell.ChatList.OpenChatCommand.Execute(item);

        await WaitAsync(() => rig.Shell.Chat.Turns.Count > 0, "the transcript to arrive");

        var items = rig.Shell.Chat.Turns[0].Items;
        Assert.Equal("What's the weather?", Assert.IsType<UserTurnItemViewModel>(items[0]).Text);
        Assert.Contains(items, i => i is ToolGroupItemViewModel);
        Assert.Contains(items, i => i is AssistantItemViewModel a && a.Text == "It's sunny.");
    },
    seed: data =>
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "History", UpdatedAt = DateTimeOffset.Now };
        chat.Messages.Add(new ChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "What's the weather?" });
        chat.Messages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            Role = "tool",
            Content = "sunny",
            ToolName = "web_search",
            ToolStatus = "Completed"
        });
        chat.Messages.Add(new ChatMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "It's sunny." });
        chat.MessageCount = chat.Messages.Count;
        data.Chats.Add(chat);
    });

    [Fact]
    public Task PhoneCanCreateRenamePinAndDeleteChats() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        // Creation is deferred to the first message on the phone; this drives the server path
        // directly. Not opened: opening spins up a Copilot session on the shared test instance,
        // which the next test would then wait behind.
        await rig.Shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.CreateChat).With("open", "false"));
        await WaitAsync(() => rig.DataStore.Data.Chats.Count > 0, "the new chat to reach the desktop");

        var created = rig.DataStore.Data.Chats[0];

        await rig.Shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.RenameChat)
            .With("chatId", created.Id.ToString())
            .With("title", "Renamed from phone"));
        await WaitAsync(() => created.Title == "Renamed from phone", "the rename to apply");

        await rig.Shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.PinChat)
            .With("chatId", created.Id.ToString())
            .With("pinned", "true"));
        await WaitAsync(() => created.IsPinned, "the pin to apply");

        await rig.Shell.SendCommandAsync(new RemoteCommand(RemoteProtocol.Actions.DeleteChat)
            .With("chatId", created.Id.ToString()));
        await WaitAsync(() => rig.DataStore.Data.Chats.Count == 0, "the delete to apply");
    });

    [Fact]
    public Task RemoteDeleteDoesNotConfirmAnotherChatsPendingWorktreeDialog() => RunAsync(async rig =>
    {
        var worktree = NewTempDir();
        try
        {
            Directory.CreateDirectory(worktree);
            var pending = new Chat
            {
                Id = Guid.NewGuid(),
                Title = "Pending dialog",
                WorktreePath = worktree,
                UpdatedAt = DateTimeOffset.Now
            };
            var remoteTarget = new Chat
            {
                Id = Guid.NewGuid(),
                Title = "Remote target",
                UpdatedAt = DateTimeOffset.Now
            };
            rig.DataStore.Data.Chats.AddRange([pending, remoteTarget]);
            rig.Main.RefreshChatList();
            rig.Main.DeleteChatCommand.Execute(pending);
            Assert.True(rig.Main.IsWorktreeDeleteDialogOpen);

            await PairAsync(rig);
            var deleted = await rig.Client.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.DeleteChat)
                    .With("chatId", remoteTarget.Id.ToString()),
                CancellationToken.None);

            Assert.True(deleted.Ok, deleted.Error);
            Assert.Contains(pending, rig.DataStore.Data.Chats);
            Assert.DoesNotContain(remoteTarget, rig.DataStore.Data.Chats);
            Assert.True(rig.Main.IsWorktreeDeleteDialogOpen);
            Assert.True(Directory.Exists(worktree));
        }
        finally
        {
            if (Directory.Exists(worktree))
                Directory.Delete(worktree, recursive: true);
        }
    });

    [Fact]
    public Task PhoneCanManageTheLibraryThroughTheRealFeatureManager() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        rig.Shell.Library.Section = LibrarySection.Skills;
        rig.Shell.Library.BeginCreateCommand.Execute(null);
        rig.Shell.Library.EditName = "Phone skill";
        rig.Shell.Library.EditDescription = "Made from a phone";
        rig.Shell.Library.EditBody = "Do the thing.";
        await rig.Shell.Library.SaveCommand.ExecuteAsync(null);

        await WaitAsync(() => rig.DataStore.Data.Skills.Any(s => s.Name == "Phone skill"),
            "the skill to be created on the desktop");

        Assert.Equal("Do the thing.", rig.DataStore.Data.Skills.First(s => s.Name == "Phone skill").Content);

        // And the change must come back down to the phone without a manual refresh.
        await WaitAsync(() => rig.Shell.Library.Entries.Any(e => e.Name == "Phone skill"),
            "the new skill to be pushed back to the phone");

        await rig.Shell.Library.DeleteCommand.ExecuteAsync(
            rig.Shell.Library.Entries.First(e => e.Name == "Phone skill"));

        await WaitAsync(() => rig.DataStore.Data.Skills.All(s => s.Name != "Phone skill"),
            "the skill to be deleted on the desktop");
    });

    [Fact]
    public Task ExistingLongLibraryContentIsNeverReplacedByItsMobileProjection() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        rig.Shell.Library.Section = LibrarySection.Skills;
        await WaitAsync(
            () => rig.Shell.Library.Entries.Any(item => item.Name == "Long mobile skill"),
            "the long skill to arrive on the phone");
        var entry = rig.Shell.Library.Entries.Single(item => item.Name == "Long mobile skill");

        await rig.Shell.Library.BeginEditCommand.ExecuteAsync(entry);
        Assert.EndsWith("FULL-SUFFIX", rig.Shell.Library.EditBody, StringComparison.Ordinal);
        rig.Shell.Library.EditName = "Renamed long mobile skill";
        await rig.Shell.Library.SaveCommand.ExecuteAsync(null);

        var saved = rig.DataStore.Data.Skills.Single(skill => skill.Id.ToString() == entry.Identifier);
        Assert.Equal("Renamed long mobile skill", saved.Name);
        Assert.EndsWith("FULL-SUFFIX", saved.Content, StringComparison.Ordinal);
    }, data =>
    {
        data.Skills.Add(new Skill
        {
            Name = "Long mobile skill",
            Description = "Full detail regression",
            Content = new string('x', 12 * 1024) + "FULL-SUFFIX"
        });
    });

    [Fact]
    public Task LibraryEditsFromThePhoneArePushedBackThroughDedicatedLibraryFrames() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");

        // Record the dedicated frame. Asserting only on Library.Entries is not enough: a separate
        // catalog snapshot or reconnect can also carry the library and make a broken push look live.
        var libraryFrames = 0;
        rig.Client.FrameReceived += frame =>
        {
            if (frame.Event == RemoteProtocol.Events.Library)
                Interlocked.Increment(ref libraryFrames);
        };

        rig.Shell.Library.Section = LibrarySection.Projects;
        rig.Shell.Library.BeginCreateCommand.Execute(null);
        rig.Shell.Library.EditName = "Phone Made Project";
        rig.Shell.Library.EditBody = "Created from the phone.";
        await rig.Shell.Library.SaveCommand.ExecuteAsync(null);

        await WaitAsync(() => rig.DataStore.Data.Projects.Any(p => p.Name == "Phone Made Project"),
            "the project to be created on the desktop");

        // The regression: the desktop only announced library changes when the *agent* edited a
        // feature, so a phone edit updated the desktop and the file on disk while every connected
        // phone — including the one that made the edit — kept rendering an empty list.
        await WaitAsync(() => Volatile.Read(ref libraryFrames) > 0,
            "the desktop to broadcast a library event after the phone's edit");

        await WaitAsync(() => rig.Shell.Library.Entries.Any(e => e.Name == "Phone Made Project"),
            "the new project to be pushed back to the phone");

        await rig.Shell.Library.DeleteCommand.ExecuteAsync(
            rig.Shell.Library.Entries.First(e => e.Name == "Phone Made Project"));

        // The delete may coalesce into the full-catalog resync started by the create frame. The
        // original regression is still pinned above (at least one dedicated library frame must
        // leave the desktop); final state is authoritative regardless of which update arrives last.
        await WaitAsync(() => rig.Shell.Library.Entries.All(e => e.Name != "Phone Made Project"),
            "the delete to be pushed back to the phone");
    });

    [Fact]
    public Task LibraryEditsMadeOnTheDesktopReachThePhoneLive() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");

        var libraryFrames = 0;
        rig.Client.FrameReceived += frame =>
        {
            if (frame.Event == RemoteProtocol.Events.Library)
                Interlocked.Increment(ref libraryFrames);
        };

        var framesBefore = await SettleAsync(() => Volatile.Read(ref libraryFrames));

        rig.Shell.Library.Section = LibrarySection.Projects;

        // The mirror image of the phone-edit regression: a desktop CRUD page (Projects, Skills,
        // Lumis, Memories, MCP servers…) just mutates AppData and calls DataStore.SaveAsync. It
        // raises neither FeatureManagementStateChanged nor anything the router sees, so the phone
        // used to keep rendering the pre-edit list until the user reconnected it.
        rig.DataStore.Data.Projects.Add(new Project { Name = "Desktop Made Project", Instructions = "Typed on the PC." });
        await rig.DataStore.SaveAsync();

        await WaitAsync(() => Volatile.Read(ref libraryFrames) > framesBefore,
            "the desktop to broadcast a library event after its own edit");

        await WaitAsync(() => rig.Shell.Library.Entries.Any(e => e.Name == "Desktop Made Project"),
            "the desktop's new project to reach the phone");
    });

    [Fact]
    public Task DesktopChangesArePushedToThePhoneLive() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");

        // A chat created on the PC must appear on the phone with no polling.
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Typed on the PC", UpdatedAt = DateTimeOffset.Now };
        rig.DataStore.Data.Chats.Add(chat);
        rig.DataStore.MarkChatChanged(chat);
        rig.Main.RefreshChatList();

        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Typed on the PC"),
            "the desktop's new chat to reach the phone");
    });

    [Fact]
    public Task DesktopTranscriptChangesReachTheOpenChatOnThePhone() => RunAsync(async rig =>
    {
        // Regression: the hub broadcast a transcript-invalidated object while the phone parsed the
        // payload as a bare GUID, so the refetch never fired. A phone watching a live chat showed an
        // empty transcript and a stuck "Thinking…" while the PC finished the turn normally.
        await PairAsync(rig);
        await WaitAsync(() => rig.Shell.IsConnected, "the event stream to attach");
        await WaitAsync(
            () => rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).Any(c => c.Title == "Live turn"),
            "the chat list to arrive");

        var chat = rig.DataStore.Data.Chats.First(c => c.Title == "Live turn");
        Assert.True(await rig.Main.OpenChatByIdAsync(chat.Id));
        rig.Shell.ChatList.OpenChatCommand.Execute(
            rig.Shell.ChatList.Groups.SelectMany(g => g.Chats).First(c => c.Title == "Live turn"));

        await WaitAsync(() => rig.Shell.Chat.ChatId == chat.Id, "the chat to open on the phone");
        await WaitAsync(() => rig.Shell.Chat.Turns.Count > 0, "the initial transcript to arrive");
        Assert.DoesNotContain(
            rig.Shell.Chat.Turns.SelectMany(t => t.Items),
            i => i is AssistantItemViewModel { Text: "PHONE_E2E_OK" });

        // Watch the wire as well as the UI: an invalidation that never leaves the desktop and one the
        // phone drops are different bugs, and asserting only on the rendered transcript hides which.
        var invalidations = 0;
        rig.Client.FrameReceived += frame =>
        {
            if (frame.Event == RemoteProtocol.Events.TranscriptInvalidated)
                Interlocked.Increment(ref invalidations);
        };

        // Opening a chat legitimately invalidates once. Let that settle, otherwise the in-flight
        // event is miscounted as the reply's and the assertion passes even when live push is dead.
        var before = await SettleAsync(() => Volatile.Read(ref invalidations));

        // The desktop finishes the turn the way a real reply lands: into the model and into the
        // ChatViewModel collection the desktop renders from.
        var reply = new ChatMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "PHONE_E2E_OK" };
        chat.Messages.Add(reply);
        chat.MessageCount = chat.Messages.Count;
        rig.Main.ChatVM.Messages.Add(new ChatMessageViewModel(reply));

        await WaitAsync(() => Volatile.Read(ref invalidations) > before,
            "the desktop to broadcast a transcript invalidation for the new reply");

        await WaitAsync(
            () => rig.Shell.Chat.Turns.SelectMany(t => t.Items)
                .Any(i => i is AssistantItemViewModel { Text: "PHONE_E2E_OK" }),
            "the desktop's reply to reach the phone's open transcript");
    },
    seed: data =>
    {
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Live turn", UpdatedAt = DateTimeOffset.Now };
        chat.Messages.Add(new ChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "Reply with exactly: PHONE_E2E_OK" });
        chat.MessageCount = chat.Messages.Count;
        data.Chats.Add(chat);
    });

    [Fact]
    public Task ForgettingThePhoneOnTheDesktopRevokesItsAccess() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        Assert.NotNull(await rig.Client.GetSnapshotAsync(CancellationToken.None));

        // Revoke on the desktop, the way Settings does.
        var device = Assert.Single(rig.DataStore.Data.Settings.RemotePairedDevices);
        Assert.True(await rig.Server.RevokeDeviceAsync(device.DeviceId));

        Assert.Empty(rig.DataStore.Data.Settings.RemotePairedDevices);
        Assert.Null(await rig.Client.GetSnapshotAsync(CancellationToken.None));
    });

    [Fact]
    public Task RevokedDeviceCannotRegisterAnEventStreamFromStaleAuthorization() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        var staleDevice = Assert.Single(rig.DataStore.SnapshotRemotePairedDevices());
        Assert.True(await rig.Server.RevokeDeviceAsync(staleDevice.DeviceId));

        using var hub = new RemoteEventHub(rig.DataStore, rig.Main, () => []);
        var client = await rig.Server.TryRegisterEventClientAsync(
            hub,
            Stream.Null,
            staleDevice,
            new RemoteEventFrame(RemoteProtocol.Events.Snapshot, "{}"),
            new IPEndPoint(IPAddress.Loopback, 50000),
            new IPEndPoint(IPAddress.Loopback, rig.Server.Port),
            CancellationToken.None);

        client?.Dispose();
        Assert.Null(client);
    });

    [Fact]
    public Task PendingLanEventStreamCannotRegisterAfterLanAccessIsDisabled() => RunAsync(async rig =>
    {
        await PairAsync(rig);
        var device = Assert.Single(rig.DataStore.SnapshotRemotePairedDevices());
        rig.DataStore.Data.Settings.RemoteAllowInsecureLan = true;
        rig.DataStore.Data.Settings.RemoteAllowInsecureLan = false;

        using var hub = new RemoteEventHub(rig.DataStore, rig.Main, () => []);
        var client = await rig.Server.TryRegisterEventClientAsync(
            hub,
            Stream.Null,
            device,
            new RemoteEventFrame(RemoteProtocol.Events.Snapshot, "{}"),
            new IPEndPoint(IPAddress.Parse("192.168.1.25"), 50000),
            new IPEndPoint(IPAddress.Parse("192.168.1.10"), rig.Server.Port),
            CancellationToken.None);

        client?.Dispose();
        Assert.Null(client);
    });

    [Fact]
    public Task PhoneCanRevokeItsOwnLongLivedToken() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        var result = await rig.Client.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.RevokeDevice),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Empty(rig.DataStore.Data.Settings.RemotePairedDevices);
        Assert.Null(await rig.Client.GetSnapshotAsync(CancellationToken.None));
    });

    [Fact]
    public Task MultipleSettingsViewModelsReflectTheSharedRemoteServer() => RunAsync(rig =>
    {
        using var secondary = new MainViewModel(
            rig.DataStore,
            TestCopilot.Shared,
            new UpdateService(),
            initializeCopilotOnStartup: false);
        secondary.SettingsVM.AttachRemoteServer(rig.Server);

        Assert.True(secondary.SettingsVM.RemoteAccessEnabled);
        Assert.Contains("Listening", secondary.SettingsVM.RemoteStatusText);

        rig.Main.SettingsVM.RemoteAllowInsecureLan = true;
        Assert.True(secondary.SettingsVM.RemoteAllowInsecureLan);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReconnectingWithAStoredTokenSkipsPairing() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        var hello = await rig.Client.HelloAsync(rig.BaseUrl, CancellationToken.None);

        Assert.NotNull(hello);
        Assert.True(hello!.IsPaired);
    });

    [Fact]
    public Task DesktopCopilotStateDoesNotClobberThePhonesLinkState() => RunAsync(async rig =>
    {
        await PairAsync(rig);

        // The link must come up and STAY up. It previously died on the first snapshot, because the
        // snapshot's IsConnected (the desktop's own Copilot session) was assigned straight onto the
        // shell's IsConnected (the phone-to-desktop link), so the phone showed itself as offline.
        await WaitAsync(() => rig.Shell.IsConnected, "the phone's link to report connected");

        // Force the exact overwrite that used to break it, with the desktop reporting NOT ready.
        rig.Main.IsConnected = false;
        await rig.Shell.RefreshSnapshotAsync();
        await WaitAsync(() => !rig.Shell.IsHostReady, "the desktop's own state to reach the phone");

        Assert.True(rig.Shell.IsConnected, "a not-ready desktop must not tear down the phone's link");
        Assert.False(rig.Shell.IsLive, "IsLive must require both the link and a ready desktop");

        rig.Main.IsConnected = true;
        await rig.Shell.RefreshSnapshotAsync();
        await WaitAsync(() => rig.Shell.IsLive, "the phone to go live once the desktop is ready");
    });

    private static async Task SendAndAbandonTrackedCommandAsync(Rig rig, RemoteCommand command)
    {
        Assert.NotNull(rig.Client.Token);
        Assert.NotNull(command.RequestId);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            rig.BaseUrl + RemoteProtocol.Routes.Command)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(command, RemoteJsonContext.Default.RemoteCommand),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation(RemoteProtocol.DeviceTokenHeader, rig.Client.Token);
        request.Headers.TryAddWithoutValidation(RemoteProtocol.DeviceIdHeader, rig.Client.DeviceId);
        using var deadline = new CancellationTokenSource();
        var responseTask = http.SendAsync(request, deadline.Token);

        Assert.True(
            SpinWait.SpinUntil(
                () => rig.Server.HasTrackedCommandRequest(rig.Client.DeviceId, command.RequestId),
                TimeSpan.FromSeconds(3)),
            "The server did not register the command before the simulated client deadline.");

        // Keep the UI thread blocked until the finite request deadline wins. The server command is
        // already registered but cannot execute until this method yields back to the dispatcher.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(50));
        Thread.Sleep(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var response = await responseTask;
        });
    }

    private static T GetPrivateField<T>(object target, string name) where T : class =>
        Assert.IsType<T>(
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));
}
