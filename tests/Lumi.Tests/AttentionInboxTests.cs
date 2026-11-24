using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
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
            Assert.Contains("needs-input", toggle.Classes);
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
        Assert.Contains("git status", request.Ask);
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
