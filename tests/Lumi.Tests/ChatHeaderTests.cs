using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>The floating chat header: the chat's mark, the live step it shows and its agent summary.</summary>
public sealed class ChatHeaderTests
{
    [Fact]
    public void Mark_IsAbsentWithoutAProjectOrAgent()
        => Assert.Null(ChatHeaderMark.Create(null, null, null));

    [Fact]
    public void Mark_ShowsTheAgentGlyphOnTheProjectHue()
    {
        var project = new Project { Name = "Home finances" };

        var mark = ChatHeaderMark.Create(project, "Coding Lumi", "⚡");

        Assert.NotNull(mark);
        Assert.Equal("⚡", mark.Glyph);
        Assert.Equal(LaunchpadProjectBadge.HueFor(project.Id), mark.Hue);
        // A refresh that changes nothing compares equal, so the header is not repainted.
        Assert.Equal(mark, ChatHeaderMark.Create(project, "Coding Lumi", "⚡"));
    }

    [Fact]
    public void Mark_FallsBackToTheProjectInitialWithoutAnAgent()
    {
        Loc.Load("en");
        var project = new Project { Name = "home finances" };

        var mark = ChatHeaderMark.Create(project, null, null);

        Assert.NotNull(mark);
        Assert.Equal("H", mark.Glyph);
        Assert.Equal(LaunchpadProjectBadge.HueFor(project.Id), mark.Hue);
    }

    [Fact]
    public void Mark_GivesAnAgentWithoutAProjectANeutralHue()
    {
        var mark = ChatHeaderMark.Create(null, "Daily Planner", "📋");

        Assert.NotNull(mark);
        Assert.Equal("📋", mark.Glyph);
        Assert.Equal(mark.Hue, ChatHeaderMark.Create(null, "Creative Writer", "✍️")!.Hue);
    }

