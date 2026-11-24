using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Remote;
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
    public Task CrossProjectRequestsOpenInTheirChatThenNextAndRestoreTheUnsentDraft() => Run(async () =>
    {
        var firstProject = new Project { Name = "Draft project" };
        var otherProject = new Project { Name = "Other project" };
        var questionChat = new Chat { Title = "Question", ProjectId = otherProject.Id };
        var formChat = new Chat { Title = "Form", ProjectId = otherProject.Id };
        var data = Data(questionChat, formChat);
        data.Projects.AddRange([firstProject, otherProject]);
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        main.SelectedProjectFilter = firstProject.Id;
        await Settle();
        main.NewChatCommand.Execute(null);
        const string draft = "Keep this exact unsent project draft.";
        main.ChatVM.PromptText = draft;
        var questionOwner = await sessions.AcquireChatAsync(questionChat);
        var formOwner = await sessions.AcquireChatAsync(formChat);
        var question = Ask(questionOwner, questionChat, "Which option?");
        var form = formOwner.AskInputAsync(formChat.Id, "form", Form, CancellationToken.None);
        await Settle();
        Assert.Equal(2, main.NeedsYouRequests.Count);
        var entry = main.NeedsYouRequests.Single(item => item.ChatId == questionChat.Id);
        await main.OpenPendingInputCommand.ExecuteAsync(entry);
        Assert.Equal(questionChat.Id, main.ActiveChatId);
        Assert.Equal(otherProject.Id, main.SelectedProjectFilter);
        Assert.Equal(entry.ItemId, main.ChatVM.PendingInputTarget);
        Assert.Single(main.NeedsYouRequests);
        Assert.Equal(2, main.AttentionVM.Requests.Count);
        questionOwner.SubmitQuestionAnswer(entry.RequestId, "Yes");
        Assert.Contains("Yes", (await question)?.ToString());
        await Settle();
        Assert.True(questionOwner.ShowPendingInputBar);
        Assert.True(questionOwner.HasNextPendingInput);
        questionOwner.NextPendingInputInChatsCommand.Execute(null);
        await Until(() => main.ActiveChatId == formChat.Id);
        Assert.Equal(formChat.Id, main.ActiveChatId);
        var message = formChat.Messages.Single(item => item.ToolName == "ask_input");
        Assert.Equal(InputRequestItem.StableIdFor(message.Id), main.ChatVM.PendingInputTarget);
        Assert.False((await formOwner.RequestInputInteractionAsync(formChat, message, "trip", "{}", "submit")).Accepted);
        Assert.Single(main.AttentionVM.Requests);
        Assert.True((await formOwner.RequestInputInteractionAsync(formChat, message, "trip",
            new InteractiveState { Values = { ["city"] = "Osaka" } }.ToJson(), "submit")).Accepted);
        Assert.Contains("Osaka", await form);
        await Settle();
        Assert.Empty(main.AttentionVM.Requests);
        Assert.DoesNotContain(questionChat.Messages, item => item.Role == "user");
        Assert.DoesNotContain(formChat.Messages, item => item.Role == "user");
        Assert.True(await main.TryNavigateHistoryAsync(-1));
        await Settle();
        Assert.True(await main.TryNavigateHistoryAsync(-1));
        await Settle();
        Assert.Null(main.ChatVM.CurrentChat);
        Assert.Equal(firstProject.Id, main.SelectedProjectFilter);
        Assert.Equal(draft, main.ChatVM.PromptText);
        sessions.Release(questionOwner);
        sessions.Release(formOwner);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public Task SplitCapsuleOpensOriginalQuestionAndNoAnsweringPageExists(string language) => Run(async () =>
    {
        Loc.Load(language);
        var chat = new Chat { Title = "Waiting chat", HasUnreadMessages = true };
        var data = Data(chat);
        data.Settings.Language = language;
        var store = new DataStore(data);
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var owner = await sessions.AcquireChatAsync(chat);
        chat.HasUnreadMessages = true;
        var waiting = Ask(owner, chat, "Keep every project visible?");
        await Settle();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 850 };
        window.Show();
        try
        {
            await Settle();
            window.UpdateLayout();
            var needs = window.FindControl<Button>("NeedsYouToggle")!;
            var unread = window.FindControl<Button>("UnreadInboxToggle")!;
            Assert.True(needs.IsEffectivelyVisible);
            Assert.True(Application.Current!.TryGetResource("Brush.WarningDefault", window.ActualThemeVariant, out var amber));
            Assert.Equal(amber, needs.Foreground);
            Assert.Equal(needs.TranslatePoint(default, window)!.Value.Y, unread.TranslatePoint(default, window)!.Value.Y, 1);
            Assert.Null(window.FindControl<Control>("PageAttention"));
            Assert.Null(window.FindControl<Control>("RailNeedsYou"));
            needs.Command!.Execute(null);
            Assert.True(main.IsNeedsYouPanelOpen);
            main.ToggleUnreadPanelCommand.Execute(null);
            Assert.False(main.IsNeedsYouPanelOpen);
            Assert.True(main.IsUnreadPanelOpen);
            needs.Command.Execute(null);
            Assert.False(main.IsUnreadPanelOpen);
            var request = Assert.Single(main.NeedsYouRequests);
            await main.OpenPendingInputCommand.ExecuteAsync(request);
            await Settle();
            window.UpdateLayout();
            Assert.Equal(0, main.SelectedNavIndex);
            Assert.Equal(chat.Id, main.ActiveChatId);
            Assert.Empty(main.NeedsYouRequests);
            Assert.Single(main.AttentionVM.Requests);
            var card = window.GetVisualDescendants().OfType<StrataQuestionCard>().Single();
            Assert.True(card.IsEffectivelyVisible);
            Assert.Equal("Keep every project visible?", card.Question);
            await Until(() => card.Classes.Contains("reply-flash"));
            var shell = window.GetVisualDescendants().OfType<StrataChatShell>().Single();
            var scroller = shell.GetVisualDescendants().OfType<ScrollViewer>().Single(item => item.Name == "PART_TranscriptScroll");
            await Until(() =>
            {
                var difference = card.TranslatePoint(default, scroller)!.Value.Y - shell.ReadingLineInset;
                var maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                return Math.Abs(difference) <= 2
                    || (difference < 0 && scroller.Offset.Y <= 1)
                    || (difference > 0 && Math.Abs(scroller.Offset.Y - maxOffset) <= 1);
            });
            Assert.InRange(card.TranslatePoint(default, scroller)!.Value.Y, 0, scroller.Viewport.Height - 1);
            Assert.True(window.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "ApprovalsWaitingBar").IsVisible);
            card.SelectedAnswer = "Yes";
            card.IsAnswered = true;
            await waiting;
            await Settle();
            Assert.Empty(main.AttentionVM.Requests);
        }
        finally { window.Close(); Loc.Load("en"); sessions.Release(owner); }
    });

    [Fact]
    public Task RequestsStayDistinctFromUnreadAndCancelAcrossWindows() => Run(async () =>
    {
        var first = new Chat { Title = "First", HasUnreadMessages = true };
        var second = new Chat { Title = "Second" };
        var store = new DataStore(Data(first, second));
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = first };
        registry.Attach(owner);
        using var inbox = new AttentionInboxViewModel(store, registry, events);
        using var cancellation = new CancellationTokenSource();
        var question = Ask(owner, first, "First?", cancellation.Token);
        var form = owner.AskInputAsync(second.Id, "form", Form, cancellation.Token);
        await Settle();
        using var later = new AttentionInboxViewModel(store, registry, events);
        Assert.Equal(2, inbox.Requests.Count);
        Assert.Equal(2, later.Requests.Count);
        first.HasUnreadMessages = false;
        events.PublishAttention(new(first.Id, ChatAttentionKind.Reply, "Not a pending request"));
        Assert.Equal(2, inbox.Requests.Count);
        owner.SubmitQuestionAnswer(inbox.Requests.Single(item => item.ChatId == first.Id).RequestId, "Yes");
        await question;
        await Settle();
        Assert.Single(inbox.Requests);
        Assert.Single(later.Requests);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await form);
        await Settle();
        Assert.Empty(inbox.Requests);
        Assert.Empty(later.Requests);
    });

    [Fact]
    public Task ApprovalTimestampOrderAndPhoneProjectionUseTheSameLiveRequests() => Run(async () =>
    {
        var questionChat = new Chat { Title = "Oldest question", UpdatedAt = DateTimeOffset.UtcNow };
        var approvalChat = new Chat { Title = "Newer approval", ApprovalMode = ApprovalMode.AskFirst, UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var unreadChat = new Chat { Title = "Unread only", HasUnreadMessages = true };
        var store = new DataStore(Data(questionChat, approvalChat, unreadChat));
        using var registry = new ChatSurfaceRegistry();
        using var sessions = new ChatSessionStore(store, TestCopilot.Shared, registry);
        using var main = Main(store, registry, sessions);
        var questionOwner = await sessions.AcquireChatAsync(questionChat);
        var approvalOwner = await sessions.AcquireChatAsync(approvalChat);
        var waiting = Ask(questionOwner, questionChat, "An actual question?");
        await Settle();
        var approval = approvalOwner.PresentPermissionRequest(approvalChat, "session", "same-id",
            new PermissionPromptRequestCommands
            {
                FullCommandText = "git status", Intention = "Inspect changes",
                CommandIdentifiers = ["git status"], CanOfferSessionApproval = true
            }, false, (_, _, _) => Task.FromResult(true))!;
        var first = main.AttentionVM.Requests.Single(item => item.ChatId == approvalChat.Id);
        using var anotherWindow = new AttentionInboxViewModel(store, registry, sessions.ChatEvents);
        using var anotherMainWindow = Main(store, registry, sessions);
        Assert.Equal(2, anotherMainWindow.NeedsYouRequests.Count);
        Assert.Equal(approval.RequestedAt, first.RequestedAt);
        Assert.Equal(first.RequestedAt, anotherWindow.Requests.Single(item => item.ChatId == approvalChat.Id).RequestedAt);
        Assert.Equal(["Oldest question", "Newer approval"], main.AttentionVM.Requests.Select(item => item.ChatTitle));
        var phone = RemoteProjector.BuildNewChatExperience(store, main.ChatVM);
        Assert.Equal(main.AttentionVM.Requests.Select(item => item.ChatId), phone.NeedsYouChats.Select(item => item.ChatId));
        Assert.Equal(2, phone.NeedsYouCount);
        Assert.All(phone.NeedsYouChats, item => Assert.True(item.IsWaiting));
        Assert.Contains("An actual question?", phone.NeedsYouChats[0].Meta);
        Assert.Contains("git status", phone.NeedsYouChats[1].Meta);
        await main.OpenPendingInputCommand.ExecuteAsync(first);
        Assert.Equal(approvalChat.Id, main.ActiveChatId);
        Assert.Equal(first.ItemId, approvalOwner.PendingInputTarget);
        Assert.True(approval.IsPending);
        await approval.DenyCommand.ExecuteAsync(null);
        Assert.Single(main.AttentionVM.Requests);
        Assert.Single(anotherWindow.Requests);
        questionOwner.SubmitQuestionAnswer(main.AttentionVM.Requests[0].RequestId, "Yes");
        await waiting;
        await Settle();
        Assert.Empty(main.AttentionVM.Requests);
        sessions.Release(questionOwner);
        sessions.Release(approvalOwner);
    });

    private static MainViewModel Main(DataStore store, ChatSurfaceRegistry registry, ChatSessionStore sessions)
        => new(store, TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false,
            chatSurfaceRegistry: registry, chatSessionStore: sessions);

    private static Task<object?> Ask(ChatViewModel owner, Chat chat, string question, CancellationToken token = default)
    {
        var tool = (AIFunction)typeof(ChatViewModel).GetMethod("BuildAskQuestionTool",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [chat.Id])!;
        return tool.InvokeAsync(new AIFunctionArguments
        {
            ["question"] = question, ["options"] = new[] { "Yes", "No" },
            ["allowFreeText"] = true, ["allowMultiSelect"] = false
        }, token).AsTask();
    }

    private static AppData Data(params Chat[] chats) => new()
    {
        Chats = [.. chats], Settings = new()
        {
            Language = "en", IsOnboarded = true, ShowAnimations = false,
            AutoSaveChats = false, NotificationsEnabled = false, EnableMemoryAutoSave = false
        }
    };

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready())
            await Task.Delay(10, timeout.Token);
        await Settle();
    }

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
