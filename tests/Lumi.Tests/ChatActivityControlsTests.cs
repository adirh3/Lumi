using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

#pragma warning disable GHCP001 // The SDK exposes task snapshots through its typed experimental API.

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatActivityControlsTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    public async Task BackgroundActivity_IsCompactBesideContext_AndExpandsOutsideComposer(double width)
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var chat = new Chat
                {
                    IsSessionActive = true,
                    Messages = [new ChatMessage { Role = "assistant", Content = "Ready." }]
                };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Chats = [chat]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = width, Height = 700, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var activity = view.FindControl<Button>("BackgroundActivityButton")!;
                    var context = view.FindControl<Button>("ContextWindowButton")!;
                    var composer = view.FindControl<StrataChatComposer>("Composer")!;
                    var flyout = Assert.IsType<Flyout>(activity.Flyout);
                    var details = Assert.IsType<StackPanel>(flyout.Content);

                    Assert.True(activity.IsVisible);
                    Assert.False(flyout.IsOpen);
                    Assert.Equal(22, activity.Bounds.Height);
                    Assert.InRange(activity.Bounds.Width, 30, 160);
                    Assert.Same(context.GetLogicalParent(), activity.GetLogicalParent());
                    Assert.DoesNotContain(activity, composer.GetLogicalDescendants());
                    Assert.False(Assert.IsType<Grid>(composer.StatusContent).IsVisible);

                    var activityPosition = activity.TranslatePoint(default, view)!.Value;
                    var contextPosition = context.TranslatePoint(default, view)!.Value;
                    var composerPosition = composer.TranslatePoint(default, view)!.Value;
                    Assert.InRange(contextPosition.X - activityPosition.X - activity.Bounds.Width, 5, 7);
                    Assert.Equal(contextPosition.Y, activityPosition.Y);
                    Assert.True(activityPosition.Y + activity.Bounds.Height <= composerPosition.Y);

                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();

                    Assert.True(flyout.IsOpen);
                    Assert.Same(vm, details.DataContext);
                    var stop = details.GetLogicalDescendants().OfType<Button>()
                        .Single(button => button.Name == "StopBackgroundSession");
                    Assert.Same(vm.StopGenerationCommand, stop.Command);

                    vm.ApplyBackgroundActivitySnapshot(
                    [
                        new TaskInfoShell
                        {
                            Id = "preview",
                            Description = "Local app preview",
                            Command = "dotnet run",
                            Status = GitHub.Copilot.Rpc.TaskStatus.Running,
                            ExecutionMode = TaskExecutionMode.Background,
                            AttachmentMode = TaskShellInfoAttachmentMode.Attached,
                            StartedAt = DateTimeOffset.UtcNow
                        }
                    ], DateTimeOffset.UtcNow);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(flyout.IsOpen);
                    var list = details.GetLogicalDescendants().OfType<ItemsControl>()
                        .Single(control => control.Name == "RunningSessionActivities");
                    Assert.Same(vm.RunningSessionActivities, list.ItemsSource);
                    var count = activity.GetLogicalDescendants().OfType<TextBlock>()
                        .Single(control => control.Name == "RunningSessionActivityCount");
                    Assert.Equal("1", count.Text);
                    Assert.True(count.IsVisible);

                    vm.IsBusy = true;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(activity.IsVisible);
                    Assert.False(flyout.IsOpen);

                    vm.IsBusy = false;
                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();
                    vm.CurrentChat = new Chat { IsSessionActive = true };
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(flyout.IsOpen);
                    Assert.Empty(vm.RunningSessionActivities);
                    Assert.False(count.IsVisible);

                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();
                    vm.IsSessionActive = false;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(activity.IsVisible);
                    Assert.False(flyout.IsOpen);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public async Task BackgroundActivity_RowsShowLiveWork_ExpandCommandsAndOpenAgentRuns()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var chat = new Chat { IsSessionActive = true };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Chats = [chat]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
                vm.Messages.Add(new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "Review it" }));
                vm.Messages.Add(new ChatMessageViewModel(new ChatMessage
                {
                    Role = "tool", ToolName = "task", ToolCallId = "review-tool", ToolStatus = "InProgress",
                    Content = "{\"description\":\"Review session cleanup\",\"agent_type\":\"code-review\"}",
                    Timestamp = DateTimeOffset.Now
                }));
                var run = Assert.Single(vm.SubagentRuns);
                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var now = DateTimeOffset.UtcNow;
                    vm.ApplyBackgroundActivitySnapshot(
                    [
                        new TaskInfoShell
                        {
                            Id = "preview", Description = "Local app preview", Command = "dotnet run",
                            Status = GitHub.Copilot.Rpc.TaskStatus.Running, ExecutionMode = TaskExecutionMode.Background,
                            AttachmentMode = TaskShellInfoAttachmentMode.Attached, StartedAt = now
                        },
                        new TaskInfoAgent
                        {
                            Id = "reviewer", AgentType = "code-review", DisplayName = "Code reviewer",
                            Description = "Review session cleanup", Prompt = "", ToolCallId = "review-tool",
                            StartedAt = now, Status = GitHub.Copilot.Rpc.TaskStatus.Running
                        }
                    ], now);
                    vm.RunningSessionActivities[0].ApplyProgress(
                        new TaskProgressShell { RecentOutput = "Restoring\nNow listening on: http://localhost:5000" });

                    var activity = view.FindControl<Button>("BackgroundActivityButton")!;
                    var flyout = Assert.IsType<Flyout>(activity.Flyout);
                    var details = Assert.IsType<StackPanel>(flyout.Content);
                    flyout.ShowAt(activity);
                    Dispatcher.UIThread.RunJobs();

                    var rows = details.GetVisualDescendants().OfType<Button>()
                        .Where(button => button.Classes.Contains("session-task")).ToList();
                    Assert.Equal(2, rows.Count);

                    // A command says what it is doing now: its newest output line.
                    var command = rows[0].GetVisualDescendants().OfType<StrataAgentRun>().Single();
                    Assert.Contains("command", command.Classes);
                    Assert.Equal("Local app preview", command.Title);
                    Assert.Equal("Now listening on: http://localhost:5000", command.Activity);

                    // An agent is its live run, as the Workspace lists it.
                    var agent = rows[1].GetVisualDescendants().OfType<StrataAgentRun>().Single();
                    Assert.Same(run, agent.DataContext);

                    // Clicking a command shows its newest output beneath the row.
                    var output = details.GetVisualDescendants().OfType<Border>()
                        .First(border => border.Classes.Contains("session-task-output"));
                    Assert.False(output.IsVisible);
                    new ButtonAutomationPeer(rows[0]).Invoke();
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(output.IsVisible);
                    Assert.True(flyout.IsOpen);

                    // Clicking an agent opens its run on the Agents page and gets the list out of the way.
                    new ButtonAutomationPeer(rows[1]).Invoke();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Same(run, vm.SelectedSubagentRun);
                    Assert.False(flyout.IsOpen);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }
    }

    [Fact]
    public void BackgroundActivity_KeepsTheStripAvailableWithoutContextUsage()
    {
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.IsSessionActive = true;
        Assert.False(vm.HasTokenUsage);
        Assert.True(vm.ShowInfoStrip);
        Assert.Contains(nameof(ChatViewModel.ShowInfoStrip), notifications);

        notifications.Clear();
        vm.IsBusy = true;
        Assert.False(vm.ShowInfoStrip);
        Assert.Contains(nameof(ChatViewModel.ShowInfoStrip), notifications);

        vm.IsBusy = false;
        Assert.True(vm.ShowInfoStrip);
        vm.IsSessionActive = false;
        Assert.False(vm.ShowInfoStrip);
    }
}
