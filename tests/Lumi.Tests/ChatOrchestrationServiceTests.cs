using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
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

    // Creates a throwaway git repo with one commit so `git worktree add -b …` has a HEAD to branch from.
    private static string CreateTempGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Lumi-orch-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        RunGit(dir, "init -b main");
        RunGit(dir, "config user.email test@lumi.local");
        RunGit(dir, "config user.name \"Lumi Test\"");
        File.WriteAllText(Path.Combine(dir, "README.md"), "seed");
        RunGit(dir, "add -A");
        RunGit(dir, "commit -m seed");
        return dir;
    }

    private static void RunGit(string workingDir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            // git marks pack/object files read-only, which blocks Directory.Delete on Windows —
            // clear attributes first so the throwaway repo doesn't linger in TEMP after the test.
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { /* best-effort */ }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort test cleanup */ }
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
        string? reasoningEffort = null,
        bool? worktree = null,
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
                    reasoningEffort, worktree, wait, timeoutSeconds, maxMessages, query, limit, sourceChatId,
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
    public async Task List_PutsPinnedChatsFirstAndReportsPinnedState()
    {
        using var session = HeadlessTestSession.Start();
        var pinned = new Chat
        {
            Title = "Pinned reference",
            IsPinned = true,
            UpdatedAt = DateTimeOffset.Now.AddDays(-5)
        };
        var recent = new Chat
        {
            Title = "Recent work",
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-1)
        };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(recent, pinned));
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            var result = await RunAsync(session, service, "list");

            Assert.True(result.IndexOf("Pinned reference", StringComparison.Ordinal)
                < result.IndexOf("Recent work", StringComparison.Ordinal));
            Assert.Contains("pinned", result);
        }
    }

    [Fact]
    public async Task PinAndUnpin_UpdateChatAndRaiseChatsChanged()
    {
        using var session = HeadlessTestSession.Start();
        var chat = new Chat { Title = "Priority chat" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(chat));
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;
            var changes = 0;
            service.ChatsChanged += () => Interlocked.Increment(ref changes);
            var pinResult = "";
            var duplicateResult = "";
            var unpinResult = "";
            Exception? failure = null;

            await session.Dispatch(async () =>
            {
                try
                {
                    pinResult = await service.ManageChatsAsync(
                        "pin",
                        identifier: chat.Id.ToString(),
                        cancellationToken: CancellationToken.None);
                    duplicateResult = await service.ManageChatsAsync(
                        "pin",
                        identifier: chat.Id.ToString(),
                        cancellationToken: CancellationToken.None);
                    unpinResult = await service.ManageChatsAsync(
                        "unpin",
                        identifier: chat.Id.ToString(),
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, CancellationToken.None);

            Assert.Null(failure);
            Assert.False(chat.IsPinned);
            Assert.Contains("Pinned chat", pinResult);
            Assert.Contains("already pinned", duplicateResult);
            Assert.Contains("Unpinned chat", unpinResult);
            Assert.Equal(2, changes);
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
    public async Task Create_AppliesModelAndReasoningEffortOverrides()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        store.Data.Settings.PreferredModel = "default-model";
        store.Data.Settings.ReasoningEffort = "medium";
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            await RunAsync(session, service, "create", title: "Tuned", model: "gpt-5.5", reasoningEffort: "high");

            var created = Assert.Single(store.Data.Chats);
            Assert.Equal("gpt-5.5", created.LastModelUsed);
            Assert.Equal("high", created.LastReasoningEffortUsed);
        }
    }

    [Fact]
    public async Task Create_DefaultsModelAndEffortToSettings()
    {
        using var session = HeadlessTestSession.Start();
        var (store, registry, sessionStore) = CreateEnvironment(CreateData());
        store.Data.Settings.PreferredModel = "default-model";
        store.Data.Settings.ReasoningEffort = "medium";
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore))
        {
            sessionStore.OrchestrationService = service;

            await RunAsync(session, service, "create", title: "Plain");

            var created = Assert.Single(store.Data.Chats);
            Assert.Equal("default-model", created.LastModelUsed);
            Assert.Equal("medium", created.LastReasoningEffortUsed);
        }
    }

    [Fact]
    public async Task Create_WorktreeRequested_NonGitProject_FallsBackWithNote()
    {
        using var session = HeadlessTestSession.Start();
        var tempDir = Path.Combine(Path.GetTempPath(), "Lumi-orch-nogit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var project = new Project { Name = "Docs", WorkingDirectory = tempDir };
            var data = CreateData();
            data.Projects.Add(project);
            var (store, registry, sessionStore) = CreateEnvironment(data);
            using (registry)
            using (sessionStore)
            using (var service = new ChatOrchestrationService(store, registry, sessionStore))
            {
                sessionStore.OrchestrationService = service;

                var result = await RunAsync(session, service, "create", title: "Notes", project: "Docs", worktree: true);

                var created = Assert.Single(store.Data.Chats);
                Assert.Null(created.WorktreePath);
                Assert.Contains("not a git repository", result);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Create_WorktreeRequested_GitRepo_CreatesWorktree()
    {
        // Driven directly against the worktree helper: routing a real `git worktree add` through the full
        // headless ManageChatsAsync + UI dispatch is unreliable because the subprocess await hops off the
        // dispatcher thread before the create returns. The end-to-end wiring (create passes worktree=true +
        // the project dir into this helper) is covered by the synchronous non-git / opt-out tests below.
        var repoDir = CreateTempGitRepo();
        string? worktreePath = null;
        try
        {
            var chat = new Chat { Title = "Feature" };

            var note = await ChatOrchestrationService.MaybeCreateWorktreeAsync(chat, requested: true, repoDir);

            worktreePath = chat.WorktreePath;
            Assert.False(string.IsNullOrWhiteSpace(chat.WorktreePath), $"repo={repoDir} | note={note}");
            Assert.True(Directory.Exists(chat.WorktreePath));
            Assert.NotNull(note);
            Assert.Contains("worktree", note, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (worktreePath is not null)
                await GitService.RemoveWorktreeAsync(repoDir, worktreePath);
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task MaybeCreateWorktree_NotRequested_ReturnsNullAndLeavesPathUnset()
    {
        var chat = new Chat { Title = "Plain" };

        var note = await ChatOrchestrationService.MaybeCreateWorktreeAsync(chat, requested: false, projectDir: null);

        Assert.Null(note);
        Assert.Null(chat.WorktreePath);
    }

    [Fact]
    public async Task Create_WorktreeNotRequested_UsesProjectFolder()
    {
        using var session = HeadlessTestSession.Start();
        var repoDir = CreateTempGitRepo();
        try
        {
            var project = new Project { Name = "Code", WorkingDirectory = repoDir };
            var data = CreateData();
            data.Projects.Add(project);
            var (store, registry, sessionStore) = CreateEnvironment(data);
            using (registry)
            using (sessionStore)
            using (var service = new ChatOrchestrationService(store, registry, sessionStore))
            {
                sessionStore.OrchestrationService = service;

                await RunAsync(session, service, "create", title: "Plain", project: "Code");

                var created = Assert.Single(store.Data.Chats);
                Assert.Null(created.WorktreePath);
            }
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task Create_UsesProjectWorktreeDefault_WhenNotExplicitlySpecified()
    {
        using var session = HeadlessTestSession.Start();
        var tempDir = Path.Combine(Path.GetTempPath(), "Lumi-orch-default-wt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var project = new Project
            {
                Name = "Code",
                WorkingDirectory = tempDir,
                DefaultNewChatsUseWorktree = true
            };
            var data = CreateData();
            data.Projects.Add(project);
            var (store, registry, sessionStore) = CreateEnvironment(data);
            using (registry)
            using (sessionStore)
            using (var service = new ChatOrchestrationService(store, registry, sessionStore))
            {
                sessionStore.OrchestrationService = service;

                var result = await RunAsync(session, service, "create", title: "Default worktree", project: "Code");

                Assert.Contains("worktree was requested", result, StringComparison.OrdinalIgnoreCase);
                Assert.Null(Assert.Single(store.Data.Chats).WorktreePath);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Create_ExplicitLocalOverride_WinsOverProjectWorktreeDefault()
    {
        using var session = HeadlessTestSession.Start();
        var tempDir = Path.Combine(Path.GetTempPath(), "Lumi-orch-local-override-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var project = new Project
            {
                Name = "Code",
                WorkingDirectory = tempDir,
                DefaultNewChatsUseWorktree = true
            };
            var data = CreateData();
            data.Projects.Add(project);
            var (store, registry, sessionStore) = CreateEnvironment(data);
            using (registry)
            using (sessionStore)
            using (var service = new ChatOrchestrationService(store, registry, sessionStore))
            {
                sessionStore.OrchestrationService = service;

                var result = await RunAsync(
                    session,
                    service,
                    "create",
                    title: "Local override",
                    project: "Code",
                    worktree: false);

                Assert.DoesNotContain("worktree was requested", result, StringComparison.OrdinalIgnoreCase);
                Assert.Null(Assert.Single(store.Data.Chats).WorktreePath);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
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
        Func<ChatViewModel, Chat, string, string, string?, string?, CancellationToken, Task> completingSend =
            (_, _, _, _, _, _, _) => { Interlocked.Increment(ref sends); return Task.CompletedTask; };

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
    public async Task Send_ForwardsModelAndEffortOverrideToSendPath()
    {
        using var session = HeadlessTestSession.Start();
        var target = new Chat { Title = "Worker" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(target));

        string? capturedModel = null;
        string? capturedEffort = null;
        var captured = 0;
        Func<ChatViewModel, Chat, string, string, string?, string?, CancellationToken, Task> capturingSend =
            (_, _, _, _, model, effort, _) =>
            {
                capturedModel = model;
                capturedEffort = effort;
                Interlocked.Increment(ref captured);
                return Task.CompletedTask;
            };

        Exception? failure = null;
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore, sendOverride: capturingSend))
        {
            sessionStore.OrchestrationService = service;
            await session.Dispatch(async () =>
            {
                try
                {
                    await service.ManageChatsAsync(
                        "send", identifier: target.Id.ToString(), message: "go",
                        model: "gpt-5.5", reasoningEffort: "high");
                    await service.WaitForRunsAsync();
                }
                catch (Exception ex) { failure = ex; }
            }, CancellationToken.None);
        }

        Assert.Null(failure);
        Assert.Equal(1, captured);
        Assert.Equal("gpt-5.5", capturedModel);
        Assert.Equal("high", capturedEffort);
    }

    [Fact]
    public async Task Send_WithoutOverride_ForwardsNullModelAndEffort()
    {
        using var session = HeadlessTestSession.Start();
        var target = new Chat { Title = "Worker" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(target));

        string? capturedModel = "sentinel";
        string? capturedEffort = "sentinel";
        Func<ChatViewModel, Chat, string, string, string?, string?, CancellationToken, Task> capturingSend =
            (_, _, _, _, model, effort, _) =>
            {
                capturedModel = model;
                capturedEffort = effort;
                return Task.CompletedTask;
            };

        Exception? failure = null;
        using (registry)
        using (sessionStore)
        using (var service = new ChatOrchestrationService(store, registry, sessionStore, sendOverride: capturingSend))
        {
            sessionStore.OrchestrationService = service;
            await session.Dispatch(async () =>
            {
                try
                {
                    await service.ManageChatsAsync("send", identifier: target.Id.ToString(), message: "go");
                    await service.WaitForRunsAsync();
                }
                catch (Exception ex) { failure = ex; }
            }, CancellationToken.None);
        }

        Assert.Null(failure);
        Assert.Null(capturedModel);
        Assert.Null(capturedEffort);
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
        Func<ChatViewModel, Chat, string, string, string?, string?, CancellationToken, Task> failingSend =
            (_, _, _, _, _, _, _) => { Interlocked.Increment(ref sends); return Task.FromException(new InvalidOperationException("boom")); };

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

    [Fact]
    public async Task AcquireChat_WithoutConfigure_SeedsModelCatalogFromPopulatedSurface()
    {
        // Regression guard for the effort-override drop on orchestrated sends. Orchestration executors
        // are resolved via AcquireChatAsync with NO configure callback (unlike MainViewModel, which always
        // passes PrepareChatSurface -> CopyModelCatalogFrom). Without seeding, such surfaces start with an
        // empty reasoning-effort catalog, so ModelSelectionHelper.NormalizeEffort() returns null and a
        // per-send reasoning-effort override (manage_chats) is silently dropped even though the model is
        // preserved. CreateTrackedSurface must copy the catalog from an already-populated surface.
        using var session = HeadlessTestSession.Start();
        var seedChat = new Chat { Title = "Seed" };
        var targetChat = new Chat { Title = "Target" };
        var (store, registry, sessionStore) = CreateEnvironment(CreateData(seedChat, targetChat));

        string[]? seedModels = null;
        string[]? acquiredModels = null;
        string[]? acquiredQualityLevels = null;
        Exception? failure = null;

        using (registry)
        using (sessionStore)
        {
            await session.Dispatch(async () =>
            {
                try
                {
                    // Populate a first tracked surface's catalog (mirrors the primary window's _chatVM).
                    var seed = await sessionStore.AcquireChatAsync(seedChat);
                    seed.UpdateModelCapabilities([CreateModelWithEfforts("test-model")]);
                    seed.AvailableModels.Add("test-model");
                    seedModels = seed.AvailableModels.ToArray();

                    // Orchestration path: acquire WITHOUT a configure callback.
                    var acquired = await sessionStore.AcquireChatAsync(targetChat);
                    acquiredModels = acquired.AvailableModels.ToArray();

                    // Setting SelectedModel recomputes QualityLevels from the (copied) effort catalog.
                    // An empty catalog would yield no quality levels -> the effort-override bug.
                    acquired.SelectedModel = "test-model";
                    acquiredQualityLevels = acquired.QualityLevels;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, CancellationToken.None);
        }

        Assert.Null(failure);
        Assert.NotNull(seedModels);
        Assert.NotNull(acquiredModels);
        Assert.Contains("test-model", acquiredModels!);
        Assert.Equal(seedModels, acquiredModels);
        Assert.NotNull(acquiredQualityLevels);
        Assert.Equal(3, acquiredQualityLevels!.Length);
    }

    private static ModelInfo CreateModelWithEfforts(string id)
        => new()
        {
            Id = id,
            Name = id,
            SupportedReasoningEfforts = ["low", "medium", "high"],
            DefaultReasoningEffort = "high",
            Capabilities = new ModelCapabilities
            {
                Limits = new ModelLimits { MaxContextWindowTokens = 128_000 },
                Supports = new ModelSupports()
            }
        };
}
