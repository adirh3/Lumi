using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

#pragma warning disable GHCP001 // Exercise the task metadata supplied by the SDK.

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class SessionActivityItemsTests
{
    [Fact]
    public void Snapshot_ShowsRunningCommandsAgentsAndTasks_NotFinishedOrIdleItems()
    {
        Loc.Load("en");
        var now = DateTimeOffset.UtcNow;
        var items = ChatViewModel.BuildBackgroundActivityItems(
        [
            Shell("preview", now.AddMinutes(-2)),
            new TaskInfoAgent
            {
                Id = "reviewer", AgentType = "code-review", DisplayName = "Code reviewer",
                Description = "Review session cleanup", Prompt = "", ToolCallId = "review-tool",
                StartedAt = now.AddHours(-1), ActiveStartedAt = now.AddSeconds(-90),
                Status = GitHub.Copilot.Rpc.TaskStatus.Running
            },
            new TaskInfoClient
            {
                Id = "watcher", ClientTaskId = "watcher-owned", DisplayName = "File watcher",
                Description = "Waiting for workspace changes", ActiveTimeMs = 0,
                CanCancel = true, ExecutionMode = new TaskClientExecutionMode("background"),
                Owner = new TaskClientOwner(), Sequence = 1, StartedAt = now.AddMinutes(-5),
                UpdatedAt = now, Status = TaskClientStatus.Running
            },
            Shell("finished", now, status: "completed"),
            Shell("idle", now, status: "idle"),
            Shell("foreground", now, mode: "sync")
        ], now);

        Assert.Equal(["preview", "reviewer", "watcher"], items.Select(item => item.Id));

        // A command reads as what it is for, with the command line beneath.
        Assert.Equal(SessionActivityKind.Command, items[0].Kind);
        Assert.Equal("Local app preview", items[0].Title);
        Assert.Equal("Command", items[0].Owner);
        Assert.Equal("dotnet run", items[0].Detail);
        Assert.Equal(">_", items[0].Initial);
        Assert.Equal(now.AddMinutes(-2), items[0].StartedAt);

        // An agent leads with its task and names who is doing it, timed from its active period.
        Assert.Equal(SessionActivityKind.Agent, items[1].Kind);
        Assert.Equal("Review session cleanup", items[1].Title);
        Assert.Equal("Code reviewer", items[1].Owner);
        Assert.Equal("C", items[1].Initial);
        Assert.Equal(SubagentToolCallItem.AccentIndexFor("Code reviewer"), items[1].AccentIndex);
        Assert.Equal("review-tool", items[1].ToolCallId);
        Assert.Equal(now.AddSeconds(-90), items[1].StartedAt);

        Assert.Equal(SessionActivityKind.Task, items[2].Kind);
        Assert.Equal("File watcher", items[2].Title);
        Assert.Equal("Background task", items[2].Owner);
        Assert.Equal("Waiting for workspace changes", items[2].Detail);
        Assert.Equal("F", items[2].Initial);
    }

    [Fact]
    public void Snapshot_UsesTheCommandWhenDescriptionIsBlank_AndClampsClockSkew()
    {
        var now = DateTimeOffset.UtcNow;
        var shell = Shell("preview", now.AddSeconds(5));
        shell.Description = " ";
        var item = Assert.Single(ChatViewModel.BuildBackgroundActivityItems([shell], now));

        Assert.Equal("dotnet run", item.Title);
        Assert.Empty(item.Detail);
        Assert.Equal(now, item.StartedAt);
    }

    [Fact]
    public void Snapshot_PatchesRowsInPlace_KeepingAnOpenRowAndDroppingFinishedTasks()
    {
        var chat = new Chat { IsSessionActive = true };
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
            Chats = [chat]
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
        var now = DateTimeOffset.UtcNow;
        vm.ApplyBackgroundActivitySnapshot([Shell("server", now), Shell("build", now)], now);
        var list = vm.RunningSessionActivities;
        var server = list[0];
        var build = list[1];
        server.ApplyProgress(new TaskProgressShell { RecentOutput = "Listening on http://localhost:5000" });
        server.ActivateCommand.Execute(null);
        Assert.True(server.IsExpanded);

        var renamed = Shell("server", now);
        renamed.Description = "Dev server";
        vm.ApplyBackgroundActivitySnapshot([Shell("build", now), renamed], now);

        Assert.Same(list, vm.RunningSessionActivities);
        Assert.Equal([build, server], list);
        Assert.Equal("Dev server", server.Title);
        Assert.True(server.IsExpanded);
        Assert.Equal("Listening on http://localhost:5000", server.Activity);

        vm.ApplyBackgroundActivitySnapshot([renamed], now);
        Assert.Same(server, Assert.Single(list));
    }

    [Fact]
    public void Progress_SaysWhatACommandIsDoing_FromItsNewestOutput()
    {
        var item = new SessionActivityItem("build", SessionActivityKind.Command);
        var output = string.Join("\n", Enumerable.Range(1, 15).Select(line => $"  step {line}"))
            + "\n\n\u001b[32mRestored\u001b[0m 3 projects\r\n"
            + "Downloading 10%\rDownloading 55%\r\n   \n";

        item.ApplyProgress(new TaskProgressShell { RecentOutput = output });

        // The live line is the newest visible line: colours stripped, a progress bar at its last state.
        Assert.Equal("Downloading 55%", item.Activity);
        var lines = item.Output!.Split('\n');
        Assert.Equal(SessionActivityItem.OutputTailLines, lines.Length);
        Assert.Equal("  step 6", lines[0]);
        Assert.Equal(["Restored 3 projects", "Downloading 55%"], lines[^2..]);
        Assert.True(item.CanExpand);
        Assert.True(item.ActivateCommand.CanExecute(null));

        // A failed progress read keeps the last known state rather than blanking the row.
        item.ApplyProgress(null);
        Assert.Equal("Downloading 55%", item.Activity);

        // An open row closes once it has no output left, instead of an empty box it could not close.
        item.ActivateCommand.Execute(null);
        Assert.True(item.IsExpanded);
        item.ApplyProgress(new TaskProgressShell { RecentOutput = "  \r\n" });
        Assert.Null(item.Activity);
        Assert.False(item.IsExpanded);
        Assert.False(item.ActivateCommand.CanExecute(null));
    }

    [Fact]
    public void Progress_SaysWhatAnAgentOrTaskIsDoing()
    {
        var agent = new SessionActivityItem("reviewer", SessionActivityKind.Agent);
        agent.ApplyProgress(new TaskProgressAgent
        {
            LatestIntent = "Reviewing session cleanup",
            RecentActivity = [Line("✓ view src/Lumi/ViewModels/ChatViewModel.cs"), Line("▸ powershell")],
        });
        Assert.Equal("Reviewing session cleanup", agent.Activity);
        Assert.Equal("✓ view src/Lumi/ViewModels/ChatViewModel.cs\n▸ powershell", agent.Output);

        agent.ApplyProgress(new TaskProgressAgent { LatestIntent = " ", RecentActivity = [Line("✓ edit README.md")] });
        Assert.Equal("✓ edit README.md", agent.Activity);

        var task = new SessionActivityItem("indexer", SessionActivityKind.Task);
        Assert.False(task.ActivateCommand.CanExecute(null));
        task.ApplyProgress(Client(lastMessage: "Indexed 600 of 1,200 files", phase: "Indexing", percentage: 140));
        Assert.Equal("Indexed 600 of 1,200 files", task.Activity);
        Assert.Equal(100, task.ProgressValue);
        Assert.False(task.HasOutput);

        task.ApplyProgress(Client(lastMessage: null, phase: "Indexing", percentage: null));
        Assert.Equal("Indexing", task.Activity);
        Assert.Equal(-1, task.ProgressValue);
    }

    [Fact]
    public void AgentRow_LinksToItsRunAndOpensItOnTheAgentsPage()
    {
        Loc.Load("en");
        using var vm = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        vm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "Review it" }));
        vm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "tool", ToolName = "task", ToolCallId = "review-tool", ToolStatus = "InProgress",
            Content = "{\"description\":\"Review session cleanup\",\"agent_type\":\"code-review\"}",
            Timestamp = DateTimeOffset.Now
        }));
        var run = Assert.Single(vm.SubagentRuns);
        var shown = 0;
        vm.SubagentRunShowRequested += () => shown++;
        var now = DateTimeOffset.UtcNow;

        vm.ApplyBackgroundActivitySnapshot([Agent("reviewer", "review-tool", now), Agent("elsewhere", "other-tool", now)], now);

        var linked = vm.RunningSessionActivities[0];
        Assert.Same(run, linked.Run);
        Assert.Same(run, linked.Presented);
        Assert.False(linked.CanExpand);
        linked.ActivateCommand.Execute(null);
        Assert.Same(run, vm.SelectedSubagentRun);
        Assert.Equal(1, shown);

        // An agent without a run in this chat shows its own progress instead of opening anything.
        var unlinked = vm.RunningSessionActivities[1];
        Assert.Null(unlinked.Run);
        Assert.Same(unlinked, unlinked.Presented);
        Assert.False(unlinked.ActivateCommand.CanExecute(null));
        unlinked.ApplyProgress(new TaskProgressAgent { RecentActivity = [Line("▸ grep")] });
        unlinked.ActivateCommand.Execute(null);
        Assert.True(unlinked.IsExpanded);
        Assert.Equal(1, shown);

        // Once its run appears, the row opens the run instead, so its own output closes.
        unlinked.Run = run;
        Assert.False(unlinked.IsExpanded);
    }

    [Fact]
    public void Snapshot_UpdatesTheListAndClearsItWhenTheSessionEnds_WithoutChangingReadiness()
    {
        var chat = new Chat { IsSessionActive = true };
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
            Chats = [chat]
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
        var now = DateTimeOffset.UtcNow;
        vm.ApplyBackgroundActivitySnapshot([Shell("preview", now)], now);

        Assert.True(vm.HasRunningSessionActivities);
        Assert.False(vm.HasBackgroundActivityNotice);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsSessionActive);

        vm.ApplyBackgroundActivitySnapshot([], now);
        Assert.Empty(vm.RunningSessionActivities);
        Assert.True(vm.HasBackgroundActivityNotice);
        Assert.True(vm.IsSessionActive);

        vm.ApplyBackgroundActivitySnapshot([Shell("preview", now)], now);
        vm.IsSessionActive = false;
        Assert.Empty(vm.RunningSessionActivities);
        Assert.False(vm.HasRunningSessionActivities);
    }

    private static TaskInfoShell Shell(string id, DateTimeOffset startedAt, string status = "running", string mode = "background") => new()
    {
        Id = id, Description = "Local app preview", Command = "dotnet run",
        Status = new GitHub.Copilot.Rpc.TaskStatus(status), ExecutionMode = new TaskExecutionMode(mode),
        AttachmentMode = TaskShellInfoAttachmentMode.Attached, StartedAt = startedAt
    };

    private static TaskInfoAgent Agent(string id, string toolCallId, DateTimeOffset startedAt) => new()
    {
        Id = id, AgentType = "code-review", DisplayName = "Code reviewer", Description = "Review session cleanup",
        Prompt = "", ToolCallId = toolCallId, StartedAt = startedAt, Status = GitHub.Copilot.Rpc.TaskStatus.Running
    };

    private static TaskProgressLine Line(string message) => new() { Message = message, Timestamp = DateTimeOffset.UtcNow };

    private static TaskProgressClient Client(string? lastMessage, string? phase, double? percentage) => new()
    {
        LastMessage = lastMessage, Phase = phase, Percentage = percentage, RecentActivity = [],
        Sequence = 1, Status = TaskClientStatus.Running, UpdatedAt = DateTimeOffset.UtcNow
    };
}
