using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression tests for the deferred-send queue ("I sent a message while the chat was running and
/// nothing happened; clicking Stop sent it"). A send that arrives while the chat is busy but has no
/// steerable live turn is deferred instead of delivered. That queue used to be drained ONLY by the
/// Stop button, could silently overwrite an earlier deferred message, clobbered the chat's draft when
/// it was handed back — and, worst of all, showed the user nothing at all while the message waited.
/// These tests pin the queue's contract: the message is visible the instant it is sent, FIFO, never
/// lossy, re-deferred while the chat is still running, delivered exactly once, and clearly flagged as
/// undelivered when it can no longer be sent.
/// </summary>
public sealed class DeferredSendQueueTests
{
    [Fact]
    public void QueueBusySendPrompt_ShowsTheMessageImmediately_AsQueued()
    {
        using var host = DeferredSendHost.Create();

        host.QueuePrompt("sent while busy");

        // The reported bug: this used to be invisible until the user hit Stop.
        var message = Assert.Single(host.Chat.Messages);
        Assert.Equal("sent while busy", message.Content);
        Assert.Equal("user", message.Role);

        var bubble = Assert.Single(host.ViewModel.Messages);
        Assert.Equal(MessageSteerState.Queued, bubble.SteerState);
        Assert.True(bubble.HasSteerBadge);
        Assert.True(bubble.IsSteerInProgress);
    }

    [Fact]
    public void QueueBusySendPrompt_KeepsEveryPrompt_InOrder()
    {
        using var host = DeferredSendHost.Create();

        host.QueuePrompt("first");
        host.QueuePrompt("second");

        // The old single-slot dictionary dropped "first" on the floor.
        Assert.Equal(["first", "second"], host.QueuedPrompts());
        Assert.Equal(["first", "second"], host.Chat.Messages.Select(m => m.Content));
    }

    [Fact]
    public void QueueBusySendPrompt_IgnoresBlankPrompts()
    {
        using var host = DeferredSendHost.Create();

        host.QueuePrompt("   ");

        Assert.Empty(host.QueuedPrompts());
        Assert.Empty(host.Chat.Messages);
    }

    [Fact]
    public void QueuedAttachmentPaths_BuildTheSdkMessageOptionsPayload()
    {
        var oldAttachment = Path.Combine("C:\\attachments", "old.txt");
        var options = new MessageOptions { Prompt = "queued" };
        var attachments = ChatViewModel.BuildUserMessageAttachments([oldAttachment]);

        ChatViewModel.ApplyMessageAttachments(options, attachments);

        var attachment = Assert.IsType<AttachmentFile>(Assert.Single(options.Attachments!));
        Assert.Equal(oldAttachment, attachment.Path);
        Assert.Equal("old.txt", attachment.DisplayName);
    }

    [Fact]
    public void QueuedSend_UsesItsOwnAttachment_AndPreservesANewerComposerAttachment()
    {
        using var host = DeferredSendHost.Create();
        var oldAttachment = Path.Combine("C:\\attachments", "old.txt");
        var newAttachment = Path.Combine("C:\\attachments", "new.txt");

        host.ViewModel.AddAttachment(oldAttachment);
        host.QueuePrompt("queued");
        host.ViewModel.AddAttachment(newAttachment);

        var queuedMessage = Assert.Single(host.Chat.Messages);
        var options = host.BuildQueuedSendOptions(queuedMessage);
        var attachment = Assert.IsType<AttachmentFile>(Assert.Single(options.Attachments!));

        Assert.Equal(oldAttachment, queuedMessage.Attachments.Single());
        Assert.Equal(oldAttachment, attachment.Path);
        Assert.Equal([newAttachment], host.ViewModel.PendingAttachments);
        Assert.Single(host.ViewModel.PendingAttachmentItems);
    }

    [Fact]
    public async Task Drain_WhileChatStillRunning_KeepsPromptQueuedInOrder()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("first");
        host.QueuePrompt("second");
        host.MarkRuntimeBusy();

        await host.DrainAsync();

        // Still running: nothing is sent and the oldest prompt keeps its place at the head.
        Assert.Equal(["first", "second"], host.QueuedPrompts());
    }

    [Fact]
    public async Task Drain_WhenChatIsNoLongerCurrent_FlagsTheVisibleMessagesAsUndelivered()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("first");
        host.QueuePrompt("second");
        var chatId = host.Chat.Id;
        var bubbles = host.ViewModel.Messages.ToList();
        host.ViewModel.CurrentChat = null;

        await host.DrainAsync(chatId);

        // A send can only be dispatched for the chat on screen. The messages stay in the transcript
        // where the user typed them, flagged so it is obvious they never reached the agent.
        Assert.Empty(host.QueuedPrompts(chatId));
        Assert.All(bubbles, b => Assert.Equal(MessageSteerState.Failed, b.SteerState));
        Assert.Equal(["first", "second"], host.Chat.Messages.Select(m => m.Content));
        Assert.Null(host.Draft(chatId));
    }

    [Fact]
    public void QueueBusySendPrompt_ForAChatThatIsNotOnScreen_StillRecordsTheMessage()
    {
        using var host = DeferredSendHost.Create();
        var chatId = host.Chat.Id;
        // Deferred while the user was looking at another chat: the message still goes into that chat so
        // it is waiting there when they switch back, it just has no view model yet.
        host.ViewModel.CurrentChat = null;
        host.QueuePrompt("deferred", chatId);

        var message = Assert.Single(host.Chat.Messages);
        Assert.Equal("deferred", message.Content);
        Assert.Equal(MessageSteerState.Queued, message.SteerDelivery);
        Assert.Empty(host.ViewModel.Messages);
        Assert.Equal(["deferred"], host.QueuedPrompts(chatId));
    }

    [Fact]
    public void FailQueued_ForAChatThatIsNotOnScreen_FlagsTheMessageOnTheModel()
    {
        using var host = DeferredSendHost.Create();
        var chatId = host.Chat.Id;
        host.ViewModel.CurrentChat = null;
        host.QueuePrompt("deferred", chatId);

        host.FailQueued();

        Assert.Equal(MessageSteerState.Failed, Assert.Single(host.Chat.Messages).SteerDelivery);
        Assert.Empty(host.QueuedPrompts(chatId));
    }

    /// <summary>
    /// Switching away from a busy chat and back rebuilds every transcript view model from the model, so
    /// the queue must never hold on to the old instance — flagging a stale one would leave the bubble
    /// the user is actually looking at stuck on "Queued…" with a live "Send now" button.
    /// </summary>
    [Fact]
    public void QueuedMessage_SurvivesATranscriptRebuild_AndResolvesToTheVisibleBubble()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("deferred");
        var originalBubble = Assert.Single(host.ViewModel.Messages);

        host.RebuildTranscript();

        var rebuiltBubble = Assert.Single(host.ViewModel.Messages);
        Assert.NotSame(originalBubble, rebuiltBubble);
        Assert.Equal(MessageSteerState.Queued, rebuiltBubble.SteerState);

        host.FailQueued();

        Assert.Equal(MessageSteerState.Failed, rebuiltBubble.SteerState);
    }

    [Fact]
    public void FailQueued_ForAVisibleMessage_LeavesTheComposerAlone()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("deferred");
        host.ViewModel.PromptText = "typed later";

        host.FailQueued();

        // The deferred message is already in the transcript — re-injecting it into the composer would
        // duplicate it. It is flagged in place instead.
        Assert.Equal("typed later", host.ViewModel.PromptText);
        Assert.Equal(MessageSteerState.Failed, Assert.Single(host.ViewModel.Messages).SteerState);
        Assert.Empty(host.QueuedPrompts());
    }

    [Fact]
    public void FailQueued_WithNothingQueued_LeavesTheDraftAlone()
    {
        using var host = DeferredSendHost.Create();
        host.ViewModel.PromptText = "untouched";

        host.FailQueued();

        Assert.Equal("untouched", host.ViewModel.PromptText);
    }

    [Fact]
    public async Task FlushAsSteer_IsSkipped_WhenTheTurnIsNotSteerable()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("deferred");
        // Busy draining background work: no live turn to inject into.
        host.MarkRuntimeBusy(turnInProgress: false);

        await host.TryFlushAsSteer();

        Assert.Equal(["deferred"], host.QueuedPrompts());
    }

    [Fact]
    public async Task FlushAsSteer_IsSkipped_WhileAManualStopIsPending()
    {
        using var host = DeferredSendHost.Create();
        // Stop & send queues the draft precisely so it starts a FRESH turn after the abort — it must
        // never be injected into the turn that is being torn down.
        host.QueuePrompt("stop and send");
        host.MarkRuntimeBusy(turnInProgress: true);
        host.Runtime.ManualStopRequested = true;

        await host.TryFlushAsSteer();

        Assert.Equal(["stop and send"], host.QueuedPrompts());
    }

    /// <summary>
    /// The flush path dequeues the head before trying to deliver it. If delivery doesn't happen the head
    /// must go back to the FRONT — appending it instead reordered the user's messages.
    /// </summary>
    /// <remarks>
    /// This pins the re-defer ORDER only. The sibling guard that lets the dequeued head bypass the
    /// "queue behind anything already deferred" check is not observable here: the test host has no
    /// session, so <c>SteerActiveTurnAsync</c> always takes the same branch via <c>session is null</c>.
    /// </remarks>
    [Fact]
    public async Task FlushAsSteer_WithSeveralQueuedSends_KeepsThemInOrder()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("first");
        host.QueuePrompt("second");
        host.MarkRuntimeBusy(turnInProgress: true);

        await host.TryFlushAsSteer();

        Assert.Equal(["first", "second"], host.QueuedPrompts());
        Assert.Equal(["first", "second"], host.Chat.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task SendWhileAPromptIsAlreadyDeferred_QueuesBehindIt_InsteadOfOvertakingIt()
    {
        using var host = DeferredSendHost.Create();
        // First message got deferred (busy, but no steerable turn yet).
        host.QueuePrompt("first");

        // A second send must land behind it rather than overtaking it.
        host.MarkRuntimeBusy(turnInProgress: true);
        await host.SendCoreAsync("second");

        Assert.Equal(["first", "second"], host.QueuedPrompts());
        // Both are visible, in the order they were typed.
        Assert.Equal(["first", "second"], host.Chat.Messages.Select(m => m.Content));
    }

    [Fact]
    public void ReleasingAnInactiveChat_FlagsQueuedMessages_InsteadOfDroppingThem()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("deferred");
        var bubble = Assert.Single(host.ViewModel.Messages);
        var chatId = host.Chat.Id;
        // Released while the user is looking at another chat. This path used to Remove() the queue
        // outright and lose the message.
        host.ViewModel.CurrentChat = null;

        host.ReleaseInactiveChat();

        Assert.Empty(host.QueuedPrompts(chatId));
        Assert.Equal(MessageSteerState.Failed, bubble.SteerState);
        Assert.Equal("deferred", Assert.Single(host.Chat.Messages).Content);
    }

    /// <summary>
    /// Rebuilding the session mid-send (an MCP/skill change the agent made during the turn, or a live
    /// agent switch) routes through the same release path as a real teardown. It must not flag the rest
    /// of the queue "not delivered" — the send is succeeding, just on a freshly built session.
    /// </summary>
    [Fact]
    public void RebuildingTheSessionMidSend_LeavesTheQueueIntact()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("still waiting");
        var bubble = Assert.Single(host.ViewModel.Messages);

        host.ReleaseSessionResources();

        Assert.Equal(["still waiting"], host.QueuedPrompts());
        Assert.Equal(MessageSteerState.Queued, bubble.SteerState);
    }

    [Fact]
    public void UnexpectedAbort_FlagsQueuedMessages()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("deferred");
        var bubble = Assert.Single(host.ViewModel.Messages);
        var chatId = host.Chat.Id;
        host.MarkRuntimeBusy();

        // A failed send / lost connection emits no session.idle, so nothing would ever drain the queue.
        host.ApplyUnexpectedAbort();

        Assert.Empty(host.QueuedPrompts(chatId));
        Assert.Equal(MessageSteerState.Failed, bubble.SteerState);
    }

    /// <summary>
    /// The exact scenario from the bug report, end-to-end through the real send path: the assistant
    /// turn has ended but background work is still draining, so the chat still shows as running with a
    /// Stop button and there is no live turn to steer into. Before the fix this window swallowed the
    /// message entirely — no bubble, no transcript entry, composer cleared — and only the Stop button
    /// ever released it. It must now be visible the instant it is sent.
    /// </summary>
    [Fact]
    public async Task SendWhileRunningWithBackgroundWorkPending_IsVisibleImmediately()
    {
        using var host = DeferredSendHost.Create();
        host.MarkTurnEndedWithBackgroundWorkPending();

        // The chat looks busy (Stop is shown) but has no steerable live turn — the defer window.
        Assert.True(host.IsChatRuntimeActive());

        host.ViewModel.PromptText = "also check the logs please";
        await host.SendCoreAsync("also check the logs please", consumeComposerPrompt: true);

        var bubble = Assert.Single(host.ViewModel.Messages);
        Assert.Equal("also check the logs please", bubble.Message.Content);
        Assert.Equal(MessageSteerState.Queued, bubble.SteerState);
        Assert.True(bubble.IsSteerInProgress);
        Assert.Single(host.Chat.Messages);
        Assert.Equal("", host.ViewModel.PromptText);

        // And it is genuinely queued for delivery, not just painted on screen.
        Assert.Equal(["also check the logs please"], host.QueuedPrompts());
    }

    /// <summary>
    /// Stop &amp; Send fires mid-stream, and the assistant's partial answer lives only in
    /// <c>_inProgressMessages</c> until the abort finalizes it into <c>chat.Messages</c>. Rendering the
    /// follow-up before the abort would therefore persist it AHEAD of the answer it is replying to —
    /// the live transcript looks right, then silently scrambles on the next reload.
    /// </summary>
    [Fact]
    public async Task StopAndSend_KeepsTheFollowUpBehindTheAbortedAnswer()
    {
        using var host = DeferredSendHost.Create();
        host.Chat.Messages.Add(new ChatMessage { Role = "user", Content = "do X" });
        host.MarkRuntimeBusy(turnInProgress: true);

        // The assistant is mid-stream: its message is NOT in chat.Messages yet.
        var streaming = host.BeginStreamingAssistantMessage("partial answer to X");
        Assert.DoesNotContain(streaming, host.Chat.Messages);

        host.ViewModel.PromptText = "actually do Y";
        await host.StopAndSendAsync();

        // The abort finalizes the partial answer; it must land before the follow-up.
        host.FinalizeStreamingAssistantMessage();

        Assert.Equal(
            ["do X", "partial answer to X", "actually do Y"],
            host.Chat.Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task StopDuringManualCompaction_WaitsForCompactionTermination()
    {
        using var host = DeferredSendHost.Create();
        host.MarkManualCompactionActive();

        var stopTask = host.StopGenerationAsync();

        Assert.True(host.ManualCompactionCancellationRequested);
        Assert.True(host.Runtime.IsBusy);
        Assert.False(stopTask.IsCompleted);

        host.ConfirmManualCompactionEnded();
        await stopTask;

        Assert.False(host.Runtime.IsBusy);
        Assert.Equal("", host.Runtime.StatusText);
        Assert.False(host.ViewModel.IsContextCompacting);
    }

    [Fact]
    public async Task AutomaticCompaction_DoesNotInterceptTheTurnStopPath()
    {
        using var host = DeferredSendHost.Create();
        host.MarkAutomaticCompactionActive();

        Assert.False(await host.TryStopManualCompactionAsync());
    }

    /// <summary>
    /// Deleting a chat is terminal, so its deferred sends must not outlive it in the queue.
    /// </summary>
    [Fact]
    public void DeletingAChat_DropsItsQueuedSends()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("never sent");
        var chatId = host.Chat.Id;
        host.MarkRuntimeBusy(turnInProgress: true);

        host.CleanupSession();

        Assert.Empty(host.QueuedPrompts(chatId));
    }

    private sealed class DeferredSendHost : IDisposable
    {
        private DeferredSendHost(ChatViewModel viewModel, Chat chat)
        {
            ViewModel = viewModel;
            Chat = chat;
        }

        public ChatViewModel ViewModel { get; }

        public Chat Chat { get; }

        public ChatRuntimeState Runtime => GetRuntimeStates()[Chat.Id];

        public static DeferredSendHost Create()
        {
            var dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false
                }
            });

            var chat = new Chat { Title = "deferred" };
            dataStore.Data.Chats.Add(chat);

            var viewModel = new ChatViewModel(dataStore, TestCopilot.Shared)
            {
                CurrentChat = chat
            };

            return new DeferredSendHost(viewModel, chat);
        }

        public void QueuePrompt(string prompt, Guid? chatId = null)
            => Invoke("QueueBusySendPrompt", chatId ?? Chat.Id, prompt, null);

        public Task DrainAsync(Guid? chatId = null)
            => (Task)Invoke("DrainQueuedBusySendAsync", chatId ?? Chat.Id)!;

        public Task SendCoreAsync(string prompt, bool consumeComposerPrompt = false)
            => (Task)Invoke("SendMessageCore", prompt, consumeComposerPrompt, null)!;

        public MessageOptions BuildQueuedSendOptions(ChatMessage message)
        {
            var attachments = (IEnumerable<Attachment>)Invoke("ResolveSendAttachments", message)!;
            var options = new MessageOptions { Prompt = message.Content };
            ChatViewModel.ApplyMessageAttachments(options, attachments);
            return options;
        }

        /// <summary>Puts the chat in the exact state from the bug report: the assistant turn has ended
        /// but background work is still in flight, so the chat still shows as running with a Stop
        /// button while having no steerable live turn.</summary>
        public void MarkTurnEndedWithBackgroundWorkPending()
        {
            var runtime = (ChatRuntimeState)Invoke("GetOrCreateRuntimeState", Chat.Id)!;
            runtime.HasPendingBackgroundWork = true;
            var method = typeof(ChatViewModel)
                .GetMethod("MarkRuntimeWaitingForSessionIdle", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MarkRuntimeWaitingForSessionIdle was not found.");
            method.Invoke(null, [runtime]);
        }

        public bool IsChatRuntimeActive()
            => (bool)Invoke("IsChatRuntimeActive", Chat.Id)!;

        public void FailQueued()
            => Invoke("FailQueuedBusySends", Chat.Id);

        /// <summary>Reproduces what a chat switch does: every transcript view model is discarded and
        /// rebuilt from the chat's messages.</summary>
        public void RebuildTranscript()
        {
            ViewModel.Messages.Clear();
            foreach (var message in Chat.Messages)
                ViewModel.Messages.Add(new ChatMessageViewModel(message));
        }

        public Task StopAndSendAsync()
            => (Task)Invoke("StopAndSendMessage")!;

        public Task StopGenerationAsync()
            => (Task)Invoke("StopGenerationInternal", true)!;

        public Task<bool> TryStopManualCompactionAsync()
            => (Task<bool>)Invoke("TryStopManualContextCompactionAsync", Chat)!;

        public bool ManualCompactionCancellationRequested
            => GetField<CancellationTokenSource>("_contextCompactionCts").IsCancellationRequested;

        public void MarkManualCompactionActive()
        {
            var runtime = (ChatRuntimeState)Invoke("GetOrCreateRuntimeState", Chat.Id)!;
            ChatViewModel.MarkRuntimeCompacting(runtime);
            Invoke("ApplyDisplayedRuntimeState", runtime);

            SetField("_contextCompactionChatId", (Guid?)Chat.Id);
            SetField("_contextCompactionCts", new CancellationTokenSource());
            SetField(
                "_contextCompactionCompletion",
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            ViewModel.IsContextOperationRunning = true;
            ViewModel.IsContextCompacting = true;
        }

        public void MarkAutomaticCompactionActive()
        {
            var runtime = (ChatRuntimeState)Invoke("GetOrCreateRuntimeState", Chat.Id)!;
            runtime.TurnInProgress = true;
            ChatViewModel.MarkRuntimeCompacting(runtime);
            Invoke("ApplyDisplayedRuntimeState", runtime);

            SetField("_contextCompactionChatId", (Guid?)Chat.Id);
            ViewModel.IsContextCompacting = true;
        }

        public void ConfirmManualCompactionEnded()
        {
            Invoke("CompleteContextCompactionLifecycle", Chat, Runtime, true);
            Invoke("CompleteManualContextCompactionTracking", Chat.Id);
        }

        public void CleanupSession()
            => ViewModel.CleanupSession(Chat.Id);

        /// <summary>Puts an assistant message mid-stream: it lives only in <c>_inProgressMessages</c>,
        /// exactly as it does between the first delta and the turn finalizing.</summary>
        public ChatMessage BeginStreamingAssistantMessage(string content)
        {
            var message = new ChatMessage { Role = "assistant", Content = content, IsStreaming = true };
            GetInProgressMessages()[Chat.Id] = message;
            return message;
        }

        /// <summary>Mirrors what the abort's turn finalization does, via the real production helper.</summary>
        public void FinalizeStreamingAssistantMessage()
        {
            var inProgress = GetInProgressMessages();
            if (!inProgress.Remove(Chat.Id, out var message))
                return;

            var finalize = typeof(ChatViewModel)
                .GetMethod("FinalizeTerminalAssistantMessage", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("FinalizeTerminalAssistantMessage was not found.");
            finalize.Invoke(null, [Chat, message]);
        }

        private Dictionary<Guid, ChatMessage> GetInProgressMessages()
            => (Dictionary<Guid, ChatMessage>)typeof(ChatViewModel)
                .GetField("_inProgressMessages", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ViewModel)!;

        public Task TryFlushAsSteer()
            => (Task)Invoke("FlushQueuedBusySendsAsSteerAsync", Chat.Id)!;

        public void ReleaseInactiveChat()
            => Invoke("ReleaseInactiveChatState", Chat, false, -1);

        public void ReleaseSessionResources()
            => Invoke("ReleaseSessionResources", Chat.Id, false, false);

        public void ApplyUnexpectedAbort()
            => Invoke("ApplyUnexpectedAbortState", Chat, "Connection to Copilot was lost.", true);

        public void MarkRuntimeBusy(bool turnInProgress = true)
        {
            var runtime = (ChatRuntimeState)Invoke("GetOrCreateRuntimeState", Chat.Id)!;
            runtime.IsBusy = true;
            runtime.TurnInProgress = turnInProgress;
        }

        public IReadOnlyList<string> QueuedPrompts(Guid? chatId = null)
        {
            var queue = GetQueue();
            var key = chatId ?? Chat.Id;
            return queue.Contains(key)
                ? ((IEnumerable<ChatMessage>)queue[key]!).Select(message => message.Content).ToList()
                : [];
        }

        public string? Draft(Guid chatId)
            => GetField<Dictionary<Guid, string>>("_chatDrafts").GetValueOrDefault(chatId);

        public void Dispose() => ViewModel.Dispose();

        private System.Collections.IDictionary GetQueue()
            => GetField<System.Collections.IDictionary>("_queuedBusySendPrompts");

        private Dictionary<Guid, ChatRuntimeState> GetRuntimeStates()
            => GetField<Dictionary<Guid, ChatRuntimeState>>("_runtimeStates");

        private T GetField<T>(string name)
            => (T)(typeof(ChatViewModel)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(ViewModel)
                ?? throw new InvalidOperationException($"Field {name} was not found."));

        private void SetField(string name, object? value)
        {
            var field = typeof(ChatViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Field {name} was not found.");
            field.SetValue(ViewModel, value);
        }

        private object? Invoke(string name, params object?[] args)
        {
            var method = typeof(ChatViewModel)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Method {name} was not found.");
            return method.Invoke(ViewModel, args);
        }
    }
}
