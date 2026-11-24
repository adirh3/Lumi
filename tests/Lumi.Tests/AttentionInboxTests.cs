using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Microsoft.Copilot;
using Microsoft.Extensions.AI;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AttentionInboxTests
{
    private const string Form = """{"id":"trip","title":"Trip preferences","fields":[{"id":"city","label":"City","required":true}]}""";

    [Fact]
    public Task CombinedListPrioritizesRequestsDeduplicatesUnreadAndRestoresTheDraftAndProject() => Run(async () =>
    {
        var draftProject = new Project { Name = "Draft project" };
        var otherProject = new Project { Name = "Other project" };
        var pending = new Chat { Title = "Waiting", ProjectId = otherProject.Id };
        var unread = new Chat { Title = "Unread only", HasUnreadMessages = true };
        var data = Data(pending, unread);
        data.Projects.AddRange([draftProject, otherProject]);
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        main.SelectedProjectFilter = draftProject.Id;
        await Settle();
        main.NewChatCommand.Execute(null);
        const string draft = "Keep this exact unsent project draft.";
        main.ChatVM.PromptText = draft;
        var owner = await sessions.AcquireChatAsync(pending);
        var waiting = Ask(owner, pending, "Which option?");
        await Settle();
        pending.HasUnreadMessages = true;
        main.RefreshUnreadState();
        Assert.Equal(["Waiting", "Unread only"], main.NotificationChats.Select(entry => entry.Title));
        Assert.True(main.NotificationChats[0].NeedsInput);
        Assert.False(main.NotificationChats[1].NeedsInput);
        Assert.Equal("Which option?", main.NotificationChats[0].Ask);
        Assert.Equal(2, main.UnreadChatCount);
        var request = main.NotificationChats[0];
        await main.OpenUnreadChatCommand.ExecuteAsync(request);
        await Settle();
        Assert.Equal(pending.Id, main.ActiveChatId);
        Assert.Equal(otherProject.Id, main.SelectedProjectFilter);
        Assert.False(pending.HasUnreadMessages);
        Assert.True(main.NotificationChats[0].NeedsInput);
        Assert.False(waiting.IsCompleted);
        Assert.Single(main.AttentionInbox.Requests);
        Assert.True(await main.TryNavigateHistoryAsync(-1));
        await Settle();
        Assert.Null(main.ChatVM.CurrentChat);
        Assert.Equal(draftProject.Id, main.SelectedProjectFilter);
        Assert.Equal(draft, main.ChatVM.PromptText);
        owner.SubmitQuestionAnswer(request.PendingRequest!.RequestId, "Yes");
        Assert.Contains("Yes", (await waiting)?.ToString());
        await Settle();
        Assert.Equal("Unread only", Assert.Single(main.NotificationChats).Title);
        Assert.DoesNotContain(pending.Messages, message => message.Role == "user");
        sessions.Release(owner);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public Task ExistingUnreadSlotHasOneToggleAndOneListAndAnswersStayInTheChat(string language) => Run(async () =>
    {
        Loc.Load(language);
        var pending = new Chat { Title = "Pending question" };
        var unread = new Chat { Title = "Unread reply", HasUnreadMessages = true };
        var data = Data(pending, unread);
        data.Settings.Language = language;
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var owner = await sessions.AcquireChatAsync(pending);
        var waiting = Ask(owner, pending, "Keep every project visible?");
        await Settle();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 850 };
        window.Show();
        try
        {
            await Settle();
            window.UpdateLayout();
            var root = window.FindControl<Border>("UnreadInboxRoot")!;
            var toggle = window.FindControl<Button>("UnreadInboxToggle")!;
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Contains("unread-toggle", toggle.Classes);
            Assert.True(Application.Current!.TryGetResource("Brush.Surface0", window.ActualThemeVariant, out var surface));
            Assert.Equal(surface, toggle.Background);
            Assert.Null(window.FindControl<Control>("NeedsYouToggle"));
            Assert.Null(window.FindControl<Control>("PageAttention"));
            Assert.Null(window.FindControl<Control>("NextPendingInputButton"));
            Assert.Null(window.FindControl<Control>("RailChatNeedsYouMarker"));
            toggle.Command!.Execute(null);
            await Settle();
            window.UpdateLayout();
            var list = Assert.Single(root.GetVisualDescendants().OfType<ItemsControl>());
            Assert.Equal("NotificationList", list.Name);
            Assert.Same(main.NotificationChats, list.ItemsSource);
            Assert.True(main.NotificationChats[0].NeedsInput);
            await main.OpenUnreadChatCommand.ExecuteAsync(main.NotificationChats[0]);
            await Settle();
            window.UpdateLayout();
            Assert.Equal(0, main.SelectedNavIndex);
            Assert.Equal(pending.Id, main.ActiveChatId);
            Assert.True(main.HasPendingNotifications);
            var card = window.GetVisualDescendants().OfType<StrataQuestionCard>().Single();
            Assert.Equal("Keep every project visible?", card.Question);
            card.SelectedAnswer = "Yes";
            card.IsAnswered = true;
            await waiting;
            await Settle();
            Assert.False(main.HasPendingNotifications);
            Assert.True(main.HasUnreadChats);
            Assert.Equal("Unread reply", Assert.Single(main.NotificationChats).Title);
        }
        finally { window.Close(); Loc.Load("en"); sessions.Release(owner); }
    });

    [Fact]
    public Task PendingOnlyNotificationsStayWhenMarkedReadAndExpireAcrossWindows() => Run(async () =>
    {
        var chat = new Chat { Title = "Pending only" };
        var store = new DataStore(Data(chat));
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var owner = await sessions.AcquireChatAsync(chat);
        using var cancellation = new CancellationTokenSource();
        var waiting = owner.AskInputAsync(chat.Id, "form", Form, cancellation.Token);
        await Settle();
        using var anotherWindow = Main(store, registry, sessions);
        Assert.False(main.HasUnreadChats);
        Assert.True(main.HasNotifications);
        Assert.True(Assert.Single(main.NotificationChats).NeedsInput);
        Assert.True(Assert.Single(anotherWindow.NotificationChats).NeedsInput);
        main.ToggleUnreadPanelCommand.Execute(null);
        Assert.True(main.IsUnreadPanelOpen);
        main.MarkAllChatsReadCommand.Execute(null);
        Assert.True(main.HasPendingNotifications);
        Assert.Single(main.NotificationChats);
        var message = chat.Messages.Single(message => message.ToolName == "ask_input");
        Assert.False((await owner.RequestInputInteractionAsync(chat, message, "trip", "{}", "submit")).Accepted);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        await Settle();
        Assert.False(main.HasNotifications);
        Assert.False(anotherWindow.HasNotifications);
        Assert.Empty(main.NotificationChats);
        sessions.Release(owner);
    });

    [Fact]
    public Task ApprovalTimeAndOrderAreSharedAndOpeningDoesNotDecideTheRequest() => Run(async () =>
    {
        var questionChat = new Chat { Title = "Older question" };
        var approvalChat = new Chat { Title = "Newer approval", ApprovalMode = ApprovalMode.AskFirst };
        var store = new DataStore(Data(questionChat, approvalChat));
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var questionOwner = await sessions.AcquireChatAsync(questionChat);
        var approvalOwner = await sessions.AcquireChatAsync(approvalChat);
        var waiting = Ask(questionOwner, questionChat, "An actual question?");
        await Settle();
        var decisions = 0;
        var approval = approvalOwner.PresentPermissionRequest(approvalChat, "session", "request",
            new PermissionPromptRequestCommands
            {
                FullCommandText = "git status", Intention = "Inspect changes",
                CommandIdentifiers = ["git status"], CanOfferSessionApproval = true
            }, false, (_, _, _) => { decisions++; return Task.FromResult(true); })!;
        using var anotherWindow = Main(store, registry, sessions);
        Assert.Equal(["Older question", "Newer approval"], main.NotificationChats.Select(entry => entry.Title));
        var request = main.NotificationChats[1];
        Assert.Equal(approval.RequestedAt, request.PendingRequest!.RequestedAt);
        Assert.Equal(approval.RequestedAt, anotherWindow.NotificationChats[1].PendingRequest!.RequestedAt);
        Assert.Equal(approval.Title, request.Ask);
        Assert.DoesNotContain("git status", request.Ask);
        await main.OpenUnreadChatCommand.ExecuteAsync(request);
        Assert.Equal(0, decisions);
        Assert.True(approval.IsPending);
        Assert.Equal(2, main.NotificationChats.Count);
        await approval.DenyCommand.ExecuteAsync(null);
        Assert.Equal(1, decisions);
        Assert.Single(main.NotificationChats);
        Assert.Single(anotherWindow.NotificationChats);
        questionOwner.SubmitQuestionAnswer(main.NotificationChats[0].PendingRequest!.RequestId, "Yes");
        await waiting;
        await Settle();
        Assert.Empty(main.NotificationChats);
        sessions.Release(questionOwner);
        sessions.Release(approvalOwner);
    });

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    public Task UnreadOnlyCapAndFooterAreAppliedAfterPendingChatsAreExcluded(int totalUnread, int overflow) => Run(async () =>
    {
        var now = DateTimeOffset.UtcNow;
        var chats = Enumerable.Range(0, totalUnread).Select(index => new Chat
        {
            Title = $"Reply {index}", HasUnreadMessages = true, UpdatedAt = now.AddMinutes(-index)
        }).ToArray();
        var store = new DataStore(Data(chats));
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var owner = await sessions.AcquireChatAsync(chats[0]);
        var waiting = Ask(owner, chats[0], "One question overlaps the newest replies.");
        await Settle();
        chats[0].HasUnreadMessages = true;
        main.RefreshUnreadState();

        Assert.Equal(totalUnread, main.UnreadChatCount);
        Assert.Equal(totalUnread > 9 ? "9+" : "9", main.UnreadBadgeText);
        Assert.Equal(8, main.UnreadChats.Count);
        Assert.Equal(9, main.NotificationChats.Count);
        Assert.True(main.NotificationChats[0].NeedsInput);
        Assert.Equal(chats.Skip(1).Take(8).Select(chat => chat.Id), main.UnreadChats.Select(entry => entry.Chat.Id));
        Assert.Equal(overflow > 0, main.HasUnreadOverflow);
        Assert.Equal(overflow > 0 ? Loc.Get("Unread_MoreChats", overflow) : "", main.UnreadOverflowText);
        Assert.True(main.UnreadChats[0].StartsUnreadSection);
        Assert.Single(main.NotificationChats, entry => entry.StartsUnreadSection);

        owner.SubmitQuestionAnswer(main.NotificationChats[0].PendingRequest!.RequestId, "Yes");
        await waiting;
        await Settle();
        Assert.Equal(totalUnread, main.UnreadChatCount);
        Assert.Equal(8, main.NotificationChats.Count);
        Assert.Equal(chats[0].Id, main.NotificationChats[0].Chat.Id);
        Assert.DoesNotContain(main.NotificationChats, entry => entry.StartsUnreadSection);
        Assert.Equal(Loc.Get("Unread_MoreChats", totalUnread - 8), main.UnreadOverflowText);
        sessions.Release(owner);
    });

    [Fact]
    public Task PendingMetadataRefreshReusesSnapshotsAndPreservesAccessibleProjectDetails() => Run(async () =>
    {
        var project = new Project { Name = "Other project" };
        var filter = new Project { Name = "Draft project" };
        var chat = new Chat { Title = "Shared title", ProjectId = project.Id };
        var sameName = new Chat { Title = chat.Title, ProjectId = filter.Id };
        var data = Data(chat, sameName);
        data.Projects.AddRange([project, filter]);
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var owner = await sessions.AcquireChatAsync(chat);
        var waiting = Ask(owner, chat, "Which option should I choose?");
        await Settle();
        main.SelectedProjectFilter = filter.Id;
        await Settle();
        var entry = Assert.Single(main.NotificationChats);
        Assert.Contains(project.Name, entry.AskHint);
        Assert.Contains(Loc.Get("NeedsYou_Title"), entry.AccessibleName);
        Assert.Contains(project.Name, entry.AccessibleDetails);
        Assert.Contains(entry.TooltipText, entry.AccessibleDetails);
        Assert.Contains("hidden", entry.AccessibleDetails);
        Assert.Contains(entry.Ask!, entry.AccessibleDetails);
        var changes = 0;
        main.NotificationChats.CollectionChanged += (_, _) => changes++;

        main.AttentionInbox.Refresh();
        main.RefreshUnreadState();
        Assert.Same(entry, Assert.Single(main.NotificationChats));
        Assert.Equal(0, changes);
        chat.Title = "Updated full title";
        Assert.Equal(chat.Title, Assert.Single(main.NotificationChats).Title);
        project.Name = "Updated project";
        main.RefreshUnreadState();
        Assert.Contains(project.Name, Assert.Single(main.NotificationChats).AccessibleDetails);

        owner.SubmitQuestionAnswer(entry.PendingRequest!.RequestId, "Yes");
        await waiting;
        sessions.Release(owner);
    });

    [Theory]
    [InlineData("en", true, true)]
    [InlineData("en", true, false)]
    [InlineData("en", false, true)]
    [InlineData("en", false, false)]
    [InlineData("he", true, true)]
    [InlineData("he", true, false)]
    [InlineData("he", false, true)]
    [InlineData("he", false, false)]
    public Task NarrowSidebarKeepsRowsCompactAccessibleAndInOneViewport(string language, bool pending, bool unread) => Run(async () =>
    {
        Loc.Load(language);
        var project = new Project { Name = "Project for accessible details" };
        var chats = Enumerable.Range(0, 3).Select(index => new Chat
        {
            Title = $"Waiting conversation {index} with a long title", ProjectId = project.Id
        }).ToArray();
        var reply = new Chat { Title = "Unread conversation with a long title", ProjectId = project.Id, HasUnreadMessages = unread };
        var data = Data([.. chats, reply]);
        data.Projects.Add(project);
        data.Settings.Language = language;
        data.Settings.SidebarWidth = 240;
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var owners = new List<ChatViewModel>();
        var waiting = new List<Task<object?>>();
        if (pending)
        {
            foreach (var chat in chats)
            {
                var owner = await sessions.AcquireChatAsync(chat);
                owners.Add(owner);
                waiting.Add(Ask(owner, chat, "Which option should I choose? This longer ask must stay on one secondary line."));
            }
        }
        await Settle();
        var window = new MainWindow { DataContext = main, Width = 900, Height = 800 };
        window.Show();
        try
        {
            await Settle();
            window.UpdateLayout();
            var root = window.FindControl<Border>("UnreadInboxRoot")!;
            var toggle = window.FindControl<Button>("UnreadInboxToggle")!;
            Assert.Equal(pending || unread, root.IsEffectivelyVisible);
            Assert.True(Application.Current!.TryGetResource("Brush.Surface0", window.ActualThemeVariant, out var surface));
            Assert.Equal(surface, toggle.Background);
            if (pending || unread)
            {
                var dot = window.FindControl<Border>("NotificationStatusDot")!;
                Assert.True(Application.Current.TryGetResource(pending ? "Brush.WarningDefault" : "Brush.AccentDefault",
                    window.ActualThemeVariant, out var expectedDot));
                Assert.Equal(expectedDot, dot.Background);
                toggle.Command!.Execute(null);
                await Settle();
                window.UpdateLayout();
                var list = window.FindControl<ItemsControl>("NotificationList")!;
                var scroller = list.FindAncestorOfType<ScrollViewer>()!;
                var rows = list.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "NotificationRow").ToArray();
                Assert.Equal((pending ? 3 : 0) + (unread ? 1 : 0), rows.Length);
                Assert.True(list.Bounds.Height <= scroller.Viewport.Height + 1,
                    $"Notification content {list.Bounds.Height} exceeds viewport {scroller.Viewport.Height}.");
                foreach (var row in rows)
                {
                    var entry = Assert.IsType<UnreadChatEntry>(row.DataContext);
                    Assert.InRange(row.Bounds.Height, 28, 46);
                    Assert.Contains(entry.Title, AutomationProperties.GetName(row));
                    Assert.Contains(entry.StateLabel, AutomationProperties.GetName(row));
                    Assert.Contains(project.Name, AutomationProperties.GetHelpText(row));
                    Assert.Equal(entry.AccessibleDetails, AutomationProperties.GetHelpText(row));
                    var hint = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "NotificationAskHint");
                    Assert.Equal(TextWrapping.NoWrap, hint.TextWrapping);
                    Assert.Equal(1, hint.MaxLines);
                }
                Assert.Equal(pending && unread ? 1 : 0, list.GetVisualDescendants().OfType<Border>()
                    .Count(border => border.Name == "NotificationSectionDivider" && border.IsEffectivelyVisible));
            }
        }
        finally
        {
            window.Close();
            foreach (var owner in owners)
                foreach (var request in owner.GetPendingInputs())
                    owner.SubmitQuestionAnswer(request.RequestId, "Yes");
            await Task.WhenAll(waiting);
            foreach (var owner in owners)
                sessions.Release(owner);
            Loc.Load("en");
        }
    });

    private static MainViewModel Main(DataStore store, ChatSurfaceRegistry registry, ChatSessionStore sessions)
        => new(store, TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false,
            chatSurfaceRegistry: registry, chatSessionStore: sessions);

    private static Task<object?> Ask(ChatViewModel owner, Chat chat, string question)
    {
        var tool = (AIFunction)typeof(ChatViewModel).GetMethod("BuildAskQuestionTool",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [chat.Id])!;
        return tool.InvokeAsync(new AIFunctionArguments
        {
            ["question"] = question, ["options"] = new[] { "Yes", "No" },
            ["allowFreeText"] = true, ["allowMultiSelect"] = false
        }).AsTask();
    }

    private static AppData Data(params Chat[] chats) => new()
    {
        Chats = [.. chats], Settings = new()
        {
            Language = "en", IsOnboarded = true, ShowAnimations = false,
            AutoSaveChats = false, NotificationsEnabled = false, EnableMemoryAutoSave = false
        }
    };

    private static async Task Settle()
    {
        for (var i = 0; i < 4; i++)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task Run(Func<Task> test)
    {
        using var ui = HeadlessTestSession.Start();
        ExceptionDispatchInfo? failure = null;
        await ui.Dispatch(async () =>
        {
            try { Loc.Load("en"); await test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }, CancellationToken.None);
        failure?.Throw();
    }
}
