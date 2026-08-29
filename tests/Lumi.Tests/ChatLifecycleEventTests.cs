using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using System.Reflection;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatLifecycleEventTests
{
    [Fact]
    public void PublishTerminalChatLifecycleEventOnce_DeduplicatesPerTurnAndEventType()
    {
        var chat = new Chat { Title = "Worker" };
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "First turn" });
        var store = new DataStore(new AppData { Chats = [chat] });
        var events = new List<ChatLifecycleEvent>();
        var hub = new ChatEventHub();
        hub.EventPublished += events.Add;
        using var viewModel = new ChatViewModel(store, TestCopilot.Shared, chatEvents: hub)
        {
            CurrentChat = chat
        };

        viewModel.BeginChatLifecycleTurn(chat);
        chat.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Queued second turn",
            SteerDelivery = MessageSteerState.Queued
        });
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnStart);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnStart);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Error, "First");
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Error, "Duplicate");
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);

        viewModel.BeginChatLifecycleTurn(chat);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Error, "Second");

        Assert.Collection(
            events,
            chatEvent => Assert.Equal(ChatLifecycleEventTypes.TurnStart, chatEvent.EventType),
            chatEvent => Assert.Equal((ChatLifecycleEventTypes.Error, "First"), (chatEvent.EventType, chatEvent.Detail)),
            chatEvent => Assert.Equal(ChatLifecycleEventTypes.TurnEnd, chatEvent.EventType),
            chatEvent => Assert.Equal(ChatLifecycleEventTypes.Idle, chatEvent.EventType),
            chatEvent => Assert.Equal((ChatLifecycleEventTypes.Error, "Second"), (chatEvent.EventType, chatEvent.Detail)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void IsTopLevelAssistantTurn_RejectsNestedSubagentTurns(int activeSubagentDepth, bool expected)
        => Assert.Equal(expected, ChatViewModel.IsTopLevelAssistantTurn(activeSubagentDepth));

    [Fact]
    public void AssistantTurnBoundaryTracker_RecognizesParentEndWhileSubagentRemainsActive()
    {
        var tracker = new AssistantTurnBoundaryTracker();

        Assert.True(tracker.Begin("parent", activeSubagentDepth: 0));
        Assert.False(tracker.Begin("nested", activeSubagentDepth: 1));
        Assert.False(tracker.End("nested"));
        Assert.True(tracker.End("parent"));
    }

    [Fact]
    public void TurnEndBeforeIdle_IdleFallbackDoesNotDuplicateTurnEnd()
    {
        var chat = new Chat { Title = "Worker" };
        var events = new List<ChatLifecycleEvent>();
        var hub = new ChatEventHub();
        hub.EventPublished += events.Add;
        using var viewModel = new ChatViewModel(
            new DataStore(new AppData { Chats = [chat] }),
            TestCopilot.Shared,
            chatEvents: hub);

        viewModel.BeginChatLifecycleTurn(chat);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.TurnEnd);
        viewModel.PublishTerminalChatLifecycleEventOnce(chat, ChatLifecycleEventTypes.Idle);

        Assert.Collection(
            events,
            chatEvent => Assert.Equal(ChatLifecycleEventTypes.TurnEnd, chatEvent.EventType),
            chatEvent => Assert.Equal(ChatLifecycleEventTypes.Idle, chatEvent.EventType));
    }

    [Fact]
    public async Task TryStopGenerationAsync_WithoutSession_PublishesAbortedForActiveTurn()
    {
        var chat = new Chat { Title = "Starting" };
        var store = new DataStore(new AppData { Chats = [chat] });
        var events = new List<ChatLifecycleEvent>();
        var hub = new ChatEventHub();
        hub.EventPublished += events.Add;
        using var viewModel = new ChatViewModel(store, TestCopilot.Shared, chatEvents: hub)
        {
            CurrentChat = chat
        };
        viewModel.BeginChatLifecycleTurn(chat);
        var cancellationSources = (Dictionary<Guid, CancellationTokenSource>)(typeof(ChatViewModel)
            .GetField("_ctsSources", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(viewModel)
            ?? throw new InvalidOperationException("Cancellation source map was not found."));
        cancellationSources[chat.Id] = new CancellationTokenSource();

        await viewModel.TryStopGenerationAsync();

        var chatEvent = Assert.Single(events);
        Assert.Equal(ChatLifecycleEventTypes.Aborted, chatEvent.EventType);
        Assert.DoesNotContain(chat.Id, cancellationSources.Keys);
    }

    [Fact]
    public void RemoveCanceledPreSendMessage_RemovesUnsentPromptFromTranscript()
    {
        var message = new ChatMessage { Role = "user", Content = "Never sent" };
        var chat = new Chat { Title = "Starting", Messages = [message] };
        var store = new DataStore(new AppData { Chats = [chat] });
        using var viewModel = new ChatViewModel(store, TestCopilot.Shared)
        {
            CurrentChat = chat
        };
        viewModel.Messages.Add(new ChatMessageViewModel(message));
        var method = typeof(ChatViewModel).GetMethod(
            "RemoveCanceledPreSendMessage",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RemoveCanceledPreSendMessage was not found.");

        method.Invoke(viewModel, [chat, message]);

        Assert.Empty(chat.Messages);
        Assert.Empty(viewModel.Messages);
    }
}