    [Fact]
    public void LiveActivity_FollowsTheTypingIndicator()
    {
        var builder = CreateBuilder();
        var changes = 0;
        builder.LiveActivityChanged += () => changes++;

        builder.ShowTypingIndicator("Thinking…");
        Assert.Equal("Thinking…", builder.LiveActivityLabel);

        builder.UpdateTypingIndicatorLabel("Reasoning…");
        Assert.Equal("Reasoning…", builder.LiveActivityLabel);

        builder.HideTypingIndicator();
        Assert.Null(builder.LiveActivityLabel);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void LiveActivity_NamesTheRunningStepUntilItFinishes()
    {
        Loc.Load("en");
        var builder = CreateBuilder();
        var tool = CreateToolVm("tool-1", "powershell", "InProgress", "{\"command\":\"dotnet test\"}");

        builder.ProcessMessageToTranscript(tool);

        Assert.Contains(Loc.Tool_RunningCommand, builder.LiveActivityLabel, StringComparison.Ordinal);
        Assert.EndsWith(" · dotnet test", builder.LiveActivityLabel, StringComparison.Ordinal);

        tool.Message.ToolStatus = "Completed";
        tool.NotifyToolStatusChanged();
        Assert.Null(builder.LiveActivityLabel);
    }

    [Fact]
    public void LiveActivity_PrefersTheAgentsReportedIntent()
    {
        Loc.Load("en");
        var builder = CreateBuilder();

        builder.ProcessMessageToTranscript(
            CreateToolVm("intent-1", "report_intent", "InProgress", "{\"intent\":\"Building the charts\"}"));
        builder.ProcessMessageToTranscript(
            CreateToolVm("tool-1", "view", "InProgress", "{\"path\":\"E:\\\\repo\\\\notes.txt\"}"));

        Assert.Equal("Building the charts…", builder.LiveActivityLabel);

        builder.CloseCurrentToolGroup();
        Assert.Null(builder.LiveActivityLabel);
    }

    [Fact]
    public void HeaderActivityText_FallsBackToTheStatusThenToWorking()
    {
        Loc.Load("en");
        var viewModel = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal(Loc.ToolGroup_Working, viewModel.HeaderActivityText);

        viewModel.StatusText = Loc.Status_Generating;

        Assert.Equal(Loc.Status_Generating, viewModel.HeaderActivityText);
        Assert.Contains(nameof(ChatViewModel.HeaderActivityText), changed);
    }

    [Fact]
    public void HeaderAgentsText_CountsRunningAgents()
    {
        Loc.Load("en");
        var viewModel = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.RunningSubagentCount = 1;
        Assert.Equal("1 agent working", viewModel.HeaderAgentsText);

        viewModel.RunningSubagentCount = 3;
        Assert.Equal("3 agents working", viewModel.HeaderAgentsText);
        Assert.Contains(nameof(ChatViewModel.HeaderAgentsText), changed);
    }

    [Fact]
    public void HeaderTools_LightTheOneWhosePageTheWorkspaceShows()
    {
        using var viewModel = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.False(viewModel.IsHeaderWorkspaceActive);
        Assert.False(viewModel.IsHeaderDesktopActive);

        viewModel.IsWorkspacePanelOpen = true;
        Assert.True(viewModel.IsHeaderWorkspaceActive);
        Assert.Contains(nameof(ChatViewModel.IsHeaderWorkspaceActive), changed);

        viewModel.WorkspacePage = WorkspacePage.Desktop;
        Assert.True(viewModel.IsHeaderDesktopActive);
        Assert.False(viewModel.IsHeaderWorkspaceActive);
        Assert.Contains(nameof(ChatViewModel.IsHeaderDesktopActive), changed);

        // The browser lights its own button through IsBrowserOpen.
        viewModel.WorkspacePage = WorkspacePage.Browser;
        Assert.False(viewModel.IsHeaderDesktopActive);
        Assert.False(viewModel.IsHeaderWorkspaceActive);

        viewModel.WorkspacePage = WorkspacePage.Plan;
        Assert.True(viewModel.IsHeaderWorkspaceActive);

        viewModel.IsWorkspacePanelOpen = false;
        Assert.False(viewModel.IsHeaderWorkspaceActive);
        Assert.False(viewModel.IsHeaderDesktopActive);
    }

    [Fact]
    public void HeaderStarted_SaysTodayOrYesterdayThenJustTheDay()
    {
        Loc.Load("en");
        var now = Local(2026, 10, 14, 14, 2);

        Assert.Equal("Started today 10:58", ChatViewModel.FormatHeaderStarted(Local(2026, 10, 14, 10, 58), now));
        Assert.Equal("Started yesterday 18:20", ChatViewModel.FormatHeaderStarted(Local(2026, 10, 13, 18, 20), now));
        Assert.Equal("Started Oct 3", ChatViewModel.FormatHeaderStarted(Local(2026, 10, 3, 9, 0), now));
        Assert.Equal("Started Dec 30, 2025", ChatViewModel.FormatHeaderStarted(Local(2025, 12, 30, 9, 0), now));

        static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
            => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));
    }

    [Fact]
    public void HeaderDetails_CountOnlyWhatWasSaid()
    {
        Loc.Load("en");
        var chat = new Chat { Title = "Budget", CreatedAt = DateTimeOffset.Now };
        chat.Messages.AddRange(
        [
            new ChatMessage { Role = "user", Content = "Build me a budget" },
            new ChatMessage { Role = "tool", Content = "{\"command\":\"dir\"}" },
            new ChatMessage { Role = "assistant", Content = "" },
            new ChatMessage { Role = "assistant", Content = "Here it is." },
        ]);
        using var viewModel = new ChatViewModel(new DataStore(new AppData()), TestCopilot.Shared) { CurrentChat = chat };

        Assert.StartsWith("Started today ", viewModel.HeaderDetailsText, StringComparison.Ordinal);
        Assert.EndsWith(" · 2 messages", viewModel.HeaderDetailsText, StringComparison.Ordinal);

        chat.Messages.RemoveAt(3);
        Assert.EndsWith(" · 1 message", viewModel.HeaderDetailsText, StringComparison.Ordinal);
    }

    private static TranscriptBuilder CreateBuilder()
    {
        var builder = new TranscriptBuilder(
            CreateDataStore(), _ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask, () => null);
        builder.SetLiveTarget(new ObservableCollection<TranscriptTurn>());
        return builder;
    }

    private static ChatMessageViewModel CreateToolVm(string toolCallId, string toolName, string toolStatus, string content)
        => new(new ChatMessage
        {
            Role = "tool",
            ToolCallId = toolCallId,
            ToolName = toolName,
            ToolStatus = toolStatus,
            Content = content,
            Timestamp = DateTimeOffset.Now,
        });

    private static DataStore CreateDataStore()
    {
#pragma warning disable SYSLIB0050
        var store = (DataStore)FormatterServices.GetUninitializedObject(typeof(DataStore));
#pragma warning restore SYSLIB0050
        var data = new AppData();
        data.Settings.ShowToolCalls = true;
        typeof(DataStore)
            .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, data);
        return store;
    }
}

