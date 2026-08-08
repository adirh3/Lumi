using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class RemoteCommandRouterSurfaceTests
{
    [Fact]
    public Task SendUsesTheDetachedChatOwnerWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        ChatViewModel? startedOn = null;
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (owner, _, _, _, _, _) =>
            {
                startedOn = owner;
                return Task.FromResult<string?>(null);
            });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "send from phone"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Same(rig.DetachedSurface, startedOn);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task SendWithAnUnknownExplicitChatIdNeverFallsBackToTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var started = false;
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (_, _, _, _, _, _) =>
            {
                started = true;
                return Task.FromResult<string?>(null);
            });

        var chatCount = rig.DataStore.Data.Chats.Count;

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", Guid.NewGuid().ToString())
                .With("message", "must not be redirected"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(started);
        Assert.Equal(chatCount, rig.DataStore.Data.Chats.Count);
        Assert.DoesNotContain(rig.MainChat.Messages, message => message.Content == "must not be redirected");
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Theory]
    [InlineData(RemoteProtocol.Actions.DeleteChat)]
    [InlineData(RemoteProtocol.Actions.RenameChat)]
    [InlineData(RemoteProtocol.Actions.PinChat)]
    public Task MutatingChatActionsRequireAnExplicitChatId(string action) => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);
        var originalTitle = rig.MainChat.Title;
        var originalPinned = rig.MainChat.IsPinned;

        var result = await router.ExecuteAsync(
            new RemoteCommand(action)
                .With("title", "must not apply")
                .With("pinned", (!originalPinned).ToString()),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("chatId is required.", result.Error);
        Assert.Contains(rig.MainChat, rig.DataStore.Data.Chats);
        Assert.Equal(originalTitle, rig.MainChat.Title);
        Assert.Equal(originalPinned, rig.MainChat.IsPinned);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task LegacyOpenChatValidatesWithoutActivatingTheDesktopSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.OpenChat)
                .With("chatId", rig.DetachedChat.Id.ToString()),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
        Assert.NotSame(rig.DetachedChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task SteerUsesTheDetachedRuntimeWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        var router = new RemoteCommandRouter(
            rig.DataStore,
            rig.Main,
            (_, _, _, _, _, _) => throw new InvalidOperationException("A busy send must steer."));

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "change direction")
                .With("steer", "true"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(
            rig.DetachedChat.Messages,
            message => message.Content == "change direction" && message.Author == "Lumi Mobile");
        Assert.DoesNotContain(rig.MainChat.Messages, message => message.Content == "change direction");
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task StopUsesTheDetachedRuntimeWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var runtime = MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        rig.DetachedSurface.IsBusy = true;
        rig.DetachedSurface.IsStreaming = true;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                .With("chatId", rig.DetachedChat.Id.ToString()),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.False(runtime.IsBusy);
        Assert.False(rig.DetachedSurface.IsBusy);
        Assert.False(rig.DetachedSurface.IsStreaming);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task ConfigureUsesTheDetachedSurfaceWithoutChangingTheMainSurface() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.Main.ChatVM.SelectedModel = "main-model";
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("model", "detached-model")
                .With("project", DetachedRig.ProjectName),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("detached-model", rig.DetachedChat.LastModelUsed);
        Assert.Equal(rig.ProjectId, rig.DetachedChat.ProjectId);
        Assert.Equal("main-model", rig.MainChat.LastModelUsed);
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task SendNeverAppliesConfigurationToALiveOwnerDisplayingAnotherChat() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        MarkBusy(rig.DetachedSurface, rig.DetachedChat);
        rig.DetachedSurface.CurrentChat = rig.MainChat;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("message", "must not mutate the displayed chat")
                .With("model", "phone-model"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("initial-model", rig.MainChat.LastModelUsed);
        Assert.Equal("initial-model", rig.DetachedChat.LastModelUsed);
    });

    [Fact]
    public Task UnknownProjectDoesNotClearAnExistingChatOrCreateANewChat() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.DetachedChat.ProjectId = rig.ProjectId;
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);
        var chatCount = rig.DataStore.Data.Chats.Count;

        var configure = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.ConfigureChat)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("project", "Deleted project"),
            CancellationToken.None);
        var send = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("newChat", "true")
                .With("message", "do not create")
                .With("project", "Deleted project"),
            CancellationToken.None);

        Assert.False(configure.Ok);
        Assert.False(send.Ok);
        Assert.Equal(rig.ProjectId, rig.DetachedChat.ProjectId);
        Assert.Equal(chatCount, rig.DataStore.Data.Chats.Count);
    });

    [Fact]
    public Task ProjectFeatureRefreshInvalidatesOnlyAffectedBusyChats() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync(includeProject: true);
        rig.DetachedChat.ProjectId = rig.ProjectId;
        MarkBusy(rig.Main.ChatVM, rig.MainChat);
        MarkBusy(rig.DetachedSurface, rig.DetachedChat);

        await rig.Main.ApplyFeatureChangeAsync(
            new FeatureChangeResult("Project updated.", DataChanged: true),
            RemoteProtocol.Resources.Projects,
            new HashSet<Guid> { rig.DetachedChat.Id });

        var mainPending = GetPrivateField<HashSet<Guid>>(rig.Main.ChatVM, "_pendingSessionInvalidations");
        var detachedPending = GetPrivateField<HashSet<Guid>>(rig.DetachedSurface, "_pendingSessionInvalidations");
        Assert.DoesNotContain(rig.MainChat.Id, mainPending);
        Assert.Contains(rig.DetachedChat.Id, detachedPending);
    });

    [Fact]
    public Task McpFeatureRefreshPreservesExplicitPerChatSelection() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        rig.DataStore.Data.McpServers.AddRange(
        [
            new McpServer { Name = "Selected MCP", IsEnabled = true },
            new McpServer { Name = "Excluded MCP", IsEnabled = true }
        ]);
        rig.DetachedChat.ActiveMcpServerNames = ["Selected MCP"];
        rig.DetachedChat.HasExplicitMcpServerSelection = true;
        rig.DetachedSurface.ActiveMcpServerNames.Clear();
        rig.DetachedSurface.ActiveMcpServerNames.Add("Selected MCP");

        await rig.Main.ApplyFeatureChangeAsync(
            new FeatureChangeResult("MCP updated.", DataChanged: true),
            RemoteProtocol.Resources.Mcps);

        Assert.Equal(["Selected MCP"], rig.DetachedChat.ActiveMcpServerNames);
        Assert.Equal(["Selected MCP"], rig.DetachedSurface.ActiveMcpServerNames);
    });

    [Fact]
    public Task McpRenameDoesNotAttachTheServerToUnrelatedSurfaces() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        rig.DataStore.Data.McpServers.AddRange(
        [
            new McpServer { Name = "New", IsEnabled = true },
            new McpServer { Name = "Other", IsEnabled = true }
        ]);
        rig.MainChat.ActiveMcpServerNames = ["Other"];
        rig.Main.ChatVM.ActiveMcpServerNames.Clear();
        rig.Main.ChatVM.ActiveMcpServerNames.Add("Other");
        rig.DetachedChat.ActiveMcpServerNames = ["Old"];
        rig.DetachedSurface.ActiveMcpServerNames.Clear();
        rig.DetachedSurface.ActiveMcpServerNames.Add("Old");

        await rig.Main.ApplyFeatureChangeAsync(
            new FeatureChangeResult(
                "MCP renamed.",
                DataChanged: true,
                RenamedMcpOldName: "Old",
                RenamedMcpNewName: "New"),
            RemoteProtocol.Resources.Mcps);

        Assert.Equal(["Other"], rig.Main.ChatVM.ActiveMcpServerNames);
        Assert.Equal(["Other"], rig.MainChat.ActiveMcpServerNames);
        Assert.Equal(["New"], rig.DetachedSurface.ActiveMcpServerNames);
        Assert.Equal(["New"], rig.DetachedChat.ActiveMcpServerNames);
    });

    [Fact]
    public Task AnswerQuestionUsesTheDetachedPendingQuestionOwner() => RunAsync(async () =>
    {
        using var rig = await DetachedRig.CreateAsync();
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        GetPrivateField<Dictionary<string, TaskCompletionSource<string>>>(
            rig.DetachedSurface,
            "_pendingQuestions").Add("question-1", answer);
        var router = new RemoteCommandRouter(rig.DataStore, rig.Main);

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.AnswerQuestion)
                .With("chatId", rig.DetachedChat.Id.ToString())
                .With("questionId", "question-1")
                .With("answer", "Detached answer"),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Detached answer", await answer.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(rig.MainChat, rig.Main.ChatVM.CurrentChat);
    });

    [Fact]
    public Task FirstTurnAppliesAllConfigurationBeforeStartingAndReturnsCreatedChatIdOnFailure() =>
        RunAsync(async () =>
        {
            Loc.Load("en");
            const string modelId = "remote-first-turn-model";
            var project = new Project { Name = "Remote project" };
            var skill = new Skill { Name = "Remote skill", Content = "Use the skill." };
            var agent = new LumiAgent { Name = "Remote agent", SystemPrompt = "Be focused." };
            var mcp = new McpServer { Name = "remote-mcp", IsEnabled = true };
            var data = new AppData
            {
                Settings = TestSettings(),
                Projects = [project],
                Skills = [skill],
                Agents = [agent],
                McpServers = [mcp]
            };
            var dataStore = new DataStore(data);
            using var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService());
            WaitForDesktopInitialization(main);
            main.ChatVM.UpdateModelCapabilities(
            [
                new ModelInfo
                {
                    Id = modelId,
                    SupportedReasoningEfforts = ["low", "medium", "high"],
                    DefaultReasoningEffort = "medium"
                }
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modelId });
            main.ChatVM.ApplyAvailableModels([modelId], modelId);

            FirstTurnSnapshot? snapshot = null;
            var router = new RemoteCommandRouter(
                dataStore,
                main,
                (owner, chat, _, _, _, _) =>
                {
                    snapshot = new FirstTurnSnapshot(
                        ReferenceEquals(owner, main.ChatVM),
                        owner.ResolveSelectedModelForChat(chat),
                        owner.ResolvePersistedReasoningEffortForChat(
                            chat,
                            owner.ResolveSelectedModelForChat(chat)),
                        owner.ResolveSelectedContextWindowTierForChat(
                            chat,
                            owner.ResolveSelectedModelForChat(chat)),
                        chat.ProjectId,
                        chat.AgentId,
                        [.. owner.ActiveSkillIds],
                        [.. owner.ActiveMcpServerNames]);
                    return Task.FromResult<string?>("Synthetic post-creation failure.");
                });

            var result = await router.ExecuteAsync(
                new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    .With("newChat", "true")
                    .With("message", "Start configured")
                    .With("model", modelId)
                    .With("quality", "high")
                    .With("contextWindowTier", "Long")
                    .With("agent", agent.Name)
                    .With("project", project.Name)
                    .WithList("addSkills", [skill.Name])
                    .WithList("addMcps", [mcp.Name]),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("Synthetic post-creation failure.", result.Error);
            Assert.NotNull(result.ChatId);
            Assert.Equal(result.ChatId, Assert.Single(data.Chats).Id);
            Assert.NotNull(snapshot);
            Assert.True(snapshot.OnMainOwner);
            Assert.Equal(modelId, snapshot.Model);
            Assert.Equal("high", snapshot.ReasoningEffort);
            Assert.Equal(ModelContextWindowTiers.LongContext, snapshot.ContextWindowTier);
            Assert.Equal(project.Id, snapshot.ProjectId);
            Assert.Equal(agent.Id, snapshot.AgentId);
            Assert.Equal([skill.Id], snapshot.SkillIds);
            Assert.Equal([mcp.Name], snapshot.McpNames);
        });

    [Fact]
    public Task FirstTurnSelectsTheRequestedByokRouteBeforeStarting() => RunAsync(async () =>
    {
        Loc.Load("en");
        var endpoint = new ByokEndpoint
        {
            Id = "remote-endpoint",
            Name = "Remote endpoint",
            BaseUrl = "https://api.example.test/v1",
            ProviderType = "openai",
            ApiKeyMode = ByokApiKeyMode.None,
            IsEnabled = true
        };
        var model = new ByokModel
        {
            Id = "remote-model",
            EndpointId = endpoint.Id,
            ModelId = "provider-model",
            DisplayName = "Remote BYOK",
            IsEnabled = true
        };
        var settings = TestSettings();
        settings.PreferredModel = "gpt-5-mini";
        settings.UseBYOKOnly = true;
        settings.ByokEndpoints = [endpoint];
        settings.ByokModels = [model];
        var data = new AppData
        {
            Settings = settings
        };
        var dataStore = new DataStore(data);
        using var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService());
        WaitForDesktopInitialization(main);
        var requestedModel = $"byok:{model.Id}";
        string? selectedAtStart = null;
        var router = new RemoteCommandRouter(
            dataStore,
            main,
            (owner, chat, _, _, _, _) =>
            {
                selectedAtStart = owner.ResolveSelectedModelForChat(chat);
                return Task.FromResult<string?>(null);
            });

        var result = await router.ExecuteAsync(
            new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                .With("newChat", "true")
                .With("message", "Use my provider")
                .With("model", requestedModel),
            CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(requestedModel, selectedAtStart);
        Assert.Equal(requestedModel, Assert.Single(data.Chats).LastModelUsed);
    });

    private static ChatRuntimeState MarkBusy(ChatViewModel surface, Chat chat)
    {
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            TurnInProgress = true,
            IsStreaming = true
        };
        GetPrivateField<Dictionary<Guid, ChatRuntimeState>>(surface, "_runtimeStates")[chat.Id] = runtime;
        return runtime;
    }

    private static UserSettings TestSettings() => new()
    {
        AutoSaveChats = false,
        AutoGenerateTitles = false,
        EnableMemoryAutoSave = false
    };

    private static T GetPrivateField<T>(object target, string name) where T : class =>
        Assert.IsType<T>(
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target));

    private static void WaitForDesktopInitialization(MainViewModel main)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (main.IsConnecting && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Assert.False(main.IsConnecting, "The shared test Copilot service did not finish initializing.");
    }

    private static async Task RunAsync(Func<Task> body)
    {
        using var session = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                var work = body();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (!work.IsCompleted)
                {
                    Dispatcher.UIThread.RunJobs();
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("The remote router test did not complete.");
                    Thread.Sleep(1);
                }

                work.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    private sealed class DetachedRig : IDisposable
    {
        public const string ProjectName = "Detached project";

        private DetachedRig(
            DataStore dataStore,
            MainViewModel main,
            Chat mainChat,
            Chat detachedChat,
            DetachedChatWindowRequest request,
            Guid? projectId)
        {
            DataStore = dataStore;
            Main = main;
            MainChat = mainChat;
            DetachedChat = detachedChat;
            Request = request;
            ProjectId = projectId;
        }

        public DataStore DataStore { get; }
        public MainViewModel Main { get; }
        public Chat MainChat { get; }
        public Chat DetachedChat { get; }
        public DetachedChatWindowRequest Request { get; }
        public ChatViewModel DetachedSurface => Request.WindowVM.ChatVM;
        public Guid? ProjectId { get; }

        public static async Task<DetachedRig> CreateAsync(bool includeProject = false)
        {
            Loc.Load("en");
            var mainChat = ChatWithMessage("Main chat", "main");
            var detachedChat = ChatWithMessage("Detached chat", "detached");
            var data = new AppData
            {
                Settings = TestSettings(),
                Chats = [mainChat, detachedChat]
            };
            Project? project = null;
            if (includeProject)
            {
                project = new Project { Name = ProjectName };
                data.Projects.Add(project);
            }

            var dataStore = new DataStore(data);
            var main = new MainViewModel(dataStore, TestCopilot.Shared, new UpdateService());
            WaitForDesktopInitialization(main);
            main.ChatVM.CurrentChat = mainChat;
            DetachedChatWindowRequest? request = null;
            main.OpenChatWindowRequested += requested => request = requested;
            await main.OpenChatInNewWindowCommand.ExecuteAsync(detachedChat);

            return new DetachedRig(
                dataStore,
                main,
                mainChat,
                detachedChat,
                Assert.IsType<DetachedChatWindowRequest>(request),
                project?.Id);
        }

        public void Dispose()
        {
            Request.WindowVM.Dispose();
            Request.ReleaseSurface();
            Main.Dispose();
        }

        private static Chat ChatWithMessage(string title, string content)
        {
            var chat = new Chat { Title = title, LastModelUsed = "initial-model" };
            chat.Messages.Add(new ChatMessage { Role = "user", Content = content });
            chat.MessageCount = chat.Messages.Count;
            return chat;
        }
    }

    private sealed record FirstTurnSnapshot(
        bool OnMainOwner,
        string? Model,
        string? ReasoningEffort,
        string? ContextWindowTier,
        Guid? ProjectId,
        Guid? AgentId,
        IReadOnlyList<Guid> SkillIds,
        IReadOnlyList<string> McpNames);
}
