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
    public async Task WorkspaceHostsChatAndPreviewIslands()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, new CopilotService());
            var workspace = new ChatWorkspaceView
            {
                DataContext = chatVm,
                DataStore = dataStore,
                ShowInternalTitle = false,
                UseChatIslandChrome = false,
                PreviewIslandMargin = new Thickness(0, 8, 8, 8),
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
                Assert.Equal(new Thickness(0, 8, 8, 8), workspace.PreviewIslandMargin);
                Assert.NotNull(workspace.FindControl<Grid>("WorkspaceGrid"));
                Assert.NotNull(workspace.FindControl<Border>("BrowserIsland"));
                Assert.NotNull(workspace.FindControl<Border>("DiffIsland"));
                Assert.NotNull(workspace.FindControl<Border>("PlanIsland"));
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
    public async Task WorkspacePreviewMethodsAreSafeAcrossRetargets()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var data = CreateAppData();
            var dataStore = new DataStore(data);
            using var chatVm = new ChatViewModel(dataStore, new CopilotService());
            using var nextChatVm = new ChatViewModel(dataStore, new CopilotService());
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
                workspace.HideBrowserPanel();
                workspace.HideDiffPanel();
                workspace.HidePlanPanel();
                Assert.False(workspace.IsBrowserOpen);
                Assert.False(workspace.IsDiffOpen);
                Assert.False(workspace.IsPlanOpen);

                workspace.DataContext = nextChatVm;
                workspace.HideBrowserPanel();
                workspace.HideDiffPanel();
                workspace.HidePlanPanel();
                Assert.Same(nextChatVm, workspace.DataContext);
            }
            finally
            {
                window.Close();
                workspace.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public void WorkspaceIndexesUserMessagesInMessagesTab()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, new CopilotService());
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

        Assert.True(chatVm.HasWorkspaceUserMessages);
        Assert.True(chatVm.HasWorkspaceContent);
        Assert.True(chatVm.IsMessagesTabSelected);
        Assert.Equal("2", chatVm.WorkspaceUserMessagesCountLabel);
        Assert.Collection(
            chatVm.WorkspaceUserMessages,
            first =>
            {
                Assert.Equal("#1", first.NumberLabel);
                Assert.Equal("First prompt with another line", first.Preview);
                Assert.Contains("1 file", first.MetaText);
                Assert.True(first.CanJump);
                Assert.Equal($"turn:message:{firstId}", first.TargetTurnStableId);
            },
            second =>
            {
                Assert.Equal("#2", second.NumberLabel);
                Assert.Equal("Second prompt about auth", second.Preview);
                Assert.Contains("1 skill", second.MetaText);
                Assert.True(second.CanJump);
                Assert.Equal($"turn:message:{secondId}", second.TargetTurnStableId);
            });

        chatVm.WorkspaceSearchText = "auth";

        Assert.Single(chatVm.WorkspaceUserMessages);
        Assert.Equal("#2", chatVm.WorkspaceUserMessages[0].NumberLabel);
    }

    [Fact]
    public void WorkspaceResetClearsUserMessages()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, new CopilotService());

        chatVm.Messages.Add(new ChatMessageViewModel(new ChatMessage
        {
            Role = "user",
            Content = "Prompt that should disappear",
        }));
        chatVm.RebuildTranscript();

        Assert.True(chatVm.HasWorkspaceUserMessages);
        Assert.Single(chatVm.WorkspaceUserMessages);

        chatVm.Messages.Clear();

        Assert.False(chatVm.HasWorkspaceUserMessages);
        Assert.False(chatVm.HasWorkspaceContent);
        Assert.Empty(chatVm.WorkspaceUserMessages);
        Assert.Equal("0", chatVm.WorkspaceUserMessagesCountLabel);
    }

    [Fact]
    public void WorkspaceIndexesLinksFromAssistantOutput()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, new CopilotService());

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

        Assert.True(chatVm.HasWorkspaceLinks);
        Assert.True(chatVm.HasWorkspaceContent);
        chatVm.WorkspaceSelectedTab = ChatViewModel.WorkspaceTabLinks;
        Assert.True(chatVm.IsLinksTabSelected);
        Assert.Equal("3", chatVm.WorkspaceLinksCountLabel);
        Assert.Collection(
            chatVm.WorkspaceLinks,
            guide =>
            {
                Assert.Equal("Lumi guide", guide.Title);
                Assert.Equal("example.com", guide.Domain);
                Assert.Equal("https://example.com/lumi/guide", guide.Url);
            },
            github =>
            {
                Assert.Equal("Lumi repository", github.Title);
                Assert.Equal("https://github.com/adirh3/Lumi", github.Url);
            },
            avalonia =>
            {
                Assert.Equal("Avalonia docs", avalonia.Title);
                Assert.Equal("https://avaloniaui.net/docs", avalonia.Url);
            });

        chatVm.WorkspaceSearchText = "github";

        Assert.Single(chatVm.WorkspaceLinks);
        Assert.Equal("github.com", chatVm.WorkspaceLinks[0].Domain);

        chatVm.Messages.Clear();

        Assert.False(chatVm.HasWorkspaceLinks);
        Assert.Empty(chatVm.WorkspaceLinks);
        Assert.Equal("0", chatVm.WorkspaceLinksCountLabel);
    }

    [Fact]
    public void WorkspaceCachesAssistantLinksUntilMessageContentChanges()
    {
        Loc.Load("en");
        var dataStore = new DataStore(CreateAppData());
        using var chatVm = new ChatViewModel(dataStore, new CopilotService());
        var assistantVm = new ChatMessageViewModel(new ChatMessage
        {
            Role = "assistant",
            Content = "[First](https://example.com/first)",
        });
        chatVm.Messages.Add(assistantVm);

        chatVm.RebuildTranscript();
        var firstCachedItem = Assert.Single(chatVm.WorkspaceLinks);

        chatVm.RebuildWorkspacePanel();

        Assert.Same(firstCachedItem, Assert.Single(chatVm.WorkspaceLinks));

        assistantVm.Message.Content += "\n[Second](https://example.com/second)";
        assistantVm.NotifyContentChanged();
        chatVm.RebuildWorkspacePanel();

        Assert.Equal(2, chatVm.WorkspaceLinks.Count);
        Assert.NotSame(firstCachedItem, chatVm.WorkspaceLinks[0]);
        var updatedCachedItem = chatVm.WorkspaceLinks[0];

        chatVm.Messages.Clear();
        Assert.Empty(chatVm.WorkspaceLinks);

        chatVm.Messages.Add(assistantVm);
        chatVm.RebuildWorkspacePanel();

        Assert.NotSame(updatedCachedItem, chatVm.WorkspaceLinks[0]);
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
        Assert.Contains("PreviewIslandMargin=\"0,8,8,8\"", chatWindowXaml);
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
        Assert.Contains("x:Name=\"WsTabLinks\"", workspaceXaml);
        Assert.Contains("ItemsSource=\"{Binding WorkspaceLinks}\"", workspaceXaml);
        Assert.Contains("x:Name=\"WsTabMessages\"", workspaceXaml);
        Assert.Contains("ItemsSource=\"{Binding WorkspaceUserMessages}\"", workspaceXaml);
        var chatViewXaml = File.ReadAllText(Path.Combine(root, "src", "Lumi", "Views", "ChatView.axaml"));
        Assert.Contains("StrataChatShell.flat-window /template/ Border#PART_Root", chatViewXaml);
        Assert.Contains("StrataChatShell.flat-window /template/ Border#PART_HeaderChrome", chatViewXaml);
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
                new CopilotService(),
                new UpdateService(),
                startBackgroundJobs: false);
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
        Assert.Contains("_controller.ParentWindow = hwnd", browserServiceCode);
        Assert.Contains("_controller.NotifyParentWindowPositionChanged()", browserServiceCode);
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
