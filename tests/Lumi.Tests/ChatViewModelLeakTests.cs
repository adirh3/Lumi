using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatViewModelLeakTests
{
    [Fact]
    public void ReleaseInactiveChatState_ReleasesDetachedRuntimeResourcesWithoutEvictingMessages()
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
            Chat = inactiveChat
        };
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[inactiveChat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[inactiveChat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[inactiveChat.Id] =
            new ChatMessage { Role = "assistant", Content = "streaming" };
        GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats").Add(inactiveChat.Id);
        GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat")[inactiveChat.Id] = Guid.NewGuid();
        GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices")[inactiveChat.Id] = new BrowserService();

        InvokePrivate(vm, "ReleaseInactiveChatState", inactiveChat);

        Assert.Single(inactiveChat.Messages);
        Assert.Equal("cached", inactiveChat.Messages[0].Content);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(inactiveChat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(inactiveChat.Id));
        Assert.DoesNotContain(inactiveChat.Id, GetField<HashSet<Guid>>(vm, "_suggestionGenerationInFlightChats"));
        Assert.DoesNotContain(inactiveChat.Id, GetField<Dictionary<Guid, Guid>>(vm, "_lastSuggestedAssistantMessageByChat").Keys);
        // Browser sessions belong to the chat's lifetime, not its transient runtime state, so they
        // survive an inactive-state release and are reattached when the user switches back.
        Assert.True(GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices").ContainsKey(inactiveChat.Id));
    }

    [Fact]
    public void BrowserService_SurvivesInactiveReleaseButIsDisposedOnCleanup()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var browserChat = new Chat { Title = "browser" };
        browserChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "kept" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(browserChat);
        vm.CurrentChat = activeChat;

        var services = GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices");
        services[browserChat.Id] = new BrowserService();
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[browserChat.Id] =
            new ChatRuntimeState { Chat = browserChat };

        // Going inactive releases the runtime state but preserves the browser session so the user
        // can return to the chat and find the browser exactly as they left it.
        InvokePrivate(vm, "ReleaseInactiveChatState", browserChat);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(browserChat.Id));
        Assert.True(services.ContainsKey(browserChat.Id));

        // Deleting the chat tears the browser session down for good.
        vm.CleanupSession(browserChat.Id);
        Assert.False(services.ContainsKey(browserChat.Id));

        vm.Dispose();
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

        InvokePrivate(vm, "ReleaseInactiveChatState", busyChat);

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

        InvokePrivate(vm, "ReleaseInactiveChatState", detachedChat);

        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(detachedChat.Id));
    }

    [Fact]
    public void DropCompletedTurnState_RemovesStaleLiveOwnershipMarkersAfterIdle()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "completed" };
        dataStore.Data.Chats.Add(chat);

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat
        };
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[chat.Id] = new CancellationTokenSource();
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[chat.Id] =
            new ChatMessage { Role = "assistant", Content = "done" };

        Assert.True(vm.OwnsLiveChat(chat.Id));

        InvokePrivate(vm, "DropCompletedTurnState", chat.Id, false);

        Assert.True(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.True(vm.OwnsLiveChat(chat.Id));

        InvokePrivate(vm, "DropCompletedTurnState", chat.Id, true);

        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.False(vm.OwnsLiveChat(chat.Id));
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

    [Fact]
    public void IsChatBusy_ReturnsTrueWhileTurnCleanupIsPending()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "pending-chat" };

        dataStore.Data.Chats.Add(chat);
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            PendingSessionUserMessageCount = 1
        };

        Assert.True(vm.IsChatBusy(chat.Id));
    }

    [Fact]
    public void IsChatBusy_ReturnsTrueWhileToolIsStillTracked()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tool-chat" };

        dataStore.Data.Chats.Add(chat);
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            ActiveToolCount = 1
        };

        Assert.True(vm.IsChatBusy(chat.Id));
    }

    [Fact]
    public void MarkRuntimeActive_SetsRunningFlagForPreSendWork()
    {
        var chat = new Chat { Title = "worktree-chat" };
        var runtime = new ChatRuntimeState { Chat = chat };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeActive", runtime, "Creating worktree", true, false);

        Assert.True(runtime.IsBusy);
        Assert.True(runtime.IsStreaming);
        Assert.True(chat.IsRunning);
        Assert.Equal("Creating worktree", runtime.StatusText);
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_KeepsRunningWhileTurnIsTracked()
    {
        var chat = new Chat { Title = "agent-chat" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            PendingSessionUserMessageCount = 1,
            StatusText = "Running agent"
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.True(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.True(chat.IsRunning);
        Assert.Equal("Running agent", runtime.StatusText);
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_KeepsRunningWhileBackgroundWorkIsPending()
    {
        var chat = new Chat { Title = "background-chat" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsStreaming = true,
            HasPendingBackgroundWork = true
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.True(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.True(runtime.HasPendingBackgroundWork);
        Assert.True(chat.IsRunning);
    }

    [Fact]
    public void MarkRuntimeWaitingForSessionIdle_ClearsWhenNoWorkRemains()
    {
        var chat = new Chat { Title = "complete-chat" };
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            StatusText = "Finishing"
        };

        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeWaitingForSessionIdle", runtime);

        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(chat.IsRunning);
        Assert.Equal("", runtime.StatusText);
    }

    [Fact]
    public void FinalizeTerminalAssistantMessage_AddsCompletedStreamingMessage()
    {
        var chat = new Chat { Title = "complete-chat" };
        var streamingMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "final answer",
            IsStreaming = true
        };

        var added = ChatViewModel.FinalizeTerminalAssistantMessage(chat, streamingMessage);

        Assert.True(added);
        Assert.False(streamingMessage.IsStreaming);
        Assert.Same(streamingMessage, Assert.Single(chat.Messages));
    }

    [Fact]
    public void FinalizeTerminalAssistantMessage_DoesNotPersistEmptyStreamingMessage()
    {
        var chat = new Chat { Title = "complete-chat" };
        var streamingMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "   ",
            IsStreaming = true
        };

        var added = ChatViewModel.FinalizeTerminalAssistantMessage(chat, streamingMessage);

        Assert.False(added);
        Assert.False(streamingMessage.IsStreaming);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public void FinalizeTerminalAssistantMessage_DoesNotDuplicateExistingMessage()
    {
        var chat = new Chat { Title = "complete-chat" };
        var streamingMessage = new ChatMessage
        {
            Role = "assistant",
            Content = "final answer",
            IsStreaming = true
        };
        chat.Messages.Add(streamingMessage);

        var added = ChatViewModel.FinalizeTerminalAssistantMessage(chat, streamingMessage);

        Assert.True(added);
        Assert.False(streamingMessage.IsStreaming);
        Assert.Single(chat.Messages);
    }

    [Fact]
    public void FinalizeTerminalReasoningMessage_ClearsStreamingState()
    {
         var reasoningMessage = new ChatMessage
        {
            Role = "reasoning",
            Content = "thinking",
            IsStreaming = true
        };

        ChatViewModel.FinalizeTerminalReasoningMessage(reasoningMessage);

        Assert.False(reasoningMessage.IsStreaming);
    }

    [Fact]
    public async Task SendMessage_WhenChatRuntimeActive_QueuesPromptAndClearsComposer()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "busy-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.PromptText = "queued while busy";
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true
        };

        await InvokePrivateAsync(vm, "SendMessage");

        Assert.Empty(chat.Messages);
        Assert.Equal("", vm.PromptText);
        Assert.Equal(
            "queued while busy",
            GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts")[chat.Id]);
    }

    [Fact]
    public async Task SendMessageCore_WhenQueuedPromptFindsRuntimeStillActive_DoesNotOverwriteDraft()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "busy-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.PromptText = "new draft";
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true
        };

        await InvokePrivateAsync(vm, "SendMessageCore", "queued prompt", false);

        Assert.Empty(chat.Messages);
        Assert.Equal("new draft", vm.PromptText);
        Assert.Equal(
            "queued prompt",
            GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts")[chat.Id]);
    }

    [Fact]
    public async Task DrainQueuedBusySendAsync_WhenChatChanged_PreservesQueuedPromptAsDraft()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var queuedChat = new Chat { Title = "queued-chat" };
        var visibleChat = new Chat { Title = "visible-chat" };

        dataStore.Data.Chats.Add(queuedChat);
        dataStore.Data.Chats.Add(visibleChat);
        vm.CurrentChat = visibleChat;
        GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts")[queuedChat.Id] = "send me later";

        await InvokePrivateAsync(vm, "DrainQueuedBusySendAsync", queuedChat.Id);

        Assert.False(GetField<Dictionary<Guid, string>>(vm, "_queuedBusySendPrompts").ContainsKey(queuedChat.Id));
        Assert.Equal("send me later", GetField<Dictionary<Guid, string>>(vm, "_chatDrafts")[queuedChat.Id]);
        Assert.Empty(queuedChat.Messages);
    }

    [Fact]
    public void MarkInProgressToolsStopped_StopsPersistedAndLiveToolMessages()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tool-chat" };
        var runningTool = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "tool-1",
            ToolStatus = "InProgress"
        };
        var completedTool = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "tool-2",
            ToolStatus = "Completed"
        };
        chat.Messages.Add(runningTool);
        chat.Messages.Add(completedTool);

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        var runningVm = new ChatMessageViewModel(runningTool);
        var completedVm = new ChatMessageViewModel(completedTool);
        vm.Messages.Add(runningVm);
        vm.Messages.Add(completedVm);

        var changed = InvokePrivate<bool>(vm, "MarkInProgressToolsStopped", chat);

        Assert.True(changed);
        Assert.Equal("Stopped", runningTool.ToolStatus);
        Assert.Equal("Stopped", runningVm.ToolStatus);
        Assert.Equal("Completed", completedTool.ToolStatus);
        Assert.Equal("Completed", completedVm.ToolStatus);
    }

    [Fact]
    public void TranscriptBuilder_RendersStoppedToolAsTerminal()
    {
        var dataStore = CreateDataStore();
        dataStore.Data.Settings.ShowToolCalls = true;
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var toolMessage = new ChatMessage
        {
            Role = "tool",
            ToolName = "powershell",
            ToolCallId = "tool-1",
            ToolStatus = "Stopped",
            Content = "{\"command\":\"Start-Sleep -Seconds 45\"}"
        };

        vm.Messages.Add(new ChatMessageViewModel(toolMessage));

        var turn = Assert.Single(vm.TranscriptTurns);
        var group = Assert.IsType<ToolGroupItem>(Assert.Single(turn.Items));
        var terminal = Assert.IsType<TerminalPreviewItem>(Assert.Single(group.ToolCalls));
        Assert.False(group.IsActive);
        Assert.Equal(StrataTheme.Controls.StrataAiToolCallStatus.Stopped, terminal.Status);
    }

    [Fact]
    public void ResetAfterCopilotReconnect_ClearsTransientRuntimeState()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "recoverable-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };

        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources")[chat.Id] = new CancellationTokenSource();
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;
        GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages")[chat.Id] =
            new ChatMessage { Role = "assistant", Content = "partial" };

        InvokePrivate(vm, "ResetAfterCopilotReconnect");

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, CancellationTokenSource>>(vm, "_ctsSources").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(GetField<Dictionary<Guid, ChatMessage>>(vm, "_inProgressMessages").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.Equal("", runtime.StatusText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void ReleaseInactiveChatState_CleansUpChatAfterRemoteShutdownClearsBackgroundWork()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var activeChat = new Chat { Title = "active" };
        var detachedChat = new Chat { Title = "detached", CopilotSessionId = "session-456" };
        detachedChat.Messages.Add(new ChatMessage { Role = "assistant", Content = "cached" });

        dataStore.Data.Chats.Add(activeChat);
        dataStore.Data.Chats.Add(detachedChat);
        vm.CurrentChat = activeChat;

        var runtime = new ChatRuntimeState
        {
            Chat = detachedChat,
            HasPendingBackgroundWork = true
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[detachedChat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[detachedChat.Id] = subscription;

        InvokePrivate(vm, "DetachSessionAfterRemoteShutdown", detachedChat, false);
        InvokePrivate(vm, "ReleaseInactiveChatState", detachedChat);

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates").ContainsKey(detachedChat.Id));
        Assert.Single(detachedChat.Messages);
    }

    [Fact]
    public void DetachSessionAfterRemoteShutdown_PreservesPersistedSessionId()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "recoverable-chat",
            CopilotSessionId = "session-123"
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;

        InvokePrivate(vm, "DetachSessionAfterRemoteShutdown", chat, true);

        Assert.Equal("session-123", chat.CopilotSessionId);
        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.Equal("", runtime.StatusText);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsStreaming);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public void InvalidateCurrentSession_ClearsPersistedSessionId()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "fresh-session",
            CopilotSessionId = "session-123"
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;

        InvokePrivate(vm, "InvalidateCurrentSession");

        Assert.Null(chat.CopilotSessionId);
    }

    [Fact]
    public void HandleSendError_AddsSingleTranscriptErrorItem()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "error-chat" };
        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;

        InvokePrivate(
            vm,
            "HandleSendError",
            new InvalidOperationException("Copilot request failed"),
            false,
            null!,
            chat);

        Assert.Single(chat.Messages);
        Assert.Single(vm.Messages);

        var turn = Assert.Single(vm.TranscriptTurns);
        var errorItem = Assert.IsType<ErrorMessageItem>(Assert.Single(turn.Items));
        Assert.Contains("Copilot request failed", errorItem.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCopilotTransportError_DetectsJsonRpcDisconnect()
    {
        var ex = new Exception(
            "Communication error with Copilot CLI",
            new IOException("The JSON-RPC connection with the remote party was lost before the request could complete."));

        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "IsCopilotTransportError", ex);

        Assert.True(result);
    }

    [Fact]
    public void IsCopilotTransportError_IgnoresUnrelatedExceptions()
    {
        var ex = new InvalidOperationException("Session not found");

        var result = InvokePrivateStatic<bool>(typeof(ChatViewModel), "IsCopilotTransportError", ex);

        Assert.False(result);
    }

    [Fact]
    public void ShouldAutoResendTransportSend_WhenServerIsMissingLatestUserTurn()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("first")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.False(analysis.UserMessageObserved);
    }

    [Fact]
    public void ShouldAutoResendTransportSend_WhenServerAlreadyRecordedLatestUserTurn()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("first"),
            CreateUserMessageEvent("second")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 2);

        Assert.True(analysis.UserMessageObserved);
    }

    [Fact]
    public void GetRecoveredAssistantMessages_ReturnsOnlyMissingTopLevelMessages()
    {
        IReadOnlyList<SessionEvent> events =
        [
            CreateUserMessageEvent("continue"),
            CreateAssistantMessageEvent("msg-1", "First reply"),
            CreateAssistantMessageEvent("tool-1", "Tool transcript", parentToolCallId: "call-1"),
            CreateAssistantMessageEvent("msg-2", "Second reply")
        ];

        var analysis = PendingTurnRecoveryAnalyzer.Analyze(events, expectedSessionUserMessageCount: 1);
        var result = analysis.AssistantMessages.Skip(1).ToList();

        Assert.Single(result);
        Assert.Equal("Second reply", result[0].Content);
    }

    [Fact]
    public void AttachSourcesToFinalAssistantMessage_UsesOnlyLatestAssistantAfterUserTurn()
    {
        var previousAssistant = new ChatMessage { Role = "assistant", Content = "Previous answer" };
        var firstAssistant = new ChatMessage { Role = "assistant", Content = "I will look that up." };
        var finalAssistant = new ChatMessage { Role = "assistant", Content = "Here is the final answer." };
        var chat = new Chat();
        chat.Messages.Add(previousAssistant);
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Find current info" });
        chat.Messages.Add(firstAssistant);
        chat.Messages.Add(new ChatMessage { Role = "tool", ToolName = "web_search", Content = "{}" });
        chat.Messages.Add(finalAssistant);

        var updatedMessage = InvokePrivateStaticNullable<ChatMessage>(
            typeof(ChatViewModel),
            "AttachSourcesToFinalAssistantMessage",
            chat,
            new List<SearchSource>
            {
                new()
                {
                    Title = "Example Domain",
                    Url = "https://example.com/",
                    Snippet = "Example snippet"
                }
            });

        Assert.Same(finalAssistant, updatedMessage);
        Assert.Empty(previousAssistant.Sources);
        Assert.Empty(firstAssistant.Sources);
        var source = Assert.Single(finalAssistant.Sources);
        Assert.Equal("https://example.com/", source.Url);
    }

    [Fact]
    public void AttachSourcesToFinalAssistantMessage_DoesNotLeakToPreviousTurn()
    {
        var previousAssistant = new ChatMessage { Role = "assistant", Content = "Previous answer" };
        var chat = new Chat();
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "Earlier question" });
        chat.Messages.Add(previousAssistant);
        chat.Messages.Add(new ChatMessage { Role = "user", Content = "New question" });

        var updatedMessage = InvokePrivateStaticNullable<ChatMessage>(
            typeof(ChatViewModel),
            "AttachSourcesToFinalAssistantMessage",
            chat,
            new List<SearchSource>
            {
                new()
                {
                    Title = "Example Domain",
                    Url = "https://example.com/",
                    Snippet = "Example snippet"
                }
            });

        Assert.Null(updatedMessage);
        Assert.Empty(previousAssistant.Sources);
    }

    [Fact]
    public void BuildSessionRecoveryReplayPrompt_IncludesRetainedTranscriptAndLatestMessage()
    {
        var retainedContext = new List<ChatMessage>
        {
            new() { Role = "system", Content = "System context" },
            new() { Role = "user", Content = "Earlier question" },
            new() { Role = "assistant", Content = "Earlier answer" },
            new() { Role = "tool", Content = "Ignored tool output" }
        };

        var prompt = InvokePrivateStatic<string>(
            typeof(ChatViewModel),
            "BuildSessionRecoveryReplayPrompt",
            retainedContext,
            "Latest question");

        Assert.Contains("The previous backend chat session is unavailable.", prompt);
        Assert.Contains("System: System context", prompt);
        Assert.Contains("User: Earlier question", prompt);
        Assert.Contains("Assistant: Earlier answer", prompt);
        Assert.DoesNotContain("Ignored tool output", prompt);
        Assert.Contains("Latest user message:", prompt);
        Assert.Contains("Latest question", prompt);
    }

    [Fact]
    public void ShouldReplayTranscriptAfterSessionReset_WhenSessionIsRecreated_ReturnsTrue()
    {
        var result = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "ShouldReplayTranscriptAfterSessionReset",
            false,
            "session-1",
            "session-2",
            2);

        Assert.True(result);
    }

    [Fact]
    public void ShouldReplayTranscriptAfterSessionReset_WhenSessionIsReused_ReturnsFalse()
    {
        var result = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "ShouldReplayTranscriptAfterSessionReset",
            false,
            "session-1",
            "session-1",
            2);

        Assert.False(result);
    }

    [Fact]
    public void BuildResendPrompt_AppendsPromptAdditions_ForEditedResend()
    {
        var retainedContext = new List<ChatMessage>
        {
            new() { Role = "user", Content = "Earlier question" }
        };
        const string promptAdditions = "\n\n[Activated skill context]";

        var prompt = InvokePrivateStatic<string>(
            typeof(ChatViewModel),
            "BuildResendPrompt",
            retainedContext,
            "Edited question",
            true,
            false,
            promptAdditions);

        Assert.Contains("Latest user message (edited):", prompt);
        Assert.Contains("Edited question", prompt);
        Assert.EndsWith(promptAdditions, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResendPrompt_AppendsPromptAdditions_ForRecoveredResend()
    {
        var retainedContext = new List<ChatMessage>
        {
            new() { Role = "assistant", Content = "Earlier answer" }
        };
        const string promptAdditions = "\n\n[Workspace skill instructions]";

        var prompt = InvokePrivateStatic<string>(
            typeof(ChatViewModel),
            "BuildResendPrompt",
            retainedContext,
            "Retry question",
            false,
            true,
            promptAdditions);

        Assert.Contains("The previous backend chat session is unavailable.", prompt);
        Assert.Contains("Retry question", prompt);
        Assert.EndsWith(promptAdditions, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparePendingTurnTracking_ClearsManualStopRequested()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tracked-chat" };

        dataStore.Data.Chats.Add(chat);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            ManualStopRequested = true
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;

        InvokePrivate(vm, "PreparePendingTurnTracking", chat, 1, 0);

        Assert.False(runtime.ManualStopRequested);
    }

    [Fact]
    public void AdjustPendingToolCount_ReconcilesWhenLastTrackedToolCompletes()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tracked-chat" };

        dataStore.Data.Chats.Add(chat);

        InvokePrivate(vm, "PreparePendingTurnTracking", chat, 1, 0);

        var started = InvokePrivate<bool>(vm, "AdjustPendingToolCount", chat.Id, 1);
        var completed = InvokePrivate<bool>(vm, "AdjustPendingToolCount", chat.Id, -1);
        var runtime = GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id];

        Assert.False(started);
        Assert.True(completed);
        Assert.Equal(0, runtime.ActiveToolCount);
    }

    [Fact]
    public void RefreshActiveMcpSelections_RebuildsFromRenameAndDeleteWithoutStaleEntries()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat
        {
            Title = "mcp-chat",
            ActiveMcpServerNames = ["filesystem", "filesystem", "legacy", "missing"]
        };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.ActiveMcpServerNames.AddRange(chat.ActiveMcpServerNames);
        vm.AvailableMcpChips.Add(new StrataTheme.Controls.StrataComposerChip("local-filesystem", "📁"));
        vm.AvailableMcpChips.Add(new StrataTheme.Controls.StrataComposerChip("workspace", "🧰"));

        var changed = InvokePrivate<bool>(
            vm,
            "RefreshActiveMcpSelections",
            new FeatureChangeResult(
                "updated",
                DataChanged: true,
                RenamedMcpOldName: "filesystem",
                RenamedMcpNewName: "local-filesystem",
                DeletedMcpName: "legacy"));

        Assert.True(changed);
        Assert.Equal(["local-filesystem"], vm.ActiveMcpServerNames);
        var chip = Assert.Single(vm.ActiveMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>());
        Assert.Equal("local-filesystem", chip.Name);
        Assert.Equal(["local-filesystem"], chat.ActiveMcpServerNames);
    }

    [Fact]
    public void ConsumeManualStopRequested_ReturnsTrueOnlyOnce()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "tracked-chat" };

        dataStore.Data.Chats.Add(chat);

        InvokePrivate(vm, "SetManualStopRequested", chat.Id, true);

        var first = InvokePrivate<bool>(vm, "ConsumeManualStopRequested", chat.Id);
        var second = InvokePrivate<bool>(vm, "ConsumeManualStopRequested", chat.Id);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void BuildCustomAgents_IncludesActiveAgentForSessionRegistration()
    {
        var dataStore = CreateDataStore();
        var activeAgent = new LumiAgent
        {
            Name = "Active agent",
            Description = "Selected before send",
            SystemPrompt = "You are active."
        };
        var otherAgent = new LumiAgent
        {
            Name = "Other agent",
            Description = "Available in catalog",
            SystemPrompt = "You are other."
        };

        dataStore.Data.Agents.Add(activeAgent);
        dataStore.Data.Agents.Add(otherAgent);

        var vm = new ChatViewModel(dataStore, new CopilotService());
        vm.SetActiveAgent(activeAgent);

        var configs = InvokePrivate<List<CustomAgentConfig>>(vm, "BuildCustomAgents", new object[] { null! });

        Assert.Contains(configs, cfg => cfg.Name == activeAgent.Name);
        Assert.Contains(configs, cfg => cfg.Name == otherAgent.Name);
    }

    [Fact]
    public void BuildCustomAgents_RegistersDiscoveredExternalAgentsAsDelegatableSubagents()
    {
        var dataStore = CreateDataStore();
        dataStore.Data.Agents.Add(new LumiAgent
        {
            Name = "Lumi agent",
            Description = "Built-in persona",
            SystemPrompt = "You are a Lumi agent."
        });

        var vm = new ChatViewModel(dataStore, new CopilotService());

        var catalog = new ProjectContextCatalogSnapshot(
            new[] { new CopilotSkillDefinition("SomeSkill", "desc", "content", @"C:\repo\.github\skills\some\SKILL.md") },
            new[]
            {
                new CopilotAgentDefinition("WebReviewer", "Reviews web app code", "You review TypeScript code.", @"C:\repo\.github\agents\reviewer\AGENT.md"),
                new CopilotAgentDefinition("Lumi agent", "External duplicate", "External duplicate body.", @"C:\repo\.github\agents\dup\AGENT.md"),
                new CopilotAgentDefinition("Blank", "No body", "   ", @"C:\repo\.github\agents\blank\AGENT.md"),
            },
            Array.Empty<ProjectContextMcpServerDefinition>());

        var configs = InvokePrivate<List<CustomAgentConfig>>(vm, "BuildCustomAgents", new object[] { catalog });

        // External .github/agents agent becomes a delegatable subagent using its AGENT.md body as the prompt.
        var webReviewer = configs.Single(cfg => cfg.Name == "WebReviewer");
        Assert.Equal("You review TypeScript code.", webReviewer.Prompt);
        Assert.Equal("Reviews web app code", webReviewer.Description);

        // Lumi's own agent wins a name collision; the external duplicate is not added.
        var lumiMatches = configs.Where(cfg => cfg.Name == "Lumi agent").ToList();
        Assert.Single(lumiMatches);
        Assert.Equal("You are a Lumi agent.", lumiMatches[0].Prompt);

        // External agents with blank content are skipped.
        Assert.DoesNotContain(configs, cfg => cfg.Name == "Blank");
    }

    [Fact]
    public void ApplyUnexpectedAbortState_ResetsRuntimeAndDetachesCachedSession()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "abort-chat" };

        dataStore.Data.Chats.Add(chat);
        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;
        var subscription = new CountingDisposable();
        GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs")[chat.Id] = subscription;

        InvokePrivate(vm, "ApplyUnexpectedAbortState", chat, "Connection to Copilot was lost.", true);

        Assert.Equal(1, subscription.DisposeCount);
        Assert.False(GetField<Dictionary<Guid, IDisposable>>(vm, "_sessionSubs").ContainsKey(chat.Id));
        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.Equal("Connection to Copilot was lost.", runtime.StatusText);
    }

    [Fact]
    public void ApplyUnexpectedAbortState_CanSkipDisplayedChatUiCleanup()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "abort-chat" };

        dataStore.Data.Chats.Add(chat);
        vm.CurrentChat = chat;
        vm.IsBusy = true;
        vm.IsStreaming = true;
        vm.StatusText = "busy";

        var runtime = new ChatRuntimeState
        {
            Chat = chat,
            IsBusy = true,
            IsStreaming = true,
            HasPendingBackgroundWork = true,
            StatusText = "busy"
        };
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = runtime;

        InvokePrivate(vm, "ApplyUnexpectedAbortState", chat, "Connection to Copilot was lost.", false);

        Assert.False(runtime.IsBusy);
        Assert.False(runtime.IsStreaming);
        Assert.False(runtime.HasPendingBackgroundWork);
        Assert.Equal("Connection to Copilot was lost.", runtime.StatusText);
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsStreaming);
        Assert.Equal("busy", vm.StatusText);
    }

    [Fact]
    public void ShouldMarkBackgroundWorkPending_ReturnsTrueWhileTurnIsStillTracked()
    {
        var runtime = new ChatRuntimeState
        {
            PendingSessionUserMessageCount = 1
        };

        var shouldMark = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);

        Assert.True(shouldMark);
    }

    [Fact]
    public void ShouldMarkBackgroundWorkPending_ReturnsFalseAfterIdleCleanup()
    {
        var runtime = new ChatRuntimeState
        {
            PendingSessionUserMessageCount = 0,
            ActiveToolCount = 0,
            IsBusy = false,
            IsStreaming = false
        };

        var shouldMark = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);

        Assert.False(shouldMark);
    }

    [Fact]
    public void ShouldMarkBackgroundWorkPending_IgnoresStaleBusyAfterTurnTrackingClears()
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = true
        };

        var shouldMark = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);

        Assert.False(shouldMark);
    }

    [Fact]
    public void BackgroundTasksChangedAfterIdleCleanup_DoesNotRestickPendingBackgroundWork()
    {
        var dataStore = CreateDataStore();
        var vm = new ChatViewModel(dataStore, new CopilotService());
        var chat = new Chat { Title = "background-chat" };

        dataStore.Data.Chats.Add(chat);
        GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id] = new ChatRuntimeState
        {
            Chat = chat,
            HasPendingBackgroundWork = true
        };

        InvokePrivate(vm, "PreparePendingTurnTracking", chat, 1, 0);

        var runtime = GetField<Dictionary<Guid, ChatRuntimeState>>(vm, "_runtimeStates")[chat.Id];
        runtime.IsBusy = false;
        runtime.IsStreaming = false;

        var shouldMarkBeforeIdle = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);
        Assert.True(shouldMarkBeforeIdle);

        InvokePrivate(vm, "ClearPendingTurnTracking", chat.Id);
        InvokePrivateStatic(typeof(ChatViewModel), "MarkRuntimeTerminal", runtime, null!);

        var shouldMarkAfterIdle = InvokePrivateStatic<bool>(typeof(ChatViewModel), "ShouldMarkBackgroundWorkPending", runtime);
        if (shouldMarkAfterIdle)
            runtime.HasPendingBackgroundWork = true;

        Assert.False(shouldMarkAfterIdle);
        Assert.False(runtime.HasPendingBackgroundWork);
    }

    [Fact]
    public void ShouldRecoverCompletedTurnIfIdleIsMissing_ReturnsTrueForTextOnlyTurnEnd()
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = false,
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = 0,
            HasPendingBackgroundWork = false
        };

        var shouldRecover = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "ShouldRecoverCompletedTurnIfIdleIsMissing",
            runtime);

        Assert.True(shouldRecover);
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 0, true)]
    public void ShouldRecoverCompletedTurnIfIdleIsMissing_ReturnsFalseWhileWorkRemains(
        bool isStreaming,
        int activeToolCount,
        bool hasPendingBackgroundWork)
    {
        var runtime = new ChatRuntimeState
        {
            IsBusy = true,
            IsStreaming = isStreaming,
            PendingSessionUserMessageCount = 1,
            ActiveToolCount = activeToolCount,
            HasPendingBackgroundWork = hasPendingBackgroundWork
        };

        var shouldRecover = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "ShouldRecoverCompletedTurnIfIdleIsMissing",
            runtime);

        Assert.False(shouldRecover);
    }

    [Fact]
    public void CanTreatCompletedTurnAsIdle_ReturnsTrueForTurnEndWithoutActiveTools()
    {
        var analysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            AssistantTurnEnded = true,
            ActiveToolCount = 0
        };

        var canTreatAsIdle = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "CanTreatCompletedTurnAsIdle",
            analysis);

        Assert.True(canTreatAsIdle);
    }

    [Fact]
    public void CanTreatCompletedTurnAsIdle_ReturnsFalseWhenToolStillActive()
    {
        var analysis = new PendingTurnRecoveryAnalysis
        {
            UserMessageObserved = true,
            AssistantTurnEnded = true,
            ActiveToolCount = 1
        };

        var canTreatAsIdle = InvokePrivateStatic<bool>(
            typeof(ChatViewModel),
            "CanTreatCompletedTurnAsIdle",
            analysis);

        Assert.False(canTreatAsIdle);
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

    private static T InvokePrivate<T>(object instance, string name, params object[] args)
        => (T)(instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args)
            ?? throw new InvalidOperationException($"Method {name} was not found."));

    private static async Task InvokePrivateAsync(object instance, string name, params object[] args)
    {
        var task = instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(instance, args) as Task
            ?? throw new InvalidOperationException($"Async method {name} was not found.");

        await task;
    }

    private static T InvokePrivateStatic<T>(Type type, string name, params object[] args)
        => (T)(type
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args)
            ?? throw new InvalidOperationException($"Static method {name} was not found."));

    private static T? InvokePrivateStaticNullable<T>(Type type, string name, params object[] args)
        where T : class
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Static method {name} was not found.");

        return (T?)method.Invoke(null, args);
    }

    private static void InvokePrivateStatic(Type type, string name, params object?[] args)
    {
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, args);
    }

    private static UserMessageEvent CreateUserMessageEvent(string content)
        => new()
        {
            Data = new UserMessageData
            {
                Content = content
            }
        };

#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; test fixture mirrors the runtime sub-agent payload.
    private static AssistantMessageEvent CreateAssistantMessageEvent(
        string messageId,
        string content,
        string? parentToolCallId = null)
        => new()
        {
            Data = new AssistantMessageData
            {
                MessageId = messageId,
                Content = content,
                ParentToolCallId = parentToolCallId
            }
        };
#pragma warning restore CS0618

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
