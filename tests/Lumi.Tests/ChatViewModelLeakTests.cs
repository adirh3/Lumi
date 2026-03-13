using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelLeakTests
{
    [Fact]
    public void ReleaseInactiveChatState_ClearsDetachedChatResources()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var inactiveChat = new Chat { Title = "inactive" };
        inactiveChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(inactiveChat);
        vm.CurrentChat = activeChat;

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[inactiveChat.Id] = new ChatRuntimeState
        {
            Chat = inactiveChat,
            HasUsedBrowser = true
        };
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[inactiveChat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[inactiveChat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[inactiveChat.Id] =
            new ChatMessage { Role = "assistant", Content = "streaming" };
        GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats").Add(inactiveChat.Id);
        GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat")[inactiveChat.Id] = Guid.NewGuid();
        GetField<Dictionary<Guid, BrowserService>>(vm, "_chatBrowserServices")[inactiveChat.Id] = new BrowserService();

        InvokePrivate(vm, "ReleaseInactiveChatState", inactiveChat, true);

        Assert.Empty(inactiveChat.Messages);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats").Contains(inactiveChat.Id));
        Assert.DoesNotContain(inactiveChat.Id, GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat").Keys);
        Assert.False(GetField<Dictionary<Guid, BrowserService>>(vm, "_chatBrowserServices").ContainsKey(inactiveChat.Id));
    }

    [Fact]
    public void ReleaseInactiveChatState_LeavesBusyChatAttached()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var busyChat = new Chat { Title = "busy" };
        busyChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(busyChat);
        vm.CurrentChat = activeChat;

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[busyChat.Id] = new ChatRuntimeState
        {
            Chat = busyChat,
            IsBusy = true
        };
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[busyChat.Id] = subscription;

        InvokePrivate(vm, "ReleaseInactiveChatState", busyChat, true);

        Assert.Single(busyChat.Messages);
        Assert.Equal(0, subscription.DisposeCount);
        Assert.True(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(busyChat.Id));
        Assert.True(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(busyChat.Id));
    }

    [Fact]
    public void ReleaseInactiveChatState_DoesNotCreateRuntimeStateForUnknownChat()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var detachedChat = new Chat { Title = "detached" };
        detachedChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(detachedChat);
        vm.CurrentChat = activeChat;

        InvokePrivate(vm, "ReleaseInactiveChatState", detachedChat, true);

        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(detachedChat.Id));
    }

    [Fact]
    public void CancelPendingQuestions_RemovesTrackedQuestionTasks()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "question-chat" };
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "ask_question", QuestionId = "q-1" });
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "ask_question", QuestionId = "q-2" });

        var pendingQuestions = GetField<Dictionary<string, TaskCompletionSource<string>>>(vm, "_pendingQuestions");
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingQuestions["q-1"] = first;
        pendingQuestions["q-2"] = second;

        InvokePrivate(vm, "CancelPendingQuestions", chat);

        Assert.True(first.Task.IsCanceled);
        Assert.True(second.Task.IsCanceled);
        Assert.Empty(pendingQuestions);
    }

    private static DataStore CreateDataStore()
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        });

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));

    private static void InvokePrivate(object instance, string name, params object[] args)
    {
        instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
