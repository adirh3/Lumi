using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatOrchestrationServiceTests
{
    private static AppData CreateData(params Chat[] chats)
    {
        var data = new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        };
        foreach (var chat in chats)
            data.Chats.Add(chat);
        return data;
    }

    private static (DataStore Store, ChatSurfaceRegistry Registry, ChatSessionStore SessionStore) CreateEnvironment(AppData data)
    {
        var store = new DataStore(data);
        var registry = new ChatSurfaceRegistry();
        var sessionStore = new ChatSessionStore(
            store,
            new CopilotService(),
            registry,
            static (surface, chat) =>
            {
                surface.CurrentChat = chat;
                return Task.CompletedTask;
            });
        return (store, registry, sessionStore);
    }

    // Runs an orchestration call on the headless UI thread and surfaces any exception. Avalonia's
    // HeadlessUnitTestSession.Dispatch swallows faults from the async body, so we capture the result
    // (and any error) inside the body and re-throw/assert outside it.
    private static async Task<string> RunAsync(
        HeadlessTestSession session,
        ChatOrchestrationService service,
        string action,
        string? identifier = null,
        string? title = null,
        string? message = null,
        string? project = null,
        string? agent = null,
        string[]? skills = null,
        string? model = null,
        bool? wait = null,
        int? timeoutSeconds = null,
        int? maxMessages = null,
        string? query = null,
        int? limit = null,
        Guid? sourceChatId = null)
    {
        string result = "";
        Exception? failure = null;
        await session.Dispatch(async () =>
        {
            try
            {
                result = await service.ManageChatsAsync(
                    action, identifier, title, message, project, agent, skills, model,
                    wait, timeoutSeconds, maxMessages, query, limit, sourceChatId,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            throw new InvalidOperationException("manage_chats threw on the UI thread.", failure);
        return result;
    }

    [Fact]
    public async Task List_ReportsEveryChatWithIdAndStatus()
    {
        using var session = HeadlessTestSession.Start();
        var alpha = new Chat { Title = "Alpha research", UpdatedAt = DateTimeOffset.Now.AddMinutes(-3) };
        var beta = new Chat { Title = "Beta build", UpdatedAt = DateTimeOffset.Now.AddMinutes(-1) };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(alpha, beta));
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "list");

            Assert.Contains("Alpha research", result);
            Assert.Contains("Beta build", result);
            Assert.Contains(alpha.Id.ToString(), result);
            Assert.Contains(beta.Id.ToString(), result);
            Assert.Contains("status:", result);
        }
    }

    [Fact]
    public async Task List_FiltersByProject()
    {
        using var session = HeadlessTestSession.Start();
        var project = new Project { Name = "Launch" };
        var inProject = new Chat { Title = "Launch planning", ProjectId = project.Id };
        var elsewhere = new Chat { Title = "Unrelated notes" };
        var data = CreateData(inProject, elsewhere);
        data.Projects.Add(project);
        var (store, registry, sessionStore) = CreateEnvironment(data);
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "list", project: "Launch");

            Assert.Contains("Launch planning", result);
            Assert.DoesNotContain("Unrelated notes", result);
        }
    }

    [Fact]
    public async Task Create_AddsChatAndRaisesChatsChanged()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;
            var changes = 0;
            service.ChatsChanged += () => Interlocked.Increment(ref changes);

            var result = await RunAsync(session, service, "create", title: "Kickoff plan");

            var created = Assert.Single(store.Data.Chats);
            Assert.Equal("Kickoff plan", created.Title);
            Assert.Contains("Created chat", result);
            Assert.Contains("No message was sent yet", result);
            Assert.True(changes >= 1);
        }
    }

    [Fact]
    public async Task Create_AssignsProjectAgentAndSkills()
    {
        using var session = HeadlessTestSession.Start();
        var project = new Project { Name = "Ops" };
        var agent = new LumiAgent { Name = "Daily Lumi" };
        var skill = new Skill { Name = "Web Researcher" };
        var data = CreateData();
        data.Projects.Add(project);
        data.Agents.Add(agent);
        data.Skills.Add(skill);
        var (store, registry, sessionStore) = CreateEnvironment(data);
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(
                session, service, "create",
                title: "Morning digest",
                project: "Ops",
                agent: "Daily Lumi",
                skills: ["Web Researcher"]);

            var created = Assert.Single(store.Data.Chats);
            Assert.Equal(project.Id, created.ProjectId);
            Assert.Equal(agent.Id, created.AgentId);
            Assert.Contains(skill.Id, created.ActiveSkillIds);
            Assert.Contains("Ops", result);
            Assert.Contains("Daily Lumi", result);
        }
    }

    [Fact]
    public async Task Create_WithUnknownProject_ReturnsErrorAndAddsNothing()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "create", title: "Nope", project: "Ghost");

            Assert.Contains("No project matches", result);
            Assert.Empty(store.Data.Chats);
        }
    }

    [Fact]
    public async Task Send_WithoutMessage_ReturnsValidationError()
    {
        using var session = HeadlessTestSession.Start();
        var target = new Chat { Title = "Worker" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(target));
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "send", identifier: target.Id.ToString());

            Assert.Contains("message is required", result);
        }
    }

    [Fact]
    public async Task Send_ToOwnChat_IsRejected()
    {
        using var session = HeadlessTestSession.Start();
        var self = new Chat { Title = "Manager chat" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(self));
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(
                session, service, "send",
                identifier: self.Id.ToString(),
                message: "loop to myself",
                sourceChatId: self.Id);

            Assert.Contains("own chat", result);
        }
    }

    [Fact]
    public async Task Status_UnknownChat_ReturnsNotFound()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "status", identifier: Guid.NewGuid().ToString());

            Assert.Contains("No chat found", result);
        }
    }

    [Fact]
    public async Task Status_ReportsCountsAndLatestReply()
    {
        using var session = HeadlessTestSession.Start();
        var chat = new Chat { Title = "Investigation" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Look into the flake." });
        chat.Messages.Add(new ChatMessage { Role = "tool", Content = "", ToolName = "powershell", ToolStatus = "Completed" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "Root cause is a race in the scheduler." });
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(chat));
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "status", identifier: chat.Id.ToString());

            Assert.Contains("Latest Lumi reply", result);
            Assert.Contains("Root cause is a race", result);
            Assert.Contains("Recent tool activity", result);
            Assert.Contains("Messages: 3", result);
        }
    }

    [Fact]
    public async Task Send_FireAndForget_ReleasesBusyStateAfterRunCompletes()
    {
        using var session = HeadlessTestSession.Start();
        var target = new Chat { Title = "Steady worker" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(target));

        var sends = 0;
        Func<ChatViewModel, Chat, string, string, CancellationToken, Task> completingSend =
            (_, _, _, _, _) => { Interlocked.Increment(ref sends); return Task.CompletedTask; };

        var firstBusyRejected = false;
        var secondBusyRejected = false;
        Exception? failure = null;

        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore, sendOverride: completingSend))
        {
            sessionStore.OrchestrationService = service;

            // Drive two sequential sends + drains inside a single UI-thread dispatch so the run's post-yield
            // continuation is pumped deterministically (awaiting WaitForRunsAsync yields to the dispatcher).
            await session.Dispatch(async () =>
            {
                try
                {
                    var first = await service.ManageChatsAsync("send", identifier: target.Id.ToString(), message: "one");
                    firstBusyRejected = first.Contains("already running", StringComparison.Ordinal);
                    await service.WaitForRunsAsync();

                    var second = await service.ManageChatsAsync("send", identifier: target.Id.ToString(), message: "two");
                    secondBusyRejected = second.Contains("already running", StringComparison.Ordinal);
                    await service.WaitForRunsAsync();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, CancellationToken.None);
        }

        Assert.Null(failure);
        Assert.False(firstBusyRejected);
        // Under the _runs ordering bug a synchronously-completing run leaked a stale busy entry, so the
        // second send would be wrongly rejected as "already running".
        Assert.False(secondBusyRejected);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task Send_WhenRunFailsSynchronously_DoesNotWedgeChatBusy()
    {
        using var session = HeadlessTestSession.Start();
        var target = new Chat { Title = "Flaky worker" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(target));

        // A send that faults synchronously (an already-faulted task) reproduces the exact shape that used to
        // leak a permanent _runs entry: SendExternalMessageAsync throwing at its own busy-guard before its
        // first await, so RunCoreAsync's finally ran (removing nothing) before StartRun recorded the run.
        var sends = 0;
        Func<ChatViewModel, Chat, string, string, CancellationToken, Task> failingSend =
            (_, _, _, _, _) => { Interlocked.Increment(ref sends); return Task.FromException(new InvalidOperationException("boom")); };

        var secondBusyRejected = false;
        Exception? failure = null;

        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore, sendOverride: failingSend))
        {
            sessionStore.OrchestrationService = service;

            await session.Dispatch(async () =>
            {
                try
                {
                    _ = await service.ManageChatsAsync("send", identifier: target.Id.ToString(), message: "go");
                    await service.WaitForRunsAsync();

                    // A synchronously-failing run must still leave the chat sendable — a second send is NOT
                    // rejected as "already running" (which would prove a stale busy entry wedged it forever).
                    var second = await service.ManageChatsAsync("send", identifier: target.Id.ToString(), message: "again");
                    secondBusyRejected = second.Contains("already running", StringComparison.Ordinal);
                    await service.WaitForRunsAsync();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, CancellationToken.None);
        }

        Assert.Null(failure);
        Assert.False(secondBusyRejected);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task UnknownAction_ReturnsGuidance()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "frobnicate");

            Assert.Contains("Unknown manage_chats action", result);
        }
    }

    [Fact]
    public async Task Create_InvokesOnChatLinkedWithCreatedChat()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            (Guid Id, string Title)? captured = null;
            Exception? failure = null;
            await session.Dispatch(async () =>
            {
                try
                {
                    await service.ManageChatsAsync(
                        "create",
                        title: "Linky",
                        onChatLinked: (id, title) => captured = (id, title),
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, CancellationToken.None);

            Assert.Null(failure);
            var created = Assert.Single(store.Data.Chats);
            Assert.NotNull(captured);
            Assert.Equal(created.Id, captured!.Value.Id);
            Assert.Equal("Linky", captured.Value.Title);
        }
    }
}
