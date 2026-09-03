using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    // UI-thread only. Entries remain pending until the agent consumes the steer or the turn terminates.
    private readonly Dictionary<Guid, List<ChatMessageViewModel>> _pendingSteerConfirmations = new();

    private async Task<bool> SteerActiveTurnAsync(
        Chat activeChat,
        string prompt,
        bool consumeComposerPrompt,
        ChatMessage? queuedMessage = null,
        string? authorOverride = null,
        IReadOnlyCollection<string>? explicitAttachmentPaths = null)
    {
        if (BlockSendForByokOnly(
                activeChat,
                ResolveSelectedModelForChat(activeChat),
                prompt,
                consumeComposerPrompt))
        {
            return false;
        }

        var chatId = activeChat.Id;

        if (!_sessionCache.TryGetValue(chatId, out var session))
            session = _activeSession;

        _runtimeStates.TryGetValue(chatId, out var runtime);

        // Ordering guard: a new send must queue behind anything already deferred, or it would overtake
        // it. A non-null queuedMessage is the head the caller just dequeued, so nothing precedes it.
        var hasQueuedSends = queuedMessage is null
            && _queuedBusySendPrompts.TryGetValue(chatId, out var queued)
            && queued.Count > 0;

        // Privacy/routing guard: an immediate steer sends through the ALREADY-CACHED session, which was
        // built for whatever provider the chat used when the turn started. If the user has since changed
        // the model (or the session was invalidated for any reason), the cached session's provider no
        // longer matches the selected route, and steering would deliver the prompt to the WRONG backend
        // — exactly the UseBYOKOnly bypass. Treat such a state as non-steerable: queue a fresh turn that
        // will rebuild the session against the new provider. This must run before any attachment/transcript
        // mutation so a deferred prompt is neither consumed nor rendered as "steering".
        var sessionProviderMismatch =
            session is not null
            && !hasQueuedSends
            && !IsCachedSessionProviderConsistentWithSelection(chatId, session);

        if (hasQueuedSends || session is null || runtime is null || !CanSteerImmediately(runtime) || sessionProviderMismatch)
        {
            QueueSteerPrompt(chatId, prompt, queuedMessage, authorOverride, explicitAttachmentPaths);
            if (consumeComposerPrompt)
            {
                PromptText = "";
                _chatDrafts.Remove(chatId);
            }

            return true;
        }

        var attachments = queuedMessage is not null
            ? BuildUserMessageAttachments(queuedMessage.Attachments)
            : explicitAttachmentPaths is null
                ? TakePendingAttachments()
                : BuildUserMessageAttachments(explicitAttachmentPaths);

        // A deferred send is already rendered — promote that bubble instead of adding a second one. No
        // view model means the chat is off screen, so it stays queued for the drain path.
        ChatMessageViewModel? queuedViewModel = null;
        if (queuedMessage is not null)
        {
            queuedViewModel = ResolveQueuedViewModel(queuedMessage);
            if (queuedViewModel is null)
            {
                QueueSteerPrompt(chatId, prompt, queuedMessage, authorOverride, explicitAttachmentPaths);
                return true;
            }
        }

        var userMsg = queuedMessage ?? new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = authorOverride ?? _dataStore.Data.Settings.UserName ?? Loc.Author_You,
            ActiveSkills = BuildSkillReferences(ActiveSkillIds, _activeExternalSkillNames)
        };

        if (attachments is { Count: > 0 })
            userMsg.Attachments = attachments.OfType<AttachmentFile>().Select(a => a.Path).ToList();

        if (WorktreePath is { Length: > 0 } worktreePath && attachments is { Count: > 0 })
        {
            var projectDirectory = GetProjectWorkingDirectory();
            var effectiveWorktreeDirectory =
                GitService.ResolveWorktreeWorkingDirectory(worktreePath, projectDirectory);
            RebaseAttachmentPaths(
                attachments,
                userMsg,
                projectDirectory,
                effectiveWorktreeDirectory);
        }

        ChatMessageViewModel messageViewModel;
        if (queuedViewModel is not null)
        {
            messageViewModel = queuedViewModel;
            messageViewModel.SteerState = MessageSteerState.Steering;
        }
        else
        {
            activeChat.Messages.Add(userMsg);
            messageViewModel = new ChatMessageViewModel(userMsg)
            {
                SteerState = MessageSteerState.Steering
            };
            Messages.Add(messageViewModel);
        }

        // Register before SendAsync so a consumption event cannot race ahead of the pending entry.
        RegisterPendingSteer(chatId, messageViewModel);
        QueueSaveChat(activeChat, saveIndex: true, touchIndex: true);
        ChatUpdated?.Invoke();
        UserMessageSent?.Invoke();

        if (consumeComposerPrompt)
        {
            PromptText = "";
            _chatDrafts.Remove(chatId);
        }
        ClearSuggestions();

        var token = _ctsSources.TryGetValue(chatId, out var cts)
            ? cts.Token
            : CancellationToken.None;

        try
        {
            // Inside the try: `token` belongs to the turn being steered, so pressing Stop while
            // activation is in flight must be handled like a failed steer rather than escaping to
            // the dispatcher's unhandled-exception path.
            var skillDirectives = await ActivateTurnExternalSkillsAsync(
                session,
                activeChat,
                sessionLostHistory: false,
                token);

            var sendOptions = new MessageOptions
            {
                Prompt = skillDirectives + prompt + BuildSendPromptAdditions(targetChat: activeChat),
                Mode = GitHub.Copilot.Rpc.SendMode.Immediate.Value
            };
            ApplyMessageAttachments(sendOptions, attachments);

            // SendAsync confirms queue acceptance; the event stream confirms actual consumption.
            await AcquireByokRateSlotAsync(activeChat, token);

            // A sub-agent may have started while skills or rate limiting were awaited. Re-check the
            // delivery gate immediately before handing the message to the SDK; otherwise immediate mode
            // can inject it into the nested agent even though the send began on the parent trajectory.
            if (!CanSteerImmediately(runtime))
            {
                RequeueMaterializedSteer(chatId, prompt, userMsg, messageViewModel);
                return true;
            }

            // Revalidate the route/session consistency after the rate-limit await: the user (or a
            // configuration change) could have flipped the model/provider while we were waiting for a
            // slot. If so, do NOT inject into the now-stale session — unregister the pending steer,
            // restore its state, and requeue the prompt for a fresh turn.
            if (!IsCachedSessionProviderConsistentWithSelection(chatId, session!))
            {
                RequeueMaterializedSteer(chatId, prompt, userMsg, messageViewModel);
                return true;
            }

            await session.SendAsync(sendOptions, token);
            ClearPendingExternalSkillInjections();
            return true;
        }
        catch (Exception ex)
        {
            UnregisterPendingSteer(chatId, messageViewModel);
            if (messageViewModel.SteerState == MessageSteerState.Steering)
                messageViewModel.SteerState = MessageSteerState.Failed;

            Debug.WriteLine($"[Steer] Immediate send failed for chat {chatId}: {ex.Message}");
            // The message is already persisted and visible with a Failed delivery state. Treat the
            // command as accepted so the phone does not restore the same text into the composer and
            // duplicate it on retry; the transcript-level status is the delivery result.
            return true;
        }
    }

    private ChatMessage? QueueSteerPrompt(
        Guid chatId,
        string prompt,
        ChatMessage? queuedMessage,
        string? authorOverride,
        IReadOnlyCollection<string>? explicitAttachmentPaths)
    {
        if (queuedMessage is not null || explicitAttachmentPaths is null)
        {
            return QueueBusySendPrompt(chatId, prompt, queuedMessage, authorOverride);
        }

        // QueueBusySendPrompt intentionally snapshots the desktop composer. Present only the
        // explicitly supplied steering attachments while it materializes the queued message, then
        // restore the desktop draft synchronously. An explicit empty collection is how remote
        // steering says "no attachments"; it must never consume files staged in the local composer.
        var pendingAttachmentPaths = PendingAttachments.ToList();
        try
        {
            ReplacePendingAttachments(explicitAttachmentPaths);
            return QueueBusySendPrompt(chatId, prompt, queuedMessage, authorOverride);
        }
        finally
        {
            ReplacePendingAttachments(pendingAttachmentPaths);
        }
    }

    /// <summary>
    /// Injects a remotely-authored prompt into the currently running turn using the same immediate
    /// steering path as the desktop composer.
    ///
    /// <para>The remote server previously implemented steering as Stop -> wait -> fresh send. Besides
    /// being semantically wrong, that path could leave the original tool running to completion and
    /// process the user's steering text only afterward. This method keeps all of the desktop path's
    /// routing, provider-consistency, queue-ordering, attachment, and confirmation safeguards.</para>
    /// </summary>
    internal Task<bool> SteerExternalMessageAsync(Chat targetChat, string prompt, string author)
    {
        ArgumentNullException.ThrowIfNull(targetChat);
        if (CurrentChat?.Id != targetChat.Id)
            throw new InvalidOperationException("The target chat must be active before it can be steered.");

        return SteerActiveTurnAsync(
            targetChat,
            prompt,
            consumeComposerPrompt: false,
            queuedMessage: null,
            authorOverride: author,
            explicitAttachmentPaths: []);
    }

    internal async Task<bool> StopAndSendExternalMessageAsync(
        Chat targetChat,
        string prompt,
        string author,
        string? remoteDeviceId = null,
        string? remoteRequestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetChat);
        if (CurrentChat?.Id != targetChat.Id)
            throw new InvalidOperationException("The target chat must be active before it can be stopped.");

        var previousDeviceId = targetChat.LastRemoteDeviceId;
        var previousRequestId = targetChat.LastRemoteRequestId;
        if (!string.IsNullOrWhiteSpace(remoteDeviceId)
            && !string.IsNullOrWhiteSpace(remoteRequestId))
        {
            targetChat.LastRemoteDeviceId = remoteDeviceId;
            targetChat.LastRemoteRequestId = remoteRequestId;
        }

        var queuedMessage = QueueSteerPrompt(
            targetChat.Id,
            prompt,
            queuedMessage: null,
            authorOverride: author,
            explicitAttachmentPaths: []);
        if (queuedMessage is null)
        {
            targetChat.LastRemoteDeviceId = previousDeviceId;
            targetChat.LastRemoteRequestId = previousRequestId;
            return false;
        }

        queuedMessage.RemoteRequestId = remoteRequestId;
        if (!string.IsNullOrWhiteSpace(remoteDeviceId)
            && !string.IsNullOrWhiteSpace(remoteRequestId))
        {
            try
            {
                _dataStore.MarkChatChanged(targetChat);
                await _dataStore.SaveChatAsync(targetChat, cancellationToken).ConfigureAwait(true);
                await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                targetChat.LastRemoteDeviceId = previousDeviceId;
                targetChat.LastRemoteRequestId = previousRequestId;
                RemoveQueuedBusySend(targetChat.Id, queuedMessage);
                throw;
            }
        }

        if (!CanInterruptQueuedSendNowImmediately(targetChat.Id))
        {
            GetOrCreateRuntimeState(targetChat.Id).SendQueuedNowWhenTurnStarts = true;
            return true;
        }

        var error = await StopGenerationInternal(targetChat, resolvePendingSteersAsFailed: true);
        return error is null;
    }

    private void RequeueMaterializedSteer(
        Guid chatId,
        string prompt,
        ChatMessage userMessage,
        ChatMessageViewModel messageViewModel)
    {
        UnregisterPendingSteer(chatId, messageViewModel);
        if (messageViewModel.SteerState == MessageSteerState.Steering)
            messageViewModel.SteerState = MessageSteerState.Queued;
        messageViewModel.CanSendNowWhenQueued = true;

        // The message has already been inserted into the transcript and owns the consumed attachment
        // payload. Passing it as `existing` preserves that instance and restores it to the queue front.
        QueueBusySendPrompt(chatId, prompt, userMessage);
    }

    /// <summary>
    /// Returns true when the provider of the session that WOULD carry an immediate steer matches the
    /// provider of the currently selected model route. Used by <see cref="SteerActiveTurnAsync"/> to
    /// ensure a prompt is never steered through a session built for a different backend (the
    /// UseBYOKOnly privacy hole: selecting a BYOK model does not rebuild the active GitHub session, so
    /// an immediate steer would otherwise deliver the prompt to GitHub). A pending session invalidation
    /// always counts as inconsistent — the cached session is known-stale regardless of signatures.
    /// </summary>
    private bool IsCachedSessionProviderConsistentWithSelection(Guid chatId, CopilotSession session)
    {
        // A pending invalidation means the session is already known to be stale and will be rebuilt on
        // the next EnsureSessionAsync — never steer through it.
        if (_pendingSessionInvalidations.Contains(chatId))
            return false;

        // Resolve the chat for this session, preferring the current chat for the active session.
        var chat = CurrentChat;
        if (chat is null || chat.Id != chatId)
        {
            chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
        }
        // No resolvable chat means we cannot prove consistency — treat as non-steerable (safe default).
        if (chat is null)
            return false;

        var selectedRoute = ResolveModelRouteForChat(ResolveSelectedModelForChat(chat), chat);
        var selectedSignature = ByokConfigHelper.BuildProviderSignature(selectedRoute.Provider);

        // The signature of the session that will actually carry the steer.
        var activeSignature = session == _activeSession
            ? _activeSessionProviderSignature
            : _sessionProviderSignatures.GetValueOrDefault(chatId);

        return string.Equals(selectedSignature, activeSignature, StringComparison.Ordinal);
    }

    private void RegisterPendingSteer(Guid chatId, ChatMessageViewModel message)
    {
        if (!_pendingSteerConfirmations.TryGetValue(chatId, out var pendingMessages))
        {
            pendingMessages = [];
            _pendingSteerConfirmations[chatId] = pendingMessages;
        }

        pendingMessages.Add(message);
    }

    private void UnregisterPendingSteer(Guid chatId, ChatMessageViewModel message)
    {
        if (!_pendingSteerConfirmations.TryGetValue(chatId, out var pendingMessages))
            return;

        pendingMessages.Remove(message);
        if (pendingMessages.Count == 0)
            _pendingSteerConfirmations.Remove(chatId);
    }

    private void ConfirmOldestPendingSteer(Guid chatId)
    {
        if (!_pendingSteerConfirmations.TryGetValue(chatId, out var pendingMessages)
            || pendingMessages.Count == 0)
        {
            return;
        }

        var message = pendingMessages[0];
        pendingMessages.RemoveAt(0);
        if (pendingMessages.Count == 0)
            _pendingSteerConfirmations.Remove(chatId);

        if (message.SteerState == MessageSteerState.Steering)
            message.SteerState = MessageSteerState.Steered;
    }

    private void ResolvePendingSteersAsDelivered(Guid chatId)
        => ResolvePendingSteers(chatId, MessageSteerState.Steered);

    private void ResolvePendingSteersAsFailed(Guid chatId)
        => ResolvePendingSteers(chatId, MessageSteerState.Failed);

    private void ResolvePendingSteers(Guid chatId, MessageSteerState resolvedState)
    {
        if (!_pendingSteerConfirmations.Remove(chatId, out var pendingMessages))
            return;

        foreach (var message in pendingMessages)
        {
            if (message.SteerState == MessageSteerState.Steering)
                message.SteerState = resolvedState;
        }
    }

    private static bool CanSteerImmediately(ChatRuntimeState runtime)
        => Volatile.Read(ref runtime.ActiveSubagentExecutionDepth) == 0
           && !Volatile.Read(ref runtime.DeferSteersUntilNextTurn)
           && HasSubmittedCopilotTurn(runtime)
           && (runtime.TurnInProgress || runtime.ActiveToolCount > 0);

    private static bool HasSubmittedCopilotTurn(ChatRuntimeState runtime)
        => runtime.PendingSessionUserMessageCount > 0
           || runtime.ActiveToolCount > 0
           || Volatile.Read(ref runtime.ActiveSubagentExecutionDepth) > 0;

    private bool CanInterruptQueuedSendNowImmediately(Guid chatId)
        => _runtimeStates.TryGetValue(chatId, out var runtime)
           && (Volatile.Read(ref runtime.AssistantTurnStarted)
               || runtime.ActiveToolCount > 0
               || Volatile.Read(ref runtime.ActiveSubagentExecutionDepth) > 0
               || runtime.HasPendingBackgroundWork);

    private async Task SendQueuedNowAfterTurnStartAsync(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime)
            || !runtime.SendQueuedNowWhenTurnStarts)
        {
            return;
        }

        runtime.SendQueuedNowWhenTurnStarts = false;
        if (CurrentChat is not { } chat
            || chat.Id != chatId
            || !_queuedBusySendPrompts.ContainsKey(chatId))
        {
            return;
        }

        await StopGenerationInternal(chat, resolvePendingSteersAsFailed: true);
    }

    /// <summary>
    /// Delivers a still-pending message now instead of letting it wait for the running work to finish.
    /// An immediate-mode steer is injected by the SDK at the running turn's NEXT STEP BOUNDARY, so while
    /// a long tool call is in flight there is no boundary to inject at and the message waits for the
    /// whole tool. Answering "now" therefore means interrupting the turn and letting the message open a
    /// fresh one — which is what this does.
    /// </summary>
    /// <remarks>
    /// The message must be reclaimed into Lumi's queue BEFORE the abort. Aborting discards whatever the
    /// SDK was still holding, so stopping without reclaiming destroyed the very message the user asked
    /// to deliver, leaving it badged "Steering…" forever.
    /// </remarks>
    private async Task SendSteeredNowAsync(ChatMessageViewModel message)
    {
        if (message.SteerState is not (MessageSteerState.Steering or MessageSteerState.Queued))
            return;

        if (CurrentChat is not { } chat
            || !chat.Messages.Contains(message.Message)
            || !IsChatRuntimeActive(chat.Id))
        {
            return;
        }

        if (message.SteerState is MessageSteerState.Queued)
        {
            if (!MoveQueuedBusySendToFront(chat.Id, message.Message))
                return;

            // Session/MCP setup cannot be skipped, but it also must not be cancelled: that destroys the
            // half-ready session and starts the same setup again. Latch the request instead. The first
            // AssistantTurnStart interrupts the original prompt immediately and drains this selected
            // message through the already-ready session.
            if (!CanInterruptQueuedSendNowImmediately(chat.Id))
            {
                GetOrCreateRuntimeState(chat.Id).SendQueuedNowWhenTurnStarts = true;
                message.CanSendNowWhenQueued = false;
                return;
            }
        }

        // A queued message is already safe in the local queue; only a materialized steer is held by the
        // SDK and has to be taken back.
        if (message.SteerState is MessageSteerState.Steering)
            RequeueMaterializedSteer(chat.Id, message.Message.Content, message.Message, message);

        // Any OTHER steer the SDK was still holding dies with this abort, so mark those "Not delivered"
        // rather than leaving them pending against a turn that no longer exists. The reclaimed message
        // is already out of that set, and the drain scheduled by the stop starts its fresh turn.
        await StopGenerationInternal(chat, resolvePendingSteersAsFailed: true);
    }
}
