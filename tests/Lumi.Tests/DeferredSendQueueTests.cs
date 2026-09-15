using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Remote;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;
using Xunit;

using ChatMessage = Lumi.Models.ChatMessage;
using JsonRpc = StreamJsonRpc.JsonRpc;

#pragma warning disable GHCP001 // Exercise the SDK's typed abort contract.

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
    public void QueueBusySendPrompt_PreservesRemoteAuthor()
    {
        using var host = DeferredSendHost.Create();

        host.QueuePrompt("sent from the phone", authorOverride: "Lumi Mobile");

        Assert.Equal("Lumi Mobile", Assert.Single(host.Chat.Messages).Author);
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
    public async Task Drain_WhileAbortOperationIsPending_KeepsPromptQueued()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("wait for abort");
        host.Runtime.AbortOperation = new TaskCompletionSource<bool>().Task;

        await host.DrainAsync();

        Assert.Equal(["wait for abort"], host.QueuedPrompts());
        Assert.True(host.IsChatRuntimeActive());
    }

    [Fact]
    public async Task Drain_WhileFirstWorktreeIsPending_KeepsPromptQueued()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("wait for the worktree");
        host.MarkWorktreeCreationPending();

        await host.DrainAsync();

        Assert.Equal(["wait for the worktree"], host.QueuedPrompts());
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
    public void QueuedSendNowAvailability_SurvivesATranscriptRebuild()
    {
        using var host = DeferredSendHost.Create();
        host.MarkRuntimeBusy();
        host.Runtime.PendingSessionUserMessageCount = 1;
        host.QueuePrompt("interruptible");

        Assert.True(Assert.Single(host.ViewModel.Messages).CanSendNow);

        host.RebuildTranscript();

        Assert.True(Assert.Single(host.ViewModel.Messages).CanSendNow);
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
    public async Task SendWhileSubagentRuns_SteersTheParentSession()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc();
        host.AttachSession(rpc);
        host.MarkRuntimeBusy(turnInProgress: true);
        host.Runtime.ActiveSubagentExecutionDepth = 1;

        await host.SendCoreAsync("tell the parent instead");

        Assert.Empty(host.QueuedPrompts());
        Assert.Equal(1, rpc.SendCount);
        Assert.Equal("immediate", rpc.LastSendMode);
        var message = Assert.Single(host.ViewModel.Messages);
        Assert.Equal(MessageSteerState.Steering, message.SteerState);
        Assert.True(message.CanSendNow);
    }

    [Fact]
    public async Task FlushAsSteer_ResumesWhenTheParentSessionIsAvailable()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc();
        host.AttachSession(rpc);
        host.QueuePrompt("keep this for the parent");
        host.MarkRuntimeBusy(turnInProgress: true);
        host.Runtime.PendingSessionUserMessageCount = 1;

        await host.TryFlushAsSteer();

        Assert.Empty(host.QueuedPrompts());
        Assert.Equal(1, rpc.SendCount);
        Assert.Equal(MessageSteerState.Steering, Assert.Single(host.ViewModel.Messages).SteerState);
    }

    [Fact]
    public async Task FlushAsSteer_IsSkipped_WhileSetupTimeSendNowWaitsForTurnStart()
    {
        using var host = DeferredSendHost.Create();
        host.QueuePrompt("send me first");
        host.MarkRuntimeBusy(turnInProgress: true);
        host.Runtime.PendingSessionUserMessageCount = 1;
        host.Runtime.SendQueuedNowWhenTurnStarts = true;

        await host.TryFlushAsSteer();

        Assert.Equal(["send me first"], host.QueuedPrompts());
    }

    [Fact]
    public void PreparingAFreshUserTurn_ResetsTheAssistantStartAcknowledgement()
    {
        using var host = DeferredSendHost.Create();
        host.Runtime.AssistantTurnStarted = true;

        host.PrepareFreshTurn();

        Assert.Equal(1, host.Runtime.PendingSessionUserMessageCount);
        Assert.False(host.Runtime.AssistantTurnStarted);
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

        Assert.True(host.Runtime.SendQueuedNowWhenTurnStarts);
        Assert.True(host.Runtime.IsBusy);

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
    public async Task StopDuringManualCompaction_RedrainsQueuedMessagesAfterChatSwitch()
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                using var host = DeferredSendHost.Create();
                host.MarkManualCompactionActive();
                host.QueuePrompt("sent during compaction");
                var queuedMessage = Assert.Single(host.ViewModel.Messages);
                var stopTask = host.StopGenerationAsync();

                host.ConfirmManualCompactionEnded(beforeCompletion: () =>
                {
                    // The compaction lifecycle's first drain runs before Stop has finished.
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(stopTask.IsCompleted);
                    Assert.Single(host.QueuedPrompts());

                    // Off-screen delivery must fail visibly, not stay queued indefinitely.
                    host.ViewModel.CurrentChat = null;
                });

                Assert.Null(await stopTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Dispatcher.UIThread.RunJobs();
                Assert.False(host.IsChatRuntimeActive());
                Assert.Empty(host.QueuedPrompts());
                Assert.Equal(MessageSteerState.Failed, queuedMessage.SteerState);
            }, CancellationToken.None);
        }
        finally
        {
            // Headless completions can resume on their dispatch thread; disposal must run elsewhere.
            await Task.Run(ui.Dispose);
        }
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

    /// <summary>
    /// "Send now" delivers by interrupting the running work, because the SDK only injects a steer at the
    /// running turn's next step boundary and a long tool call does not reach one. The message the user
    /// clicked must therefore be reclaimed into Lumi's queue first — aborting while the SDK still held it
    /// destroyed it, leaving the bubble badged "Steering…" with nothing ever delivered.
    /// </summary>
    [Fact]
    public async Task SendNow_OnSteeringMessage_ReclaimsItBeforeStoppingTheTurn()
    {
        using var host = DeferredSendHost.Create();
        host.MarkRuntimeBusy();
        var message = host.AddTranscriptMessage("answer me now", MessageSteerState.Steering);

        Assert.True(host.IsChatRuntimeActive());

        await host.SendNowAsync(message);

        // Reclaimed, not destroyed: it is back in the queue the post-stop drain reads from.
        Assert.Equal(MessageSteerState.Queued, message.SteerState);
        Assert.Contains("answer me now", host.QueuedPrompts());
    }

    /// <summary>
    /// A setup-time queued message cannot abort immediately because the first prompt has not reached the
    /// SDK yet. The request must wait for AssistantTurnStart, preserving session/MCP setup, and only then
    /// interrupt the first turn so the selected queued message can run through the ready session.
    /// </summary>
    [Fact]
    public async Task SendNow_OnSetupTimeQueuedMessage_WaitsForTurnStartWithoutCancellingSetup()
    {
        using var host = DeferredSendHost.Create();
        host.MarkRuntimeBusy();
        host.QueuePrompt("answer me now");
        var message = host.ViewModel.Messages.Single(item => item.Content == "answer me now");
        message.SteerState = MessageSteerState.Queued;

        Assert.True(message.CanSendNow);

        await host.SendNowAsync(message);

        Assert.True(host.Runtime.IsBusy);
        Assert.False(host.Runtime.ManualStopRequested);
        Assert.True(host.Runtime.SendQueuedNowWhenTurnStarts);
        Assert.False(message.CanSendNow);
        Assert.Equal(MessageSteerState.Queued, message.SteerState);
        Assert.Contains("answer me now", host.QueuedPrompts());

        host.Runtime.PendingSessionUserMessageCount = 1;
        host.Runtime.AssistantTurnStarted = true;
        await host.SendQueuedNowAfterTurnStartAsync();

        Assert.False(host.Runtime.IsBusy);
        Assert.False(host.Runtime.SendQueuedNowWhenTurnStarts);
        Assert.Contains("answer me now", host.QueuedPrompts());
    }

    [Fact]
    public async Task SendNow_OnQueuedMessageWithSubmittedTurn_InterruptsTheTurn()
    {
        using var host = DeferredSendHost.Create();
        host.MarkRuntimeBusy();
        host.Runtime.PendingSessionUserMessageCount = 1;
        host.Runtime.AssistantTurnStarted = true;
        host.QueuePrompt("answer me now");
        var message = host.ViewModel.Messages.Single(item => item.Content == "answer me now");

        Assert.True(message.CanSendNow);

        await host.SendNowAsync(message);

        Assert.False(host.Runtime.IsBusy);
        Assert.Equal(MessageSteerState.Queued, message.SteerState);
        Assert.Contains("answer me now", host.QueuedPrompts());
    }

    [Fact]
    public async Task BackgroundActivityRefresh_ListsConcreteTasksWithoutMakingTheAssistantBusy()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc();
        rpc.RunningShells.Add("preview");
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();

        await host.ViewModel.RefreshBackgroundActivityAsync();

        var item = Assert.Single(host.ViewModel.RunningSessionActivities);
        Assert.Equal("preview", item.Id);
        Assert.Equal("Debug process", item.Title);
        Assert.Equal("Start-Sleep -Seconds 300", item.Detail);
        Assert.False(host.ViewModel.IsBusy);
        Assert.Equal(0, rpc.AbortCount);

        rpc.RunningShells.Clear();
        await host.ViewModel.RefreshBackgroundActivityAsync();
        Assert.Empty(host.ViewModel.RunningSessionActivities);
        Assert.True(host.ViewModel.HasBackgroundActivityNotice);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundActivityRefresh_IgnoresAResultAfterSessionEndsOrChatChanges(bool switchChat)
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                using var host = DeferredSendHost.Create();
                using var rpc = new AbortRpc
                {
                    TaskListReply = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                rpc.RunningShells.Add("preview");
                host.AttachSession(rpc);
                host.MarkTurnEndedWithBackgroundWorkPending();
                var refresh = host.ViewModel.RefreshBackgroundActivityAsync();
                await rpc.TaskListReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

                if (switchChat)
                    host.ViewModel.CurrentChat = new Chat { IsSessionActive = true };
                else
                    host.ViewModel.IsSessionActive = false;
                rpc.TaskListReply.SetResult(rpc.BuildTaskList());
                await refresh;

                Assert.Empty(host.ViewModel.RunningSessionActivities);
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    [Fact]
    public async Task BackgroundActivityRefresh_FailureKeepsLastKnownItemsAndReportsUnavailable()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc();
        rpc.RunningShells.Add("preview");
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();
        await host.ViewModel.RefreshBackgroundActivityAsync();
        var items = host.ViewModel.RunningSessionActivities;

        rpc.Disconnect();
        await host.ViewModel.RefreshBackgroundActivityAsync();

        Assert.Same(items, host.ViewModel.RunningSessionActivities);
        Assert.True(host.ViewModel.HasBackgroundActivityNotice);
        Assert.Equal(Lumi.Localization.Loc.Get("Chat_BackgroundActivityUnavailable"), host.ViewModel.BackgroundActivityNotice);
        Assert.True(host.ViewModel.IsSessionActive);
        Assert.False(host.ViewModel.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSend_ConfigurationChangedDuringPersistence_QueuesWithoutReplacingTheSession(
        bool sameSessionReconfiguration)
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                await TestCopilot.Shared.ConnectAsync();
                using var host = DeferredSendHost.Create();
                using var rpc = new AbortRpc();
                using var lifetime = new CancellationTokenSource();
                using var sendCancellation = new CancellationTokenSource();
                host.AttachSession(rpc);
                host.MarkTurnEndedWithBackgroundWorkPending();
                host.MarkRuntimeTerminal();
                host.Runtime.IsSessionActive = true;
                host.Runtime.HasPendingBackgroundWork = true;
                host.SetTurnCancellation(lifetime);
                var sequence = host.Runtime.LifecycleTurnSequence;
                var sessionId = host.Chat.CopilotSessionId;
                const string deviceId = "phone-config-race";
                const string requestId = "message-config-race";
                var configurationChanged = false;
                host.DataStore.IndexSaved += () =>
                {
                    if (configurationChanged || host.Chat.LastRemoteRequestId != requestId)
                        return;
                    configurationChanged = true;
                    host.QueueSessionRefresh(sameSessionReconfiguration);
                };

                var accepted = false;
                try
                {
                    await host.ViewModel.SendExternalMessageAsync(
                        host.Chat, "Keep the server alive", "Lumi Mobile", sendCancellation.Token,
                        onAccepted: () =>
                        {
                            accepted = true;
                            // Bound the unfixed path before it can start a real replacement session.
                            sendCancellation.Cancel();
                        },
                        remoteDeviceId: deviceId,
                        remoteRequestId: requestId);
                }
                catch (OperationCanceledException) when (sendCancellation.IsCancellationRequested)
                {
                }

                Assert.True(configurationChanged);
                Assert.True(accepted);
                Assert.Equal(sessionId, host.Chat.CopilotSessionId);
                Assert.True(host.HasCachedSession(rpc.Session));
                Assert.Equal(0, rpc.DestroyCount);
                Assert.Equal(0, rpc.SendCount);
                Assert.False(lifetime.IsCancellationRequested);
                Assert.False(host.ViewModel.IsBusy);
                Assert.True(host.IsChatRuntimeActive());
                Assert.Equal(sequence, host.Runtime.LifecycleTurnSequence);
                Assert.Equal(["Keep the server alive"], host.QueuedPrompts());
                var message = Assert.Single(host.Chat.Messages);
                Assert.Equal(MessageSteerState.Queued, message.SteerDelivery);
                Assert.Equal(requestId, message.RemoteRequestId);
                Assert.Equal(deviceId, host.Chat.LastRemoteDeviceId);
                Assert.Equal(requestId, host.Chat.LastRemoteRequestId);

                using var main = new MainViewModel(
                    host.DataStore, TestCopilot.Shared, new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatSurfaceRegistry.Attach(host.ViewModel);
                var retry = await new RemoteCommandRouter(host.DataStore, main).ExecuteAsync(
                    new RemoteCommand(RemoteProtocol.Actions.SendMessage)
                    {
                        AuthenticatedDeviceId = deviceId, RequestId = requestId
                    }.With("chatId", host.Chat.Id.ToString()).With("message", message.Content),
                    CancellationToken.None);
                Assert.True(retry.Ok, retry.Error);
                Assert.Single(host.Chat.Messages);
                Assert.Single(host.QueuedPrompts());
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    [Fact]
    public async Task RemoteStop_RejectedTaskCancellation_ReturnsFailureWhileSessionRemainsActive()
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                using var host = DeferredSendHost.Create();
                using var rpc = new AbortRpc { RejectTaskCancellation = true };
                rpc.RunningShells.Add("preview");
                host.AttachSession(rpc);
                host.MarkTurnEndedWithBackgroundWorkPending();
                using var main = new MainViewModel(
                    host.DataStore, TestCopilot.Shared, new UpdateService(),
                    initializeCopilotOnStartup: false);
                main.ChatSurfaceRegistry.Attach(host.ViewModel);
                var router = new RemoteCommandRouter(host.DataStore, main);

                var result = await router.ExecuteAsync(
                    new RemoteCommand(RemoteProtocol.Actions.StopGeneration)
                        .With("chatId", host.Chat.Id.ToString()),
                    CancellationToken.None);

                Assert.False(result.Ok);
                Assert.Equal(host.Chat.Id, result.ChatId);
                Assert.NotNull(result.Error);
                Assert.True(host.ViewModel.IsSessionActive);
                Assert.True(host.ViewModel.HasBackgroundActivity);
                Assert.Contains("preview", rpc.RunningShells);
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    [Fact]
    public async Task StopSession_CancelsAttachedShellsInsteadOfOnlyAbortingTheAssistant()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc();
        rpc.RunningShells.Add("debug-process");
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();

        var error = await host.StopGenerationAsync();

        Assert.Null(error);
        Assert.Equal(["debug-process"], rpc.CancelledShells);
        Assert.Empty(rpc.RunningShells);
        Assert.False(host.IsChatRuntimeActive());
    }

    [Fact]
    public async Task StopSession_RejectedTaskCancellation_PreservesBackgroundActivityAndReportsFailure()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc { RejectTaskCancellation = true };
        rpc.RunningShells.Add("debug-process");
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();
        host.QueuePrompt("must not replace a failed stop");

        var error = await host.StopGenerationAsync();

        Assert.NotNull(error);
        Assert.True(host.IsChatRuntimeActive());
        Assert.True(host.Runtime.HasPendingBackgroundWork);
        Assert.Equal(MessageSteerState.Failed, Assert.Single(host.ViewModel.Messages).SteerState);
    }

    [Fact]
    public async Task SendNow_BackgroundOnlyAbortWithoutIdle_ReleasesTheChat()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc();
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();
        host.QueuePrompt("answer me now");
        var message = Assert.Single(host.ViewModel.Messages);

        await host.SendNowAsync(message);

        Assert.Equal(1, rpc.AbortCount);
        Assert.False(host.IsChatRuntimeActive());
        Assert.Equal(["answer me now"], host.QueuedPrompts());
        Assert.Equal(MessageSteerState.Queued, message.SteerState);
    }

    [Fact]
    public async Task SendNow_RejectedAbort_SurfacesTheErrorAndDoesNotSend()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc
        {
            Result = new AbortResult { Success = false, Error = "The session could not be interrupted." }
        };
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();
        host.QueuePrompt("answer me now");
        var message = Assert.Single(host.ViewModel.Messages);

        await host.SendNowAsync(message);

        Assert.Equal(MessageSteerState.Failed, message.SteerState);
        Assert.Empty(host.QueuedPrompts());
        Assert.True(host.IsChatRuntimeActive());
        Assert.False(host.Runtime.IsStopping);
        Assert.False(host.Runtime.ManualStopRequested);
        Assert.Contains("The session could not be interrupted.", host.ViewModel.StatusText);
    }

    [Fact]
    public async Task Stop_CoalescesRequestsAndKeepsNewSendsQueuedUntilCleanupCompletes()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc
        {
            AbortReply = new TaskCompletionSource<AbortResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();
        host.QueuePrompt("selected");

        var firstStop = host.StopGenerationAsync();
        await rpc.AbortReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.MarkRuntimeTerminal();

        Assert.True(host.Runtime.IsStopping);
        Assert.True(host.IsChatRuntimeActive());
        Assert.Same(firstStop, host.StopGenerationAsync());

        await host.SendCoreAsync("sent during stop");
        await host.DrainAsync();
        Assert.Equal(["selected", "sent during stop"], host.QueuedPrompts());
        Assert.Equal(0, rpc.SendCount);

        rpc.AbortReply.SetResult(new AbortResult { Success = true });
        Assert.Null(await firstStop);
        Assert.Equal(1, rpc.AbortCount);
        Assert.False(host.Runtime.IsStopping);
        Assert.False(host.IsChatRuntimeActive());
    }

    [Fact]
    public async Task Stop_DisconnectedTransport_CompletesWithoutAnIdleWaiter()
    {
        using var host = DeferredSendHost.Create();
        using var rpc = new AbortRpc
        {
            AbortReply = new TaskCompletionSource<AbortResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        host.AttachSession(rpc);
        host.MarkTurnEndedWithBackgroundWorkPending();
        host.QueuePrompt("selected");

        var stop = host.StopGenerationAsync();
        await rpc.AbortReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rpc.Disconnect();

        var error = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        rpc.AbortReply.TrySetResult(new AbortResult { Success = false });
        Assert.NotNull(error);
        Assert.False(host.Runtime.IsStopping);
        Assert.False(host.IsChatRuntimeActive());
        Assert.Equal(MessageSteerState.Failed, Assert.Single(host.ViewModel.Messages, message => message.Role == "user").SteerState);
        Assert.Empty(host.QueuedPrompts());
    }

    [Fact]
    public async Task AssistantIdle_WithBackgroundWork_ReadiesTheAssistantWithoutReleasingTheSession()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            host.ViewModel.IsBusy = true;
            host.ViewModel.IsStreaming = true;
            host.Runtime.HasPendingBackgroundWork = true;

            rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
            Dispatcher.UIThread.RunJobs();

            Assert.False(host.ViewModel.IsBusy);
            Assert.False(host.ViewModel.IsStreaming);
            Assert.False(host.Chat.IsRunning);
            Assert.True(host.Chat.IsSessionActive);
            Assert.True(host.ViewModel.HasBackgroundActivity);
            Assert.True(host.IsChatRuntimeActive());
            Assert.True(host.Runtime.HasPendingBackgroundWork);
            Assert.Equal(0, rpc.AbortCount);

            rpc.Emit(new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "continuation" } });
            Dispatcher.UIThread.RunJobs();

            Assert.True(host.ViewModel.IsBusy);
            Assert.True(host.Chat.IsRunning);
            Assert.True(host.Runtime.HasPendingBackgroundWork);
            Assert.False(host.ViewModel.HasBackgroundActivity);

            rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
            rpc.Emit(new SessionIdleEvent { Data = new SessionIdleData() });
            Dispatcher.UIThread.RunJobs();

            Assert.False(host.ViewModel.IsBusy);
            Assert.False(host.ViewModel.IsSessionActive);
            Assert.False(host.Chat.IsSessionActive);
            Assert.False(host.IsChatRuntimeActive());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BackgroundChangeQueuedAfterSessionIdle_DoesNotReactivateTheSession()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            host.Runtime.HasPendingBackgroundWork = true;

            rpc.Emit(new SessionIdleEvent { Data = new SessionIdleData() });
            rpc.Emit(new SessionBackgroundTasksChangedEvent { Data = new SessionBackgroundTasksChangedData() });
            Dispatcher.UIThread.RunJobs();

            Assert.False(host.IsChatRuntimeActive());
            Assert.False(host.Chat.IsSessionActive);
            Assert.False(host.ViewModel.HasBackgroundActivity);
            Assert.False(host.Runtime.HasPendingBackgroundWork);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChildActivity_AfterAssistantIdle_DoesNotRestartTheAssistantSpinner()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            host.ViewModel.IsBusy = true;

            rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
            rpc.Emit(new SubagentStartedEvent
            {
                AgentId = "background-child",
                Data = new SubagentStartedData
                {
                    ToolCallId = "child-tool",
                    AgentName = "task",
                    AgentDisplayName = "Background child",
                    AgentDescription = "Continues independently"
                }
            });
            rpc.Emit(new ToolExecutionStartEvent
            {
                AgentId = "background-child",
                Data = new ToolExecutionStartData { ToolCallId = "child-shell", ToolName = "powershell" }
            });
            rpc.Emit(new SessionBackgroundTasksChangedEvent { Data = new SessionBackgroundTasksChangedData() });
            Dispatcher.UIThread.RunJobs();

            Assert.False(host.ViewModel.IsBusy);
            Assert.False(host.Chat.IsRunning);
            Assert.True(host.IsChatRuntimeActive());
            Assert.Equal(1, host.Runtime.ActiveSubagentExecutionDepth);
            Assert.Equal("InProgress", host.Chat.Messages.Single(message => message.ToolCallId == "child-tool").ToolStatus);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SendNow_OnReadyActiveSession_DoesNotWaitForAnotherAssistantStart()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(async () =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();

            rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
            Dispatcher.UIThread.RunJobs();
            host.QueuePrompt("run this now");

            await host.SendNowAsync(Assert.Single(host.ViewModel.Messages));

            Assert.Equal(1, rpc.AbortCount);
            Assert.False(host.Runtime.SendQueuedNowWhenTurnStarts);
            Assert.False(host.Runtime.IsSessionActive);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ReadySession_AdmitsANewTurnWithoutCancelingItsBackgroundLifetime()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            using var backgroundLifetime = new CancellationTokenSource();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            host.SetTurnCancellation(backgroundLifetime);
            host.Runtime.HasPendingBackgroundWork = true;

            rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
            Dispatcher.UIThread.RunJobs();

            Assert.True(host.CanStartTurnOnReadySession());
            Assert.False(host.ViewModel.IsAssistantBusy(host.Chat.Id));
            Assert.False(host.ViewModel.IsChatBusyForSend(host.Chat.Id));
            Assert.False(host.ReleasePreviousTurnCancellation());
            Assert.False(backgroundLifetime.IsCancellationRequested);
            Assert.True(host.ViewModel.OwnsLiveChat(host.Chat.Id));
            Assert.Equal(0, rpc.AbortCount);

            using var reservation = host.ViewModel.TryReserveExternalSend(host.Chat.Id);
            Assert.NotNull(reservation);
            Assert.True(host.ViewModel.IsChatBusyForSend(host.Chat.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AssistantTurnEnd_IsAStepBoundary_NotAssistantReadiness()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();

            rpc.Emit(new AssistantTurnStartEvent { Data = new AssistantTurnStartData { TurnId = "step" } });
            rpc.Emit(new AssistantTurnEndEvent { Data = new AssistantTurnEndData { TurnId = "step" } });
            Dispatcher.UIThread.RunJobs();

            Assert.True(host.ViewModel.IsBusy);
            Assert.True(host.Chat.IsRunning);
            Assert.True(host.Chat.IsSessionActive);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChildLifecycleEvents_DoNotStopTheParentOrConfirmItsSteer()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.MarkRuntimeBusy();
            host.PrepareFreshTurn();
            var steer = host.AddTranscriptMessage("for the parent", MessageSteerState.Steering);
            host.RegisterPendingSteer(steer);

            rpc.Emit(new UserMessageEvent { AgentId = "child", Data = new UserMessageData { Content = "child prompt" } });
            rpc.Emit(new AssistantTurnStartEvent { AgentId = "child", Data = new AssistantTurnStartData { TurnId = "child-turn" } });
            rpc.Emit(new AssistantTurnEndEvent { AgentId = "child", Data = new AssistantTurnEndData { TurnId = "child-turn" } });
            rpc.Emit(new AbortEvent { AgentId = "child", Data = new AbortData { Reason = new GitHub.Copilot.AbortReason("subagent_cancelled") } });
            rpc.Emit(new SessionErrorEvent { AgentId = "child", Data = new SessionErrorData { ErrorType = "test", Message = "Child failed" } });
            rpc.Emit(new SessionIdleEvent { AgentId = "child", Data = new SessionIdleData() });
            Dispatcher.UIThread.RunJobs();

            Assert.True(host.Runtime.TurnInProgress);
            Assert.False(host.Runtime.AssistantTurnStarted);
            Assert.Equal(1, host.Runtime.PendingSessionUserMessageCount);
            Assert.Equal(MessageSteerState.Steering, steer.SteerState);
            Assert.True(host.IsChatRuntimeActive());

            rpc.Emit(new UserMessageEvent { Data = new UserMessageData { Content = "for the parent" } });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MessageSteerState.Steered, steer.SteerState);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SteeredParentOutput_StaysOutsideTheRunningChildCard()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            rpc.Emit(new SubagentStartedEvent
            {
                AgentId = "child",
                Data = new SubagentStartedData
                {
                    ToolCallId = "child-tool",
                    AgentName = "task",
                    AgentDisplayName = "Child",
                    AgentDescription = "Background work"
                }
            });
            Dispatcher.UIThread.RunJobs();

            rpc.Emit(new AssistantMessageEvent
            {
                Data = new AssistantMessageData { MessageId = "parent-answer", Content = "PARENT_STEER_OK" }
            });
            rpc.Emit(new AssistantMessageEvent
            {
                AgentId = "child",
                Data = new AssistantMessageData { MessageId = "child-answer", Content = "CHILD_DONE" }
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("PARENT_STEER_OK", Assert.Single(host.Chat.Messages, message => message.Role == "assistant").Content);
            var child = Assert.Single(host.Chat.Messages, message => message.ToolCallId == "child-tool");
            Assert.Contains("CHILD_DONE", child.Content);
            Assert.DoesNotContain("PARENT_STEER_OK", child.Content);
            Assert.Equal("InProgress", child.ToolStatus);
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviousTurnTerminalCallback_CannotClearTheReplacementTurn(bool abort)
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.ViewModel.BeginChatLifecycleTurn(host.Chat);
            host.PrepareFreshTurn();
            rpc.Emit(new UserMessageEvent { Data = new UserMessageData { Content = "original" } });
            Dispatcher.UIThread.RunJobs();

            if (abort)
                rpc.Emit(new AbortEvent { Data = new AbortData { Reason = GitHub.Copilot.AbortReason.UserInitiated } });
            else
                rpc.Emit(new SessionIdleEvent { Data = new SessionIdleData() });

            host.ViewModel.BeginChatLifecycleTurn(host.Chat);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            Dispatcher.UIThread.RunJobs();

            Assert.True(host.Runtime.TurnInProgress);
            Assert.Equal(1, host.Runtime.PendingSessionUserMessageCount);
            Assert.True(host.IsChatRuntimeActive());
            Assert.DoesNotContain(host.Chat.Messages, message => message.Role == "error");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AbortedIdleAfterReplacementEcho_DoesNotEndTheReplacementTurn()
    {
        using var ui = HeadlessTestSession.Start();
        await ui.Dispatch(() =>
        {
            using var host = DeferredSendHost.Create();
            using var rpc = new AbortRpc();
            host.AttachSession(rpc, subscribe: true);
            host.ViewModel.BeginChatLifecycleTurn(host.Chat);
            host.PrepareFreshTurn();
            host.MarkRuntimeBusy();
            rpc.Emit(new UserMessageEvent { Data = new UserMessageData { Content = "replacement" } });
            Dispatcher.UIThread.RunJobs();

            rpc.Emit(new SessionIdleEvent { Data = new SessionIdleData { Aborted = true } });
            Dispatcher.UIThread.RunJobs();

            Assert.True(host.Runtime.TurnInProgress);
            Assert.Equal(1, host.Runtime.PendingSessionUserMessageCount);
            Assert.True(host.IsChatRuntimeActive());
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SendNow_OnLaterQueuedMessage_MovesTheSelectedMessageToTheFront()
    {
        using var host = DeferredSendHost.Create();
        host.MarkRuntimeBusy();
        host.Runtime.AssistantTurnStarted = true;
        host.QueuePrompt("first");
        host.QueuePrompt("selected");
        var selected = host.ViewModel.Messages.Single(item => item.Content == "selected");

        await host.SendNowAsync(selected);

        Assert.Equal(["selected", "first"], host.QueuedPrompts());
    }

    /// <summary>
    /// A pending ask_question task is Lumi-owned state, not SDK turn state. Stopping the SDK tool without
    /// cancelling that task leaves HasPendingQuestion true forever, so the post-stop drain and every later
    /// send remain queued even though the UI says the chat is idle.
    /// </summary>
    [Fact]
    public async Task SendNow_WithPendingQuestion_CancelsQuestionBeforeTheQueuedSendDrain()
    {
        using var host = DeferredSendHost.Create();
        var pendingQuestion = host.TrackPendingQuestion();
        var message = host.AddTranscriptMessage("answer me now", MessageSteerState.Steering);

        Assert.True(host.IsChatRuntimeActive());

        await host.SendNowAsync(message);

        Assert.True(pendingQuestion.Task.IsCanceled);
        Assert.Equal("Failed", host.QuestionMessage.ToolStatus);
        Assert.Equal(MessageSteerState.Queued, message.SteerState);
        Assert.False(host.IsChatRuntimeActive());
        Assert.Contains("answer me now", host.QueuedPrompts());
    }

    [Fact]
    public async Task QuestionRequestTimeout_ReleasesTheIdleChat_ForTheNextSend()
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                using var host = DeferredSendHost.Create();
                using var rpc = new AbortRpc();
                host.AttachSession(rpc, subscribe: true);
                rpc.RegisterTool(host.BuildQuestionTool());
                var presented = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                host.ViewModel.QuestionAsked += (id, _, _, _) => presented.TrySetResult(id);
                const string requestId = "expired-question";
                const string toolCallId = "expired-question-call";

                var invocation = rpc.BroadcastAsync(new ExternalToolRequestedEvent
                {
                    Data = new ExternalToolRequestedData
                    {
                        RequestId = requestId,
                        SessionId = rpc.Session.SessionId,
                        ToolCallId = toolCallId,
                        ToolName = "ask_question",
                        Arguments = JsonSerializer.SerializeToElement(new
                        {
                            question = "Approve the rebase?",
                            options = new[] { "Yes", "No" },
                            allowFreeText = true,
                            allowMultiSelect = false
                        })
                    }
                });
                var questionId = await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(host.IsChatRuntimeActive());

                await rpc.BroadcastAsync(new ExternalToolCompletedEvent
                {
                    Data = new ExternalToolCompletedData { RequestId = requestId }
                });
                rpc.Emit(new ToolExecutionCompleteEvent
                {
                    Data = new ToolExecutionCompleteData
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Error = new ToolExecutionCompleteError
                        {
                            Message = "External tool request received no response within 1800 seconds.",
                            Code = "failure"
                        }
                    }
                });
                rpc.Emit(new AssistantIdleEvent { Data = new AssistantIdleData() });
                rpc.Emit(new SessionIdleEvent { Data = new SessionIdleData() });
                Dispatcher.UIThread.RunJobs();

                Assert.False(host.Runtime.IsBusy);
                Assert.False(host.Runtime.IsSessionActive);
                await invocation.WaitAsync(TimeSpan.FromSeconds(5));
                Dispatcher.UIThread.RunJobs();
                Assert.False(host.IsChatRuntimeActive());
                Assert.False(host.ViewModel.IsChatBusy(host.Chat.Id));
                Assert.True(host.CanStartTurnOnReadySession());
                Assert.True(host.QuestionCard(questionId).IsExpired);
                Assert.Equal("Failed", host.Chat.Messages.Single(m => m.QuestionId == questionId).ToolStatus);

                await TestCopilot.Shared.ConnectAsync();
                await host.SendCoreAsync("Rebase on latest main");
                Assert.Equal(1, rpc.SendCount);
                Assert.Null(rpc.LastSendMode);
                Assert.Empty(host.QueuedPrompts());
                Assert.Equal(MessageSteerState.None, host.Chat.Messages.Last(m => m.Role == "user").SteerDelivery);
                Assert.Equal(0, rpc.AbortCount);
                Assert.Equal(0, rpc.DestroyCount);
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    [Fact]
    public async Task QuestionRequest_AlreadyCanceled_DoesNotCreateAQuestionOrBlockTheChat()
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                using var host = DeferredSendHost.Create();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var tool = host.BuildQuestionTool();
                Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("cancellationToken", out _));

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => host.AskQuestionAsync("Never present this", cancellation.Token));
                Dispatcher.UIThread.RunJobs();

                Assert.Empty(host.Chat.Messages);
                Assert.Empty(host.ViewModel.TranscriptTurns);
                Assert.False(host.IsChatRuntimeActive());
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    [Fact]
    public async Task QuestionCancellation_AfterIdleDrain_RetriesTheAlreadyQueuedMessage()
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                await TestCopilot.Shared.ConnectAsync();
                using var host = DeferredSendHost.Create();
                using var rpc = new AbortRpc();
                using var cancellation = new CancellationTokenSource();
                host.AttachSession(rpc);
                var presented = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                host.ViewModel.QuestionAsked += (id, _, _, _) => presented.TrySetResult(id);
                var invocation = host.AskQuestionAsync("Please choose", cancellation.Token);
                await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(host.IsChatRuntimeActive());

                // Idle was processed before the asynchronous SDK cancellation reached the handler.
                host.QueuePrompt("the message already waiting");
                await host.DrainAsync();
                Assert.Equal(0, rpc.SendCount);
                Assert.Single(host.QueuedPrompts());
                Assert.False(host.Runtime.IsSessionActive);

                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => invocation.WaitAsync(TimeSpan.FromSeconds(5)));
                await rpc.SendReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Empty(host.QueuedPrompts());
                Assert.Equal(1, rpc.SendCount);
                Assert.Null(rpc.LastSendMode);
                Assert.Single(host.Chat.Messages, m => m.Role == "user");
                Assert.Equal(0, rpc.AbortCount);
                Assert.Equal(0, rpc.DestroyCount);
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    [Fact]
    public async Task QuestionCancellation_ExpiresOnlyThatQuestion_AndPreservesAnotherAnswer()
    {
        var ui = HeadlessTestSession.Start();
        try
        {
            await ui.Dispatch(async () =>
            {
                using var host = DeferredSendHost.Create();
                using var cancellation = new CancellationTokenSource();
                var presented = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                host.ViewModel.QuestionAsked += (id, _, _, _) => presented.TrySetResult(id);
                var expired = host.AskQuestionAsync("Expired choice", cancellation.Token);
                var firstId = await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
                presented = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var active = host.AskQuestionAsync("Active choice");
                var secondId = await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));

                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => expired.WaitAsync(TimeSpan.FromSeconds(5)));
                Dispatcher.UIThread.RunJobs();

                Assert.True(host.QuestionCard(firstId).IsExpired);
                Assert.False(host.QuestionCard(secondId).IsExpired);
                Assert.True(host.IsChatRuntimeActive());
                Assert.False(active.IsCompleted);
                host.ViewModel.SubmitQuestionAnswer(firstId, "too late");
                Assert.Null(host.Chat.Messages.First().ToolOutput);

                host.QuestionCard(secondId).Submit("Yes");
                var result = await active.WaitAsync(TimeSpan.FromSeconds(5));
                Dispatcher.UIThread.RunJobs();
                Assert.Contains("User answered: Yes", result?.ToString());
                Assert.Equal("User answered: Yes", host.Chat.Messages.Last().ToolOutput);
                Assert.True(host.QuestionCard(secondId).IsAnswered);
                Assert.False(host.QuestionCard(secondId).IsExpired);
                Assert.False(host.IsChatRuntimeActive());
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(ui.Dispose);
        }
    }

    /// <summary>
    /// "Send now" is offered for exactly the two pending states. A delivered or failed message has
    /// nothing left to expedite.
    /// </summary>
    [Theory]
    [InlineData(MessageSteerState.Queued, true)]
    [InlineData(MessageSteerState.Steering, true)]
    [InlineData(MessageSteerState.Steered, false)]
    [InlineData(MessageSteerState.Failed, false)]
    [InlineData(MessageSteerState.None, false)]
    public void IsSteerInProgress_IsOfferedForPendingMessagesOnly(MessageSteerState state, bool expected)
    {
        var message = new ChatMessageViewModel(new ChatMessage { Role = "user", Content = "m" })
        {
            SteerState = state
        };

        Assert.Equal(expected, message.IsSteerInProgress);
    }

    private sealed class DeferredSendHost : IDisposable
    {
        private DeferredSendHost(ChatViewModel viewModel, Chat chat)
        {
            ViewModel = viewModel;
            Chat = chat;
        }

        public ChatViewModel ViewModel { get; }

        public DataStore DataStore => GetField<DataStore>("_dataStore");

        public Chat Chat { get; }

        public ChatRuntimeState Runtime => GetRuntimeStates()[Chat.Id];

        public ChatMessage QuestionMessage { get; private set; } = null!;

        public static DeferredSendHost Create()
        {
            var dataStore = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    NotificationsEnabled = false
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

        public void QueuePrompt(string prompt, Guid? chatId = null, string? authorOverride = null)
            => Invoke("QueueBusySendPrompt", chatId ?? Chat.Id, prompt, null, authorOverride);

        public Task DrainAsync(Guid? chatId = null)
            => (Task)Invoke("DrainQueuedBusySendAsync", chatId ?? Chat.Id)!;

        public Task SendCoreAsync(string prompt, bool consumeComposerPrompt = false)
            => (Task)Invoke("SendMessageCore", prompt, consumeComposerPrompt, null)!;

        public AIFunction BuildQuestionTool()
            => (AIFunction)Invoke("BuildAskQuestionTool", Chat.Id)!;

        public Task<object?> AskQuestionAsync(string question, CancellationToken cancellationToken = default)
            => BuildQuestionTool().InvokeAsync(new AIFunctionArguments
            {
                ["question"] = question,
                ["options"] = new[] { "Yes", "No" },
                ["allowFreeText"] = true,
                ["allowMultiSelect"] = false
            }, cancellationToken).AsTask();

        public QuestionItem QuestionCard(string questionId)
            => ViewModel.TranscriptTurns.SelectMany(turn => turn.Items)
                .OfType<QuestionItem>().Single(question => question.QuestionId == questionId);

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

        public Task<string?> StopGenerationAsync()
            => (Task<string?>)Invoke("StopGenerationInternal", Chat, true)!;

        public bool CanStartTurnOnReadySession()
            => (bool)Invoke("CanStartTurnOnReadySession", Chat)!;

        public bool ReleasePreviousTurnCancellation()
            => (bool)Invoke("ReleasePreviousTurnCancellation", Chat.Id)!;

        public void SetTurnCancellation(CancellationTokenSource cancellation)
            => GetField<Dictionary<Guid, CancellationTokenSource>>("_ctsSources")[Chat.Id] = cancellation;

        public void QueueSessionRefresh(bool sameSession)
            => GetField<HashSet<Guid>>(sameSession ? "_pendingSessionReconfigurations" : "_pendingSessionInvalidations")
                .Add(Chat.Id);

        public bool HasCachedSession(CopilotSession session)
            => GetField<Dictionary<Guid, CopilotSession>>("_sessionCache").GetValueOrDefault(Chat.Id) == session;

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

        public void ConfirmManualCompactionEnded(Action? beforeCompletion = null)
        {
            Invoke("CompleteContextCompactionLifecycle", Chat, Runtime, true);
            beforeCompletion?.Invoke();
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

        public void PrepareFreshTurn()
            => Invoke("PreparePendingTurnTracking", Chat, 1, 0);

        public Task SendNowAsync(ChatMessageViewModel message)
            => (Task)Invoke("SendSteeredNowAsync", message)!;

        public Task SendQueuedNowAfterTurnStartAsync()
            => (Task)Invoke("SendQueuedNowAfterTurnStartAsync", Chat.Id)!;

        public TaskCompletionSource<string> TrackPendingQuestion()
        {
            const string questionId = "q-send-now";
            QuestionMessage = new ChatMessage
            {
                Role = "tool",
                ToolName = "ask_question",
                ToolStatus = "InProgress",
                QuestionId = questionId
            };
            Chat.Messages.Add(QuestionMessage);

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Invoke("TrackPendingQuestion", Chat.Id, questionId, completion);
            return completion;
        }

        /// <summary>Adds a user message to the chat and its transcript view model, in a steer state.</summary>
        public ChatMessageViewModel AddTranscriptMessage(string content, MessageSteerState steerState)
        {
            var message = new ChatMessage { Role = "user", Content = content };
            Chat.Messages.Add(message);
            var viewModel = new ChatMessageViewModel(message) { SteerState = steerState };
            ViewModel.Messages.Add(viewModel);
            return viewModel;
        }

        public void ReleaseInactiveChat()
            => Invoke("ReleaseInactiveChatState", Chat, false, -1);

        public void ReleaseSessionResources()
            => Invoke("ReleaseSessionResources", Chat.Id, false);

        public void ApplyUnexpectedAbort()
            => Invoke("ApplyUnexpectedAbortState", Chat, "Connection to Copilot was lost.", true);

        public void MarkRuntimeBusy(bool turnInProgress = true)
        {
            var runtime = (ChatRuntimeState)Invoke("GetOrCreateRuntimeState", Chat.Id)!;
            runtime.IsBusy = true;
            runtime.TurnInProgress = turnInProgress;
        }

        public void MarkWorktreeCreationPending()
            => GetField<HashSet<Guid>>("_pendingWorktreeCreations").Add(Chat.Id);

        public void AttachSession(AbortRpc rpc, bool subscribe = false)
        {
            Chat.CopilotSessionId = rpc.Session.SessionId;
            GetField<Dictionary<Guid, CopilotSession>>("_sessionCache")[Chat.Id] = rpc.Session;
            SetField("_activeSession", rpc.Session);
            SetField("_activeSessionProviderSignature", ByokConfigHelper.BuildProviderSignature(null));
            if (subscribe)
                Invoke("SubscribeToSession", rpc.Session, Chat, Environment.CurrentDirectory, null);
        }

        public void RegisterPendingSteer(ChatMessageViewModel message)
            => Invoke("RegisterPendingSteer", Chat.Id, message);

        public void MarkRuntimeTerminal()
            => typeof(ChatViewModel)
                .GetMethod("MarkRuntimeTerminal", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Runtime, "Stopped"]);

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

    private sealed class AbortRpc : IDisposable
    {
        private readonly JsonRpc _rpc;
        private readonly IDisposable _sdkRpc;

        public AbortRpc()
        {
            var requests = new Pipe();
            var responses = new Pipe();
            _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                responses.Writer.AsStream(), requests.Reader.AsStream(), new JsonMessageFormatter()));
            _rpc.AddLocalRpcTarget(this);
            _rpc.StartListening();

            var rpcType = typeof(CopilotSession).Assembly.GetType("GitHub.Copilot.JsonRpc", throwOnError: true)!;
            _sdkRpc = (IDisposable)Activator.CreateInstance(
                rpcType,
                requests.Writer.AsStream(),
                responses.Reader.AsStream(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
                },
                NullLogger.Instance)!;
            rpcType.GetMethod("StartListening")!.Invoke(_sdkRpc, null);
            Session = (CopilotSession)Activator.CreateInstance(
                typeof(CopilotSession),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: [Guid.NewGuid().ToString(), _sdkRpc, NullLogger.Instance, null, null],
                culture: null)!;
            GC.SuppressFinalize(Session);
        }

        public CopilotSession Session { get; }
        public AbortResult Result { get; init; } = new() { Success = true };
        public TaskCompletionSource<AbortResult>? AbortReply { get; init; }
        public TaskCompletionSource AbortReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AbortCount { get; private set; }
        public int SendCount { get; private set; }
        public TaskCompletionSource SendReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DestroyCount { get; private set; }
        public string? LastSendMode { get; private set; }
        public HashSet<string> RunningShells { get; } = [];
        public List<string> CancelledShells { get; } = [];
        public bool RejectTaskCancellation { get; init; }
        public TaskCompletionSource<object>? TaskListReply { get; init; }
        public TaskCompletionSource TaskListReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod("session.abort", UseSingleObjectParameterDeserialization = true)]
        public async Task<AbortResult> Abort(object request)
        {
            AbortCount++;
            AbortReceived.TrySetResult();
            return AbortReply is null ? Result : await AbortReply.Task;
        }

        [JsonRpcMethod("session.destroy", UseSingleObjectParameterDeserialization = true)]
        public object Destroy(object request)
        {
            DestroyCount++;
            return new { };
        }

        [JsonRpcMethod("session.tasks.list", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> ListTasks(object request)
        {
            TaskListReceived.TrySetResult();
            return TaskListReply is null ? BuildTaskList() : await TaskListReply.Task;
        }

        public object BuildTaskList() => new
        {
            tasks = RunningShells.Select(id => new
            {
                type = "shell",
                id,
                status = "running",
                attachmentMode = "attached",
                executionMode = "background",
                command = "Start-Sleep -Seconds 300",
                description = "Debug process",
                startedAt = DateTimeOffset.UtcNow
            }).ToArray()
        };

        [JsonRpcMethod("session.tasks.cancel", UseSingleObjectParameterDeserialization = true)]
        public TasksCancelResult CancelTask(Dictionary<string, object> request)
        {
            var id = Assert.IsType<string>(request["id"]);
            if (RejectTaskCancellation || !RunningShells.Remove(id))
                return new TasksCancelResult { Cancelled = false };
            CancelledShells.Add(id);
            return new TasksCancelResult { Cancelled = true };
        }

        [JsonRpcMethod("session.send", UseSingleObjectParameterDeserialization = true)]
        public SendResult Send(Dictionary<string, object> request)
        {
            SendCount++;
            LastSendMode = request.GetValueOrDefault("mode") is { } mode ? Assert.IsType<string>(mode) : null;
            SendReceived.TrySetResult();
            return new SendResult { MessageId = Guid.NewGuid().ToString() };
        }

        public void RegisterTool(AIFunction tool)
            => typeof(CopilotSession).GetMethod("RegisterTools", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Session, [new AIFunctionDeclaration[] { tool }]);

        public Task BroadcastAsync(SessionEvent evt)
        {
            Emit(evt);
            return (Task)typeof(CopilotSession)
                .GetMethod("HandleBroadcastEventAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Session, [evt])!;
        }

        public void Emit(SessionEvent evt)
        {
            var subscriptions = (System.Collections.IEnumerable)typeof(CopilotSession)
                .GetField("_eventHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Session)!;
            foreach (var subscription in subscriptions)
            {
                var handler = (Action<SessionEvent>)subscription.GetType()
                    .GetProperty("Handler")!.GetValue(subscription)!;
                handler(evt);
            }
        }

        public void Disconnect() => _rpc.Dispose();

        public void Dispose()
        {
            _sdkRpc.Dispose();
            _rpc.Dispose();
        }
    }
}

#pragma warning restore GHCP001
