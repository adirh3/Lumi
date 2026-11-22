using System.Reflection;
using System.Runtime.ExceptionServices;
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
using Microsoft.Copilot;
using Microsoft.Extensions.AI;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AttentionInboxTests
{
    private const string Form = """{"id":"trip","title":"Trip preferences","fields":[{"id":"city","label":"City","required":true}]}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SelectedSidebarChatReturnsFromNeedsYouWithoutOpeningAnotherWindow(bool useKeyboard) => Run(async () =>
    {
        var chat = new Chat { Title = "Selected chat" };
        var store = Store(chat);
        store.Data.Settings.IsOnboarded = true;
        store.Data.Settings.ShowAnimations = false;
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false);
        var detached = 0;
        main.OpenChatWindowRequested += _ => detached++;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 850 };
        window.Show();
        try
        {
            Assert.True(await main.OpenChatByIdAsync(chat.Id));
            await Settle();
            main.OpenAttentionInboxCommand.Execute(null);
            await Settle();
            window.UpdateLayout();
            var row = window.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => ReferenceEquals(item.DataContext, chat));
            Assert.True(row.IsSelected);
            if (useKeyboard)
            {
                Assert.True(row.Focus(NavigationMethod.Tab));
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            }
            else
            {
                var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }
            await Settle();
            Assert.Equal(0, main.SelectedNavIndex);
            Assert.Equal(chat.Id, main.ActiveChatId);
            Assert.Equal(0, detached);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ApprovalRequestTimeAndContextStayTheSameWhenAnotherWindowOpensLater() => Run(async () =>
    {
        var chat = new Chat { ApprovalMode = ApprovalMode.AskFirst };
        var store = Store(chat);
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = chat };
        registry.Attach(owner);
        using var first = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        var approval = owner.PresentPermissionRequest(chat, "session", "approval-time",
            new PermissionPromptRequestCommands
            {
                FullCommandText = "git status", Intention = "Inspect changes",
                CommandIdentifiers = ["git status"], CanOfferSessionApproval = true
            }, false, (_, _, _) => Task.FromResult(true))!;
        var original = Assert.Single(first.Requests);
        await original.ContextReady;
        chat.Messages.Add(new()
        {
            Role = "assistant", Content = "This happened after the approval was requested.",
            Timestamp = approval.RequestedAt.AddMinutes(1)
        });
        await Task.Delay(10);
        using var second = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        var later = Assert.Single(second.Requests);
        await later.ContextReady;
        Assert.True(DateTimeOffset.UtcNow > approval.RequestedAt);
        Assert.Equal(approval.RequestedAt, original.RequestedAt);
        Assert.Equal(original.RequestedAt, later.RequestedAt);
        Assert.Equal(original.AskedAt, later.AskedAt);
        Assert.DoesNotContain(later.Context, entry => entry.Text.Contains("after the approval"));
        owner.CancelPermissionRequests(chat.Id);
    });

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public Task BannerOpensTheInteractivePageAndTheNativeCardClearsIt(string language) => Run(async () =>
    {
        Loc.Load(language);
        var chat = new Chat { Title = "Question owner", HasUnreadMessages = true };
        var store = Store(chat);
        store.Data.Settings.Language = language;
        store.Data.Settings.IsOnboarded = true;
        store.Data.Settings.ShowAnimations = false;
        using var main = new MainViewModel(store, TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false);
        main.ChatVM.CurrentChat = chat;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 850 };
        window.Show();
        try
        {
            var waiting = Ask(main.ChatVM, chat, "Keep every project visible?");
            await Settle();
            window.UpdateLayout();
            var banner = window.FindControl<Button>("NeedsYouBanner")!;
            var unread = window.FindControl<Border>("UnreadInboxRoot")!;
            Assert.True(banner.IsEffectivelyVisible);
            Assert.Equal(Loc.Get("NeedsYou_Banner", 1), main.AttentionVM.BannerText);
            Assert.True(Application.Current!.TryGetResource("Brush.DangerDefault", window.ActualThemeVariant, out var danger));
            Assert.Equal(danger, banner.Foreground);
            Assert.True(banner.TranslatePoint(default, window)!.Value.Y < unread.TranslatePoint(default, window)!.Value.Y);

            banner.Command!.Execute(null);
            await Settle();
            window.UpdateLayout();
            Assert.Equal(MainViewModel.AttentionNavIndex, main.SelectedNavIndex);
            Assert.Equal(0, MainWindow.SidebarForPage(main.SelectedNavIndex));
            var page = window.GetVisualDescendants().OfType<AttentionInboxView>().Single();
            var card = page.GetVisualDescendants().OfType<StrataQuestionCard>().Single();
            Assert.True(card.IsEffectivelyVisible);
            Assert.Equal("Keep every project visible?", card.Question);
            card.SelectedAnswer = "Yes";
            card.IsAnswered = true;
            await waiting;
            await Settle();
            window.UpdateLayout();
            Assert.Empty(main.AttentionVM.Requests);
            Assert.False(banner.IsVisible);
            Assert.True(page.FindControl<StackPanel>("NeedsYouEmpty")!.IsEffectivelyVisible);
            Assert.True(await main.TryNavigateHistoryAsync(-1));
            Assert.Equal(0, main.SelectedNavIndex);
        }
        finally
        {
            window.Close();
            Loc.Load("en");
        }
    });

    [Fact]
    public Task QuestionsFromOtherChatsAreAnsweredOnceWithoutMovingOrSendingANewMessage() => Run(async () =>
    {
        var first = new Chat { Title = "First", ProjectId = Guid.NewGuid(), HasUnreadMessages = true };
        var second = new Chat { Title = "Second", ProjectId = Guid.NewGuid() };
        var store = Store(first, second);
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = first };
        registry.Attach(owner);
        using var inbox = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        var a = Ask(owner, first, "First question?");
        var b = Ask(owner, second, "Second question?");
        await Settle();

        Assert.Equal(2, inbox.Requests.Count);
        events.PublishAttention(new(first.Id, ChatAttentionKind.Reply, "Unread reply"));
        first.HasUnreadMessages = false;
        inbox.Refresh();
        Assert.Equal(2, inbox.Requests.Count);
        var target = inbox.Requests.Single(request => request.ChatId == second.Id);
        var question = Assert.IsType<QuestionItem>(target.Item);
        question.Submit("Yes");
        Assert.Contains("Yes", (await b)?.ToString());
        await Settle();

        Assert.Single(inbox.Requests);
        Assert.False(a.IsCompleted);
        Assert.Same(first, owner.CurrentChat);
        Assert.DoesNotContain(second.Messages, message => message.Role == "user");
        Assert.False(owner.TryAnswerPendingInput(first.Id, question.QuestionId, "No"));
        Assert.False(owner.TryAnswerPendingInput(second.Id, question.QuestionId, "No"));
        Assert.IsType<QuestionItem>(inbox.Requests[0].Item).Submit("No");
        await a;
        await Settle();
        Assert.False(inbox.HasRequests);
    });

    [Fact]
    public Task FormValidationPauseAndCancellationUseTheOriginalRequest() => Run(async () =>
    {
        var chat = new Chat();
        var store = Store(chat);
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = chat };
        registry.Attach(owner);
        using var cancellation = new CancellationTokenSource();
        var waiting = owner.AskInputAsync(chat.Id, "form", Form, cancellation.Token);
        await Settle();
        // A window opened after the event must still see the live request.
        using var inbox = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        var request = Assert.Single(inbox.Requests);
        Assert.False((await request.RequestInteractionAsync("trip", "{}", "submit")).Accepted);
        Assert.False(waiting.IsCompleted);
        var state = new InteractiveState { Values = { ["city"] = "Osaka" } }.ToJson();
        chat.IsPaused = true;
        Assert.False(request.CanAnswer);
        Assert.False((await request.RequestInteractionAsync("trip", state, "submit")).Accepted);
        chat.IsPaused = false;
        Assert.True((await request.RequestInteractionAsync("trip", state, null)).Accepted);
        Assert.Single(inbox.Requests);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        await Settle();
        Assert.Empty(inbox.Requests);
        Assert.False((await request.RequestInteractionAsync("trip", state, "submit")).Accepted);
    });

    [Fact]
    public Task SubmissionAndAnsweringInTheChatRemoveTheRequestFromEveryInbox() => Run(async () =>
    {
        var chat = new Chat();
        var store = Store(chat);
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = chat };
        registry.Attach(owner);
        using var first = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        using var second = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        var form = owner.AskInputAsync(chat.Id, "form", Form, CancellationToken.None);
        await Settle();
        var request = Assert.Single(first.Requests);
        Assert.Single(second.Requests);
        Assert.True((await request.RequestInteractionAsync("trip",
            new InteractiveState { Values = { ["city"] = "Osaka" } }.ToJson(), "submit")).Accepted);
        Assert.Contains("Osaka", await form);
        await Settle();
        Assert.Empty(first.Requests);
        Assert.Empty(second.Requests);

        var question = Ask(owner, chat, "Continue?");
        await Settle();
        owner.SubmitQuestionAnswer(first.Requests[0].RequestId, "Yes");
        await question;
        await Settle();
        Assert.Empty(first.Requests);
        Assert.Empty(second.Requests);
    });

    [Fact]
    public Task ContextDescribesOnlyTheLeadInAndOpeningTheChatDoesNotDismissTheQuestion() => Run(async () =>
    {
        var chat = new Chat { Title = "Trip" };
        chat.Messages.Add(new() { Role = "user", Content = "Find a quiet place in Osaka." });
        chat.Messages.Add(new() { Role = "assistant", Content = "I found two stays, but need your dates." });
        chat.Messages.Add(new() { Role = "reasoning", Content = "PRIVATE_REASONING" });
        chat.Messages.Add(new()
        {
            Role = "tool", ToolName = "powershell", ToolStatus = "Completed",
            Content = """{"description":"Compare nightly prices","command":"fixture"}""",
            ToolOutput = "PRIVATE_TOOL_OUTPUT"
        });
        var store = Store(chat);
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = chat };
        registry.Attach(owner);
        Chat? opened = null;
        using var inbox = new AttentionInboxViewModel(store, registry, events,
            target => { opened = target; return Task.FromResult(true); });
        var waiting = Ask(owner, chat, "Which dates?");
        await Settle();
        var request = Assert.Single(inbox.Requests);
        await request.ContextReady;
        Assert.Contains("quiet place in Osaka", request.Summary);
        Assert.Contains("two stays", request.Summary);
        Assert.Contains("Compare nightly prices", request.Summary);
        Assert.DoesNotContain(request.Context, entry => entry.Text.Contains("PRIVATE_REASONING"));
        Assert.DoesNotContain(request.Context, entry => entry.Text.Contains("PRIVATE_TOOL_OUTPUT"));
        chat.Messages.Add(new() { Role = "assistant", Content = "AFTER_QUESTION", Timestamp = DateTimeOffset.UtcNow.AddMinutes(1) });
        await request.LoadContextCommand.ExecuteAsync(null);
        Assert.DoesNotContain(request.Context, entry => entry.Text.Contains("AFTER_QUESTION"));
        await request.OpenChatCommand.ExecuteAsync(null);
        Assert.Same(chat, opened);
        Assert.Single(inbox.Requests);
        Assert.IsType<QuestionItem>(request.Item).Submit("Yes");
        await waiting;
    });

    [Fact]
    public Task ApprovalDecisionsStayWithTheirOwnerAndOnlyRemoveTheSettledRequest() => Run(async () =>
    {
        var first = new Chat { ApprovalMode = ApprovalMode.AskFirst };
        var second = new Chat { ApprovalMode = ApprovalMode.AskFirst };
        var store = Store(first, second);
        var events = new ChatEventHub();
        using var registry = new ChatSurfaceRegistry();
        using var owner = new ChatViewModel(store, TestCopilot.Shared, chatEvents: events) { CurrentChat = first };
        registry.Attach(owner);
        using var inbox = new AttentionInboxViewModel(store, registry, events, _ => Task.FromResult(true));
        var answers = 0;
        var prompt = new PermissionPromptRequestCommands
        {
            FullCommandText = "git status", Intention = "Check the worktree", CommandIdentifiers = ["git status"],
            CanOfferSessionApproval = true
        };
        var a = owner.PresentPermissionRequest(first, "session", "same-id", prompt, false,
            (_, _, _) => { answers++; return Task.FromResult(true); })!;
        owner.PresentPermissionRequest(second, "session", "same-id", prompt, false,
            (_, _, _) => throw new InvalidOperationException("Wrong owner."));
        Assert.Equal(2, inbox.Requests.Count);
        await Assert.IsType<ApprovalItem>(inbox.Requests.Single(entry => entry.ChatId == first.Id).Item)
            .Request.AllowOnceCommand.ExecuteAsync(null);
        Assert.Equal(1, answers);
        Assert.Equal(ApprovalOutcome.Allowed, a.Outcome);
        Assert.Equal(second.Id, Assert.Single(inbox.Requests).ChatId);
        owner.CancelPermissionRequests(second.Id);
        Assert.Empty(inbox.Requests);
    });

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

    private static DataStore Store(params Chat[] chats) => new(new AppData
    {
        Chats = [.. chats],
        Settings = new() { Language = "en", AutoSaveChats = false, NotificationsEnabled = false, EnableMemoryAutoSave = false }
    });

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
