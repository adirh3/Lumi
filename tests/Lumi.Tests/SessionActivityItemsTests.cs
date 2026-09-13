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
        Assert.Equal("Local app preview", items[0].Title);
        Assert.Equal("Command", items[0].Kind);
        Assert.Equal("dotnet run", items[0].Detail);
        Assert.Equal("2m 00s", items[0].Elapsed);
        Assert.Equal("Code reviewer", items[1].Title);
        Assert.Equal("Review session cleanup", items[1].Detail);
        Assert.Equal("1m 30s", items[1].Elapsed);
        Assert.Equal("Background task", items[2].Kind);
    }

    [Fact]
    public void Snapshot_UsesTheCommandWhenDescriptionIsBlank_AndClampsClockSkew()
    {
        var now = DateTimeOffset.UtcNow;
        var shell = Shell("preview", now.AddSeconds(5));
        shell.Description = " ";
        var item = Assert.Single(ChatViewModel.BuildBackgroundActivityItems([shell], now));

        Assert.Equal("dotnet run", item.Title);
        Assert.False(item.HasDetail);
        Assert.Equal("0s", item.Elapsed);
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
}