/// <summary>The floating header on screen: what its tools do under the pointer.</summary>
[Collection("Headless UI")]
public sealed class ChatHeaderUiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tools_MoveTheirIconsOnHover_UnlessAnimationsAreOff(bool animations)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var data = new AppData
            {
                Settings = new UserSettings { ShowAnimations = animations, AutoSaveChats = false, EnableMemoryAutoSave = false }
            };
            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            var view = new ChatView { DataContext = viewModel };
            var window = new Window { Width = 1100, Height = 760, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();

                // Each tool moves the way its thing does, and comes back to rest.
                (string Tool, string Icon, string Still, string Hovered)[] motions =
                [
                    ("BrowserToggleButton", "tool-globe", "rotate(0deg) scale(1)", "rotate(-23.5deg) scale(1.1)"),
                    ("DesktopPreviewButton", "tool-screen", "scale(1)", "scale(1.14)"),
                    ("WorkspaceToggleButton", "tool-panels", "rotate(0deg)", "rotate(180deg)"),
                ];

                foreach (var (tool, icon, still, hovered) in motions)
                {
                    var button = view.FindControl<Button>(tool)!;
                    AssertPose(button, icon, still);
                    ((IPseudoClasses)button.Classes).Set(":pointerover", true);
                    AssertPose(button, icon, animations ? hovered : still);
                    ((IPseudoClasses)button.Classes).Set(":pointerover", false);
                    AssertPose(button, icon, still);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>The pose a motion ends in (the style's value), not the transition's current frame.</summary>
    private static void AssertPose(Button tool, string iconClass, string pose)
    {
        var icon = tool.GetLogicalDescendants().OfType<PathIcon>().Single(i => i.Classes.Contains(iconClass));
        var transform = Assert.IsType<TransformOperations>(icon.GetBaseValue(Visual.RenderTransformProperty).GetValueOrDefault());
        Assert.Equal(TransformOperations.Parse(pose).Operations, transform.Operations);
    }

    [Fact]
    public async Task HeaderOverlay_LandsTargetsAndPagesClearOfTheFloatingHeader()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var items = new List<Border>();
            var transcript = new StackPanel();
            for (var index = 0; index < 30; index++)
            {
                var item = new Border { Height = 80 };
                items.Add(item);
                transcript.Children.Add(item);
            }

            var shell = new StrataChatShell
            {
                Header = new Border { Height = 64 },
                Composer = new Border { Height = 96 },
                Transcript = transcript,
                ExtendsTranscriptBehindHeader = true
            };
            var window = new Window { Width = 400, Height = 800, Content = shell };
            window.Show();
            try
            {
                Settle(window);
                var scroll = shell.TranscriptScrollViewer!;
                var header = shell.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "PART_HeaderChrome").Bounds.Height;
                shell.PreserveViewport();
                shell.ScrollToVerticalOffset(1200);
                Settle(window);

                // Brought into view from above, a target lands below the header instead of under it.
                items[5].BringIntoView();
                Settle(window);
                Assert.Equal(header, items[5].TranslatePoint(default, scroll)!.Value.Y, 1);

                // A page moves by what the reader can see, so no line slips under the header.
                var before = scroll.Offset.Y;
                items[12].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageDown });
                Settle(window);
                Assert.Equal(scroll.Viewport.Height - header, scroll.Offset.Y - before, 1);

                // The scrollbar starts below the header, which takes the taps on its empty space.
                var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Name == "PART_VerticalScrollBar");
                var overlaidTop = bar.Margin.Top;
                Assert.Equal(header, shell.TopChromeInset, 1);

                shell.ExtendsTranscriptBehindHeader = false;
                Settle(window);
                Assert.Equal(0, shell.TopChromeInset);
                Assert.Equal(bar.Margin.Top + header, overlaidTop, 1);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        static void Settle(Window window)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
