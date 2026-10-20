using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class PolishedInteractionTests
{
    private const string Form = """{"id":"trip","title":"Trip preferences","fields":[{"id":"destination","label":"Destination","required":true},{"id":"budget","label":"Budget","type":"number","required":true,"minimum":0}]}""";
    private const string Results = """{"id":"stays","title":"Choose a stay","items":[{"id":"garden","label":"Garden stay"},{"id":"city","label":"City stay"}]}""";
    private const string Checklist = """{"id":"prep","title":"Prep","items":[{"id":"milk","label":"Milk"},{"id":"eggs","label":"Eggs"}]}""";

    [Fact]
    public Task FormResumesTheWaitingTaskWithoutAUserMessageAndPreservesTheDraft() => Run(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat], Settings = new() { Language = "en" } });
        using var vm = new ChatViewModel(store, TestCopilot.Shared);
        vm.CurrentChat = chat;
        vm.PromptText = "Keep this unrelated draft";
        vm.PendingAttachments.Add(@"C:\sample.txt");
        vm.IsBusy = true;
        var waiting = vm.AskInputAsync(chat.Id, "form", Form, CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        var request = Assert.Single(chat.Messages);
        Assert.Equal("ask_input", request.ToolName);
        Assert.True(vm.IsAwaitingInput);
        Assert.False(waiting.IsCompleted);
        var state = InteractiveState.Create(InteractiveDefinition.Parse("form", Form));
        state.Values["destination"] = "Osaka";
        state.Values["budget"] = "1200";

        var submitted = await vm.RequestInputInteractionAsync(chat, request, "trip", state.ToJson(), "submit");
        Assert.True(submitted.Accepted, submitted.Error);
        var answer = await waiting;
        Assert.Contains("Osaka", answer);
        Assert.Contains("1200", answer);
        Assert.Single(chat.Messages);
        Assert.DoesNotContain(chat.Messages, message => message.Role == "user");
        Assert.Equal("Keep this unrelated draft", vm.PromptText);
        Assert.Single(vm.PendingAttachments);
        Assert.False(vm.IsAwaitingInput);
        Assert.True(vm.IsBusy);
        Assert.Equal("Submitted", new InputRequestItem(request).InputStatus);
    });

    [Fact]
    public Task InvalidInputAndUnknownActionsDoNotReleaseTheRequest() => Run(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        using var cancellation = new CancellationTokenSource();
        var waiting = vm.AskInputAsync(chat.Id, "form", Form, cancellation.Token);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        var request = Assert.Single(chat.Messages);
        var state = InteractiveState.Create(InteractiveDefinition.Parse("form", Form));
        Assert.False((await vm.RequestInputInteractionAsync(chat, request, "trip", state.ToJson(), "submit")).Accepted);
        Assert.False(waiting.IsCompleted);
        Assert.False((await vm.RequestInputInteractionAsync(chat, request, "trip", state.ToJson(), "compare")).Accepted);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    });

    [Fact]
    public Task CancelReturnsToTheSameTaskAndMakesTheRequestReadOnly() => Run(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        var waiting = vm.AskInputAsync(chat.Id, "results", Results, CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        var request = Assert.Single(chat.Messages);
        var state = InteractiveState.Create(InteractiveDefinition.Parse("results", Results));
        Assert.True((await vm.RequestInputInteractionAsync(chat, request, "stays", state.ToJson(), "cancel")).Accepted);
        Assert.Contains("canceled", await waiting);
        Assert.True(request.InputCanceled);
        Assert.False(new InputRequestItem(request).CanSubmit);
        Assert.False((await vm.RequestInputInteractionAsync(chat, request, "stays", state.ToJson(), "use")).Accepted);
        Assert.Single(chat.Messages);
    });

    [Fact]
    public Task ResultSelectionReturnsKnownIdsWithoutStartingAComparisonTurn() => Run(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        var waiting = vm.AskInputAsync(chat.Id, "results", Results, CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        var request = Assert.Single(chat.Messages);
        var state = new InteractiveState { SelectedIds = ["city"] };
        Assert.True((await vm.RequestInputInteractionAsync(chat, request, "stays", state.ToJson(), "use")).Accepted);
        Assert.Contains("\"city\"", await waiting);
        Assert.Single(chat.Messages);
    });

    [Fact]
    public Task StopCancelsPendingInputSynchronouslyAndKeepsItsDraft() => Run(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        var waiting = vm.AskInputAsync(chat.Id, "form", Form, CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        var request = Assert.Single(chat.Messages);
        var state = InteractiveState.Create(InteractiveDefinition.Parse("form", Form));
        state.Values["destination"] = "Osaka";
        Assert.True((await vm.RequestInputInteractionAsync(chat, request, "trip", state.ToJson(), null)).Accepted);
        typeof(ChatViewModel).GetMethod("CancelPendingQuestions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [chat]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsAwaitingInput);
        Assert.False(vm.OwnsLiveChat(chat.Id));
        Assert.Equal("Expired", new InputRequestItem(request).InputStatus);
        Assert.Contains("Osaka", request.InteractionStates!["trip"]);
        Assert.Single(chat.Messages);
    });

    [Fact]
    public void ReceiptFreezesValuesAndCloneDoesNotShareLifecycleState()
    {
        var definition = InteractiveDefinition.Parse("form", Form);
        var state = InteractiveState.Create(definition);
        state.Values["destination"] = "Osaka";
        state.Values["budget"] = "1200";
        var source = new ChatMessage { Role = "assistant" };
        var receipt = InteractiveResponseState.CreateReceipt(source, definition, state, "submit", "en");
        var message = new ChatMessage { InteractionReceipt = receipt };
        state.Values["budget"] = "2500";
        Assert.Contains("Budget: " + InteractiveFormat.Number("1200", null), receipt.Detail);
        Assert.DoesNotContain(InteractiveFormat.Number("2500", null), receipt.Detail);
        Assert.Equal(source.Id, receipt.SourceMessageId);
        var clone = message.Clone();
        clone.InteractionReceipt!.Status = "Failed";
        Assert.Equal("Submitted", receipt.Status);
        Assert.Equal(receipt.Detail, clone.InteractionReceipt.Detail);
    }

    [Fact]
    public Task EditsToAStillStreamingAnswerAreKept() => Run(async () =>
    {
        var chat = new Chat();
        var store = new DataStore(new AppData { Chats = [chat] });
        using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        var streaming = new ChatMessage { Role = "assistant", IsStreaming = true, Content = $"Prep:\n\n```checklist\n{Checklist}\n```\n\nStill writing" };
        ((Dictionary<Guid, ChatMessage>)typeof(ChatViewModel)
            .GetField("_inProgressMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!)[chat.Id] = streaming;
        var result = await vm.RequestInteractionAsync(streaming.Id, "prep", new InteractiveState { CheckedIds = ["milk"] }.ToJson(), null);
        Assert.True(result.Accepted, result.Error);
        Assert.Contains("milk", streaming.InteractionStates!["prep"]);
    });

    [Fact]
    public Task LostConnectionSettlesAWorkingReceipt() => Run(() =>
    {
        var chat = new Chat();
        var action = new ChatMessage { Role = "user", Content = "Compare", InteractionReceipt = new() { Status = "Working" } };
        chat.Messages.Add(action);
        var store = new DataStore(new AppData { Chats = [chat] });
        using var vm = new ChatViewModel(store, TestCopilot.Shared) { CurrentChat = chat };
        ((Dictionary<Guid, ChatRuntimeState>)typeof(ChatViewModel)
            .GetField("_runtimeStates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!)[chat.Id] =
            new ChatRuntimeState { Chat = chat, IsBusy = true, InteractionReceiptMessageId = action.Id };
        typeof(ChatViewModel).GetMethod("ResetAfterCopilotReconnect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        Assert.Equal("Stopped", action.InteractionReceipt!.Status);
        Assert.Equal(InteractionHistory.InterruptedReceiptError, action.InteractionReceipt.Error);
        return Task.CompletedTask;
    });

    [Fact]
    public void ReopeningInterruptedHistoryDoesNotClaimAReplyWasCompleted()
    {
        var chat = new Chat();
        var action = new ChatMessage { InteractionReceipt = new() { Status = "Working", Detail = "Budget: 1200" } };
        var input = new ChatMessage { Role = "tool", ToolName = "ask_input", InputKind = "form", InputDefinition = Form, ToolStatus = "InProgress" };
        chat.Messages.Add(action);
        chat.Messages.Add(input);
        Assert.True(InteractionHistory.SettleInterrupted(chat));
        Assert.Equal("Stopped", action.InteractionReceipt!.Status);
        Assert.Equal("Expired", new InputRequestItem(input).InputStatus);
        Assert.False(InteractionHistory.SettleInterrupted(chat));
    }

    [Fact]
    public void ActualRemoteSnapshotCarriesReceiptsAndStructuredInput()
    {
        var chat = new Chat { IsAwaitingInput = true };
        var input = new ChatMessage
        {
            Role = "tool", ToolName = "ask_input", QuestionId = "pending", InputKind = "form",
            InputDefinition = Form, ToolStatus = "InProgress",
            InteractionStates = new() { ["trip"] = InteractiveState.Create(InteractiveDefinition.Parse("form", Form)).ToJson() }
        };
        var action = new ChatMessage
        {
            Role = "user", Content = "Full model payload",
            InteractionReceipt = new() { SourceMessageId = Guid.NewGuid(), Title = "Compare 2 results", Detail = "Exact choices", Status = "Working", Action = "compare" }
        };
        chat.Messages.Add(action);
        chat.Messages.Add(input);
        var snapshots = chat.Messages.Select(LumiRemoteServer.CloneMessageForRemote).ToList();
        var transcript = RemoteProjector.BuildTranscript(chat, snapshots,
            new() { ChatId = chat.Id, IsAwaitingInput = true }, true, true, 1);
        Assert.True(transcript.Status.IsAwaitingInput);
        var items = transcript.Turns.SelectMany(turn => turn.Items).ToList();
        Assert.Equal("Compare 2 results", items.First().InteractionReceipt!.Title);
        Assert.Equal("compare", items.First().InteractionReceipt!.Action);
        Assert.Equal("Waiting", items.Last().InputRequest!.Status);
        Assert.Equal(RemoteProtocol.ItemKinds.Input, items.Last().Kind);
    }

    [Fact]
    public void ForkKeepsTheSnapshotButRepointsTheOriginalAndEndsLiveWork()
    {
        var chat = new Chat();
        var source = new ChatMessage { Role = "assistant", Content = "Original result" };
        var action = new ChatMessage
        {
            Role = "user", Content = "Compare",
            InteractionReceipt = new() { SourceMessageId = source.Id, Status = "Working", Detail = "Exact selection" }
        };
        var fork = ChatForkFactory.CreateFork(chat, [source, action]).Chat;
        Assert.Equal(fork.Messages[0].Id, fork.Messages[1].InteractionReceipt!.SourceMessageId);
        Assert.Equal("Stopped", fork.Messages[1].InteractionReceipt!.Status);
        Assert.Equal("Working", action.InteractionReceipt.Status);
    }

    [Fact]
    public Task ClickingADayInTheCalendarChoosesItAndClosesTheSheet() => Run(async () =>
    {
        var host = new Grid { Width = 412, Height = 650 };
        var control = new StrataInteractiveBlock
        {
            Definition = InteractiveDefinition.Parse("form",
                """{"id":"trip","title":"Trip","fields":[{"id":"date","label":"Departure","type":"date","value":"2026-10-23"}]}"""),
            TouchMode = true, PickerHost = host
        };
        var saves = 0;
        control.Requested += (_, request) => { if (request.Action is null) saves++; };
        host.Children.Add(control);
        var window = new Window { Width = 760, Height = 800, Content = host };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            control.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Field_date")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(300);
            window.UpdateLayout();
            var day = host.GetVisualDescendants().OfType<CalendarDayButton>()
                .Single(button => button.DataContext is DateTime date && date == new DateTime(2026, 10, 27));
            var center = day.TranslatePoint(new Point(day.Bounds.Width / 2, day.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, saves);
            Assert.Equal("2026-10-27", InteractiveState.Parse(control.StateJson!).Values["date"]);
            Assert.Empty(host.Children.OfType<StrataBottomSheet>());
        }
        finally { window.Close(); }
    });

    private static async Task Run(Func<Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp), AvaloniaTestIsolationLevel.PerTest);
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            try { Loc.Load("en"); await action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }, CancellationToken.None);
        failure?.Throw();
    }
}
