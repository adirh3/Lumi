using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    [InlineData(600, false)]
    [InlineData(1000, true)]
    public async Task RunningPill_ShowsBackgroundWorkInTheHeader_AndItsCardStopsTheSession(double width, bool expanded)
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
                    var running = view.FindControl<Button>("HeaderRunningButton")!;
                    var header = view.FindControl<Panel>("ChatHeaderLayer")!;
                    var composer = view.FindControl<StrataChatComposer>("Composer")!;
                    var card = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(running));
                    var details = Assert.IsType<StackPanel>(card.Content);
                    var label = running.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.Classes.Contains("header-pill-text"));

                    // Background work is the chat's state: a header pill, never a row above the composer.
                    Assert.Null(view.FindControl<Control>("CodingStrip"));
                    Assert.Null(view.FindControl<Control>("BackgroundActivityButton"));
                    Assert.True(running.IsEffectivelyVisible);
                    Assert.Contains(running, header.GetVisualDescendants());
                    Assert.DoesNotContain(running, composer.GetLogicalDescendants());
                    Assert.Equal(36, running.Bounds.Height);
                    Assert.Equal("Background", label.Text);
                    Assert.False(card.IsOpen);

                    new ButtonAutomationPeer(running).Invoke();
                    Dispatcher.UIThread.RunJobs();

                    Assert.True(card.IsOpen);
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
                    Assert.True(card.IsOpen);
                    var list = details.GetLogicalDescendants().OfType<ItemsControl>()
                        .Single(control => control.Name == "RunningSessionActivities");
                    Assert.Same(vm.RunningSessionActivities, list.ItemsSource);
                    Assert.Equal("1 running", label.Text);
                    Assert.Equal(expanded, label.IsVisible);
                    var terminal = running.GetVisualDescendants().OfType<Border>()
                        .Single(face => face.Classes.Contains("command"));
                    Assert.True(terminal.IsVisible);
                    var terminalPosition = terminal.TranslatePoint(default, running)!.Value;
                    Assert.True(terminalPosition.X >= running.Padding.Left,
                        $"The terminal starts at {terminalPosition.X}, inside the pill's {running.Padding.Left}px leading padding.");
                    Assert.True(terminalPosition.X + terminal.Bounds.Width <= running.Bounds.Width - running.Padding.Right);

                    // While Lumi replies, nothing runs in the background: the pill goes and takes its card.
                    vm.IsBusy = true;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(running.IsVisible);
                    Assert.False(card.IsOpen);

                    vm.IsBusy = false;
                    card.ShowAt(running);
                    Dispatcher.UIThread.RunJobs();
                    vm.CurrentChat = new Chat { IsSessionActive = true };
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(card.IsOpen);
                    Assert.Empty(vm.RunningSessionActivities);
                    Assert.Equal("Background", label.Text);
                    Assert.False(terminal.IsVisible);

                    card.ShowAt(running);
                    Dispatcher.UIThread.RunJobs();
                    vm.IsSessionActive = false;
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(running.IsVisible);
                    Assert.False(card.IsOpen);
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

                    var activity = view.FindControl<Button>("HeaderRunningButton")!;
                    var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(activity));
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
    public void RunningPill_FollowsTheSession_NotTheAssistant()
    {
        Loc.Load("en");
        var data = new AppData
        {
            Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
        };
        using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        vm.IsSessionActive = true;
        Assert.False(vm.HasTokenUsage);
        Assert.True(vm.ShowHeaderRunning);
        Assert.Equal("Background", vm.HeaderRunningText);
        Assert.Contains(nameof(ChatViewModel.ShowHeaderRunning), notifications);

        notifications.Clear();
        vm.IsBusy = true;
        Assert.False(vm.ShowHeaderRunning);
        Assert.Contains(nameof(ChatViewModel.ShowHeaderRunning), notifications);

        vm.IsBusy = false;
        Assert.True(vm.ShowHeaderRunning);
        vm.IsSessionActive = false;
        Assert.False(vm.ShowHeaderRunning);
    }

    [Fact]
    public async Task RunningPill_WhileLumiReplies_ShowsTheAgentsAndOpensTheirPage()
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
                vm.IsBusy = true;
                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    var running = view.FindControl<Button>("HeaderRunningButton")!;
                    var card = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(running));
                    var agentsRequested = 0;
                    vm.SubagentRunShowRequested += () => agentsRequested++;

                    Assert.True(running.IsEffectivelyVisible);
                    Assert.Equal("1 agent working", vm.HeaderRunningText);
                    Assert.Single(vm.HeaderRunningAgents);

                    new ButtonAutomationPeer(running).Invoke();
                    Dispatcher.UIThread.RunJobs();

                    Assert.Equal(1, agentsRequested);
                    Assert.False(card.IsOpen);
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
    public async Task ContextRing_SplitsTheTitlePill_AndWarmsAsTheWindowFills()
    {
        var session = HeadlessTestSession.Start();
        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var chat = new Chat { Messages = [new ChatMessage { Role = "assistant", Content = "Ready." }] };
                var data = new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                    Chats = [chat]
                };
                using var vm = new ChatViewModel(new DataStore(data), TestCopilot.Shared) { CurrentChat = chat };
                var view = new ChatView { DataContext = vm };
                var window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                try
                {
                    vm.ContextTokenLimit = 200_000;
                    vm.ContextCurrentTokens = 60_000;
                    Dispatcher.UIThread.RunJobs();
                    var pill = view.FindControl<Panel>("ChatTitlePill")!;
                    var title = view.FindControl<Button>("ChatTitleButton")!;
                    var ring = view.FindControl<Button>("ContextWindowButton")!;
                    var percent = ring.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.Classes.Contains("context-ring-text"));

                    // One pill, two halves: the chat, then how full its context window is.
                    Assert.Contains(title, pill.GetVisualDescendants());
                    Assert.Contains(ring, pill.GetVisualDescendants());
                    Assert.True(ring.IsEffectivelyVisible);
                    Assert.Equal(108, vm.ContextUsageSweep, 3);
                    Assert.DoesNotContain("high", ring.Classes);
                    Assert.False(percent.IsVisible);
                    Assert.Equal("Context window 30% full · 60K / 200K", ToolTip.GetTip(ring));

                    vm.ContextCurrentTokens = 160_000;
                    Dispatcher.UIThread.RunJobs();
                    Assert.Contains("high", ring.Classes);
                    Assert.True(percent.IsVisible);
                    Assert.Equal("80%", percent.Text);

                    // Its card holds the window's details and the one thing to do about it.
                    var card = Assert.IsType<Flyout>(ring.Flyout);
                    var details = Assert.IsType<StackPanel>(card.Content);
                    card.ShowAt(ring);
                    Dispatcher.UIThread.RunJobs();
                    var compact = details.GetLogicalDescendants().OfType<Button>()
                        .Single(button => button.Name == "ContextCompactButton");
                    Assert.Same(vm.CompactContextCommand, compact.Command);
                    card.Hide();
                    Assert.Null(view.FindControl<Control>("CodingStrip"));
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
}
