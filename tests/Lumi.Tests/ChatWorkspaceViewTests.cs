using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatWorkspaceViewTests
{
    [Fact]
    public async Task WorkspaceHostsChatAndOneWorkspacePanel()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            var workspace = new ChatWorkspaceView
            {
                DataContext = chatVm,
                DataStore = dataStore,
                ShowInternalTitle = false,
                UseChatIslandChrome = false,
                WorkspacePanelMargin = new Thickness(0, 8, 8, 8),
            };
            var window = new Window
            {
                Width = 1000,
                Height = 720,
                Content = workspace,
            };

            window.Show();
            try
            {
                await Task.Delay(50);

                Assert.Same(chatVm, workspace.DataContext);
                Assert.NotNull(workspace.ChatView);
                Assert.False(workspace.ChatView!.ShowInternalTitle);
                Assert.Equal(new Thickness(0, 8, 8, 8), workspace.WorkspacePanelMargin);
                Assert.NotNull(workspace.FindControl<Grid>("WorkspaceGrid"));
                Assert.NotNull(workspace.FindControl<Border>("WorkspacePanel"));
                Assert.NotNull(workspace.FindControl<WorkspaceOverview>("WorkspaceOverview"));
                Assert.NotNull(workspace.FindControl<ContentControl>("WorkspacePageHost"));
                // Plans, agents, diffs and previews now open inside the Workspace, not beside it.
                Assert.Null(workspace.FindControl<Border>("PlanIsland"));
                Assert.Null(workspace.FindControl<Border>("SubagentIsland"));
                Assert.Null(workspace.FindControl<Border>("WorkspaceRail"));
                Assert.False(workspace.ChatView!.UseShellChrome);
                Assert.Contains("flat-window", workspace.ChatView!.FindControl<StrataTheme.Controls.StrataChatShell>("ChatShell")!.Classes);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WorkspacePageMethodsAreSafeAcrossRetargets()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            using var nextChatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
            var workspace = new ChatWorkspaceView
            {
                DataContext = chatVm,
                DataStore = dataStore,
            };
            var window = new Window
            {
                Width = 1100,
                Height = 760,
                Content = workspace,
            };

            window.Show();
            try
            {
                workspace.CloseWorkspacePages();
                Assert.False(workspace.IsBrowserOpen);
                Assert.Equal(WorkspacePage.Overview, workspace.WorkspacePage);

                workspace.DataContext = nextChatVm;
                workspace.CloseWorkspacePages();
                Assert.Same(nextChatVm, workspace.DataContext);
                Assert.Equal(WorkspacePage.Overview, workspace.WorkspacePage);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void WorkspaceIndexesUserMessagesNewestFirst()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Id = firstId,
            Role = "user",
            Content = "First prompt\nwith another line",
            Timestamp = new DateTimeOffset(2026, 6, 28, 20, 10, 0, TimeSpan.Zero),
            Attachments = ["C:\\Temp\\design.png"],
        }));
        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = "Answer",
        }));
        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "user",
            Author = "Lumi Job - Daily summary",
            Content = "Background job triggered: Daily summary",
        }));
        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Id = secondId,
            Role = "user",
            Content = "Second prompt about auth",
            Timestamp = new DateTimeOffset(2026, 6, 28, 20, 15, 0, TimeSpan.Zero),
            ActiveSkills = [new SkillReference { Name = "Code Helper" }],
        }));

        chatVm.RebuildTranscript();

        var messages = chatVm.WorkspaceMessages;
        Assert.True(messages.HasItems);
        Assert.True(chatVm.HasWorkspaceContent);
        Assert.False(chatVm.ShowWorkspaceEmptyState);
        Assert.Equal("2", messages.CountLabel);
        // The overview answers "what just happened", so the latest ask leads; numbers stay chronological.
        Assert.Collection(
            messages.Items,
            second =>
            {
                Assert.Equal("#2", second.NumberLabel);
                Assert.Equal("Second prompt about auth", second.Preview);
                Assert.Contains("1 skill", second.MetaText);
                Assert.True(second.CanJump);
                Assert.Equal($"turn:message:{secondId}", second.TargetTurnStableId);
            },
            first =>
            {
                Assert.Equal("#1", first.NumberLabel);
                Assert.Equal("First prompt with another line", first.Preview);
                Assert.Contains("1 file", first.MetaText);
                Assert.True(first.CanJump);
                Assert.Equal($"turn:message:{firstId}", first.TargetTurnStableId);
            });

        chatVm.WorkspaceSearchText = "auth";

        Assert.Equal("#2", Assert.Single(messages.Items).NumberLabel);
        Assert.Equal("1", messages.CountLabel);
        Assert.False(chatVm.WorkspaceSearchHasNoMatches);

        chatVm.WorkspaceSearchText = "nothing matches this";

        Assert.False(messages.HasItems);
        Assert.True(chatVm.WorkspaceSearchHasNoMatches);

        chatVm.ClearWorkspaceSearchCommand.Execute(null);

        Assert.Equal(2, messages.Items.Count);
        Assert.False(chatVm.WorkspaceSearchHasNoMatches);
    }

    [Fact]
    public void WorkspaceResetClearsUserMessages()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);

        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "user",
            Content = "Prompt that should disappear",
        }));
        chatVm.RebuildTranscript();

        Assert.True(chatVm.WorkspaceMessages.HasItems);
        Assert.Single(chatVm.WorkspaceMessages.Items);

        chatVm.Messages.Clear();

        Assert.False(chatVm.WorkspaceMessages.HasItems);
        Assert.False(chatVm.HasWorkspaceContent);
        Assert.True(chatVm.ShowWorkspaceEmptyState);
        Assert.Empty(chatVm.WorkspaceMessages.Items);
        Assert.Equal("0", chatVm.WorkspaceMessages.CountLabel);
    }

    [Fact]
    public void WorkspaceIndexesLinksFromAssistantOutput()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);

        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "user",
            Content = "Do not index [this user link](https://user.example/request).",
        }));
        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = """
                Read the [Lumi guide](https://example.com/lumi/guide) and
                [Lumi repository](https://github.com/adirh3/Lumi).

                A third link: [Avalonia docs](https://avaloniaui.net/docs).
                Duplicate: [same guide](https://example.com/lumi/guide).

                Inline code `[ignored](https://ignored.example/inline)` is not a link.
                ![Image](https://ignored.example/image.png)

                ```text
                [ignored](https://ignored.example/fenced)
                ```
                """,
        }));

        chatVm.RebuildTranscript();

        var links = chatVm.WorkspaceLinks;
        Assert.True(links.HasItems);
        Assert.True(chatVm.HasWorkspaceContent);
        Assert.Equal("3", links.CountLabel);
        Assert.Collection(
            links.Items,
            avalonia =>
            {
                Assert.Equal("Avalonia docs", avalonia.Title);
                Assert.Equal("https://avaloniaui.net/docs", avalonia.Url);
            },
            github =>
            {
                Assert.Equal("Lumi repository", github.Title);
                Assert.Equal("https://github.com/adirh3/Lumi", github.Url);
            },
            guide =>
            {
                Assert.Equal("Lumi guide", guide.Title);
                Assert.Equal("example.com", guide.Domain);
                Assert.Equal("https://example.com/lumi/guide", guide.Url);
            });

        chatVm.WorkspaceSearchText = "github";

        Assert.Equal("github.com", Assert.Single(links.Items).Domain);

        chatVm.Messages.Clear();

        Assert.False(links.HasItems);
        Assert.Empty(links.Items);
        Assert.Equal("0", links.CountLabel);
    }

    [Fact]
    public void WorkspaceCachesAssistantLinksUntilMessageContentChanges()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, TestCopilot.Shared);
        var assistantVm = new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = "[First](https://example.com/first)",
        });
        chatVm.Messages.Add(assistantVm);

        chatVm.RebuildTranscript();
        var firstCachedItem = Assert.Single(chatVm.WorkspaceLinks.Items);

        chatVm.RebuildWorkspacePanel();

        Assert.Same(firstCachedItem, Assert.Single(chatVm.WorkspaceLinks.Items));

        assistantVm.Message.Content += "\n[Second](https://example.com/second)";
        assistantVm.NotifyContentChanged();
        chatVm.RebuildWorkspacePanel();

        Assert.Equal(2, chatVm.WorkspaceLinks.Items.Count);
        var updatedFirst = chatVm.WorkspaceLinks.Items.Single(link => link.Url == "https://example.com/first");
        Assert.NotSame(firstCachedItem, updatedFirst);

        chatVm.Messages.Clear();
        Assert.Empty(chatVm.WorkspaceLinks.Items);

        chatVm.Messages.Add(assistantVm);
        chatVm.RebuildWorkspacePanel();

        Assert.NotSame(updatedFirst, chatVm.WorkspaceLinks.Items.Single(link => link.Url == "https://example.com/first"));
    }

    [Fact]
    public void MainAndDetachedHostXamlUseSameWorkspaceComponent()
    {
        var root = FindRepoRoot();
        var mainWindowXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "MainWindow.axaml"));
        var chatWindowXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatWindow.axaml"));

        Assert.Contains("<views:ChatWorkspaceView x:Name=\"ChatContentGrid\"", mainWindowXaml);
        Assert.Contains("UseChatIslandChrome=\"True\"", mainWindowXaml);
        Assert.Contains("ShowInternalTitle=\"True\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"BrowserIsland\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"DiffIsland\"", mainWindowXaml);
        Assert.DoesNotContain("x:Name=\"PlanIsland\"", mainWindowXaml);

        Assert.Contains("<views:ChatWorkspaceView x:Name=\"DetachedChatView\"", chatWindowXaml);
        Assert.Contains("UseChatIslandChrome=\"False\"", chatWindowXaml);
        Assert.Contains("ShowInternalTitle=\"False\"", chatWindowXaml);
        Assert.Contains("WorkspacePanelMargin=\"0,8,8,8\"", chatWindowXaml);
        Assert.Contains("RowDefinitions=\"38,*\"", chatWindowXaml);
        Assert.Contains("Grid.Row=\"1\"", chatWindowXaml);
        Assert.Contains("x:Name=\"TitleDragRegion\"", chatWindowXaml);
        Assert.Contains("chrome:WindowDecorationProperties.ElementRole=\"TitleBar\"", chatWindowXaml);
        Assert.DoesNotContain("Text=\"{Binding WindowTitle}\"", chatWindowXaml);
        Assert.Contains("x:Name=\"WindowIsland\"", chatWindowXaml);
        Assert.Contains("CornerRadius=\"0\"", chatWindowXaml);
        Assert.DoesNotContain("CornerRadius=\"{DynamicResource Radius.Overlay}\"", chatWindowXaml);
        Assert.DoesNotContain("<Grid Height=\"10\"", chatWindowXaml);
        Assert.Contains("Panel.ZIndex=\"10\"", chatWindowXaml);
        Assert.True(chatWindowXaml.IndexOf("x:Name=\"TitleDragRegion\"", StringComparison.Ordinal) <
            chatWindowXaml.IndexOf("x:Name=\"DetachedChatView\"", StringComparison.Ordinal));
        var workspaceXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatWorkspaceView.axaml"));
        Assert.Contains("UseShellChrome=\"{Binding UseChatIslandChrome", workspaceXaml);
        Assert.Contains("<views:WorkspaceOverview x:Name=\"WorkspaceOverview\"", workspaceXaml);
        Assert.Contains("x:Name=\"WorkspacePageHost\"", workspaceXaml);
        var overviewXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "WorkspaceOverview.axaml"));
        Assert.Contains("Section=\"{Binding WorkspaceLinks}\"", overviewXaml);
        Assert.Contains("Section=\"{Binding WorkspaceMessages}\"", overviewXaml);
        Assert.Contains("Command=\"{Binding OpenWorkspacePlanCommand}\"", overviewXaml);
        Assert.Contains("Command=\"{Binding OpenWorkspaceAgentsCommand}\"", overviewXaml);
        Assert.Contains("Command=\"{Binding ShowGitChangesCommand}\"", overviewXaml);
        var chatViewXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatView.axaml"));
        Assert.Contains("StrataChatShell.flat-window /template/ Border#PART_Root", chatViewXaml);
        Assert.Contains("StrataChatShell.flat-window /template/ Border#PART_HeaderChrome", chatViewXaml);
        // Agents live in the Workspace now: the header keeps one Workspace toggle that carries the live count.
        Assert.DoesNotContain("SubagentToggleButton", chatViewXaml);
        Assert.Contains("x:Name=\"WorkspaceToggleButton\"", chatViewXaml);
        Assert.DoesNotContain("x:Name=\"BrowserIsland\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"DiffIsland\"", chatWindowXaml);
        Assert.DoesNotContain("x:Name=\"PlanIsland\"", chatWindowXaml);
    }

    [Fact]
    public void ChatViewKeepsCodingBranchSlotVisibleForCodingProjects()
    {
        var root = FindRepoRoot();
        var chatViewXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatView.axaml"));

        Assert.Contains("IsVisible=\"{Binding IsCodingProject}\"", chatViewXaml);
        Assert.Contains("Text=\"{Binding GitBranchLabel}\"", chatViewXaml);
    }

    [Fact]
    public void MainWindowChatListDoubleClickUsesDetachedWindowCommand()
    {
        var root = FindRepoRoot();
        var mainWindowCode = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("OnChatListPointerPressed", mainWindowCode);
        Assert.Contains("IsChatListDoubleClick(chat, point.Position, e)", mainWindowCode);
        Assert.Contains("ChatListHandlersAttachedProperty", mainWindowCode);
        Assert.Contains("OpenChatInNewWindowCommand.Execute(chat)", mainWindowCode);
    }

    [Fact]
    public async Task MainWindowChatListDoubleClickRequestsDetachedWindow()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat
            {
                Title = "Open by double click",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var data = CreateAppData();
            data.Settings.IsOnboarded = true;
            data.Chats.Add(chat);
            var dataStore = new DataStore(data);
            var viewModel = new MainViewModel(
                dataStore,
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false,
                initializeCopilotOnStartup: false);
            DetachedChatWindowRequest? request = null;
            viewModel.OpenChatWindowRequested += requested => request = requested;

            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820,
            };

            window.Show();
            try
            {
                await PumpAsync();

                var listItem = window.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .First(item => ReferenceEquals(item.DataContext, chat));
                var topLeft = listItem.TranslatePoint(new Point(0, 0), window)
                    ?? throw new InvalidOperationException("Chat list item is not attached.");
                var point = topLeft + new Point(listItem.Bounds.Width / 2, listItem.Bounds.Height / 2);

                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                await Task.Delay(80);
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();

                Assert.Same(chat, request?.Chat);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
                request?.WindowVM.Dispose();
                request?.WindowVM.ChatVM.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void BrowserViewReparentsNativeWebViewBeforeBoundsUpdates()
    {
        var root = FindRepoRoot();
        var browserViewCode = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "BrowserView.axaml.cs"));
        var browserServiceCode = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Services", "BrowserService.cs"));

        Assert.Contains("_browserService.SetParentHwnd(platformHandle.Handle)", browserViewCode);
        Assert.Contains("controller.ParentWindow = hwnd", browserServiceCode);
        Assert.Contains("controller.NotifyParentWindowPositionChanged()", browserServiceCode);
        Assert.Contains("_webViewHwnd = IntPtr.Zero", browserServiceCode);
    }

    private static AppData CreateAppData() => new()
    {
        Settings = new UserSettings
        {
            AutoSaveChats = false,
            EnableMemoryAutoSave = false,
        },
    };

    private static string FindRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startPath in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory(),
                     Path.GetDirectoryName(sourceFilePath) ?? "",
                 })
        {
            if (string.IsNullOrWhiteSpace(startPath))
                continue;

            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate Lumi repository root.");
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
