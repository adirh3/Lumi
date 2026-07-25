using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _copilotService.Reconnected -= OnCopilotReconnected;
        _copilotService.SessionDeletedRemotely -= OnSessionDeletedRemotely;
        _transcriptWindow.PropertyChanged -= OnTranscriptWindowPropertyChanged;

        // Detach the title-tracking subscription from the chat model. The chat outlives this surface
        // (it stays in DataStore.Data.Chats and MainViewModel keeps a running-state PropertyChanged
        // subscription on it), so leaving this handler attached pins the whole disposed surface — its
        // Messages, transcript turns, and realized Avalonia controls — in memory until app shutdown.
        if (_currentChatTitleSource is not null)
        {
            _currentChatTitleSource.PropertyChanged -= OnCurrentChatPropertyChanged;
            _currentChatTitleSource = null;
        }

        lock (_chatLoadSync)
        {
            _chatLoadRequestId++;
            try { _chatLoadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            _chatLoadCts?.Dispose();
            _chatLoadCts = null;
        }

        foreach (var chatId in _sessionCache.Keys
                     .Concat(_sessionsPendingResume.Keys)
                     .Concat(_ctsSources.Keys)
                     .Concat(_runtimeStates.Keys)
                     .Concat(_chatBrowserServices.Keys)
                     .Distinct()
                     .ToList())
        {
            ReleaseSessionResources(chatId, cancelActiveRequest: true, deleteServerSession: false);
            RemoveSuggestionTracking(chatId);
            DisposeBrowserService(chatId);
        }

        _runtimeStates.Clear();
        _pendingQuestions.Clear();
        _queuedBusySendPrompts.Clear();
        _inProgressMessages.Clear();
        _voiceService.Dispose();
        _modelSelectionSaveCts?.Cancel();
        _modelSelectionSaveCts?.Dispose();
        _modelSelectionSaveCts = null;
        _modelSelectionSyncCts?.Cancel();
        _modelSelectionSyncCts?.Dispose();
        _modelSelectionSyncCts = null;
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();
        _fileSearchCts = null;
        _gitRefreshThrottleCts?.Cancel();
        _gitRefreshThrottleCts?.Dispose();
        _gitRefreshThrottleCts = null;
    }

    /// <summary>
    /// Sheds this surface's realized transcript controls — the built StrataChatMessage / markdown /
    /// tool-call subtrees cached on each mounted turn — while keeping the surface's view-models and
    /// paging state intact. Called by <see cref="ChatSessionStore"/> for chats that are cached but no
    /// longer visible, so a pool of idle surfaces doesn't retain hundreds of live Avalonia controls
    /// each. The transcript re-realizes from its items through the normal frame-budgeted path the next
    /// time the surface is shown, so switching back stays smooth and never shows a blank transcript.
    /// </summary>
    internal void ReleaseRealizedTranscriptControls()
    {
        if (_isDisposed)
            return;

        // Mutating the cached hosts touches Avalonia controls, so it must run on the UI thread.
        // Surface caching happens during UI-thread navigation; guard defensively in case a caller
        // reaches here off-thread (e.g. a background streaming path releasing an idle surface).
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ReleaseRealizedTranscriptControls);
            return;
        }

        _transcriptWindow.ReleaseRealizedHosts("surface-idle");
    }

    private bool IsChatRuntimeActive(Guid chatId)
        => _runtimeStates.TryGetValue(chatId, out var runtime)
           && runtime.HasActiveWork;

    internal bool OwnsLiveChat(Guid chatId)
    {
        if (IsChatRuntimeActive(chatId)
            || _ctsSources.ContainsKey(chatId)
            || _inProgressMessages.ContainsKey(chatId))
            return true;

        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        return chat?.Messages.Any(message =>
            message.ToolName == "ask_question"
            && message.ToolStatus == "InProgress"
            && message.QuestionId is { Length: > 0 } questionId
            && _pendingQuestions.ContainsKey(questionId)) == true;
    }

    // A browser session outlives its chat's runtime state (it persists across chat switches), so a
    // surface can still hold one for a chat it no longer "owns". Deletion paths use this to ensure
    // the browser is torn down rather than leaking until app shutdown.
    internal bool HasBrowserService(Guid chatId) => _chatBrowserServices.ContainsKey(chatId);

    internal bool OwnsAnyLiveChat()
    {
        foreach (var chatId in _runtimeStates.Keys
                     .Concat(_ctsSources.Keys)
                     .Concat(_inProgressMessages.Keys)
                     .Distinct())
        {
            if (OwnsLiveChat(chatId))
                return true;
        }

        return _dataStore.Data.Chats.Any(chat =>
            chat.Messages.Any(message =>
                message.ToolName == "ask_question"
                && message.ToolStatus == "InProgress"
                && message.QuestionId is { Length: > 0 } questionId
                && _pendingQuestions.ContainsKey(questionId)));
    }

    /// <summary>
    /// Resolved on demand, never cached: transcript view models are rebuilt from the model on every chat
    /// switch, so a cached one would go stale and leave the bubble stuck showing "Queued…".
    /// </summary>
    private ChatMessageViewModel? ResolveQueuedViewModel(ChatMessage message)
        => Messages.FirstOrDefault(viewModel => ReferenceEquals(viewModel.Message, message));

    /// <summary>
    /// Defers a send that could not be steered into a live turn. FIFO, so a second deferred message
    /// cannot overwrite the first. Pass <paramref name="existing"/> to re-defer an already-shown
    /// message; that is always the head a delivery path just dequeued, so it goes back to the front.
    /// </summary>
    private void QueueBusySendPrompt(Guid chatId, string prompt, ChatMessage? existing = null)
    {
        if (existing is null && string.IsNullOrWhiteSpace(prompt))
            return;

        var message = existing ?? CreateQueuedBusySend(chatId, prompt);
        if (message is null)
            return;

        if (!_queuedBusySendPrompts.TryGetValue(chatId, out var pending))
        {
            pending = [];
            _queuedBusySendPrompts[chatId] = pending;
        }

        if (existing is null)
            pending.Add(message);
        else
            pending.Insert(0, message);
    }

    /// <summary>
    /// Shows a deferred send in the transcript straight away with a "Queued…" pill, so sending while the
    /// chat is busy is never invisible even though delivery has to wait.
    /// </summary>
    private ChatMessage? CreateQueuedBusySend(Guid chatId, string prompt)
    {
        var chat = CurrentChat?.Id == chatId
            ? CurrentChat
            : _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        if (chat is null)
            return null;

        var message = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = _dataStore.Data.Settings.UserName ?? Loc.Author_You,
            Model = SelectedModel,
            AgentId = ActiveAgent?.Id,
            SdkAgentName = SelectedSdkAgentName,
            HasAgentSelection = true,
            ActiveMcpServerNames = new List<string>(ActiveMcpServerNames),
            HasMcpSelection = true,
            ActiveSkills = BuildSkillReferences(ActiveSkillIds, _activeExternalSkillNames),
            SteerDelivery = MessageSteerState.Queued
        };

        // An assistant message that is still streaming lives only in _inProgressMessages until the turn
        // finalizes — and finalization APPENDS it. Persist it now so this follow-up cannot land ahead of
        // the answer it is replying to. Same instance, so later deltas keep mutating it in place, and
        // FinalizeTerminalAssistantMessage's id check stops it from being added twice.
        if (_inProgressMessages.TryGetValue(chatId, out var streaming)
            && !string.IsNullOrWhiteSpace(streaming.Content)
            && !chat.Messages.Contains(streaming))
        {
            chat.Messages.Add(streaming);
        }

        chat.Messages.Add(message);
        if (CurrentChat?.Id == chatId)
            Messages.Add(new ChatMessageViewModel(message));

        QueueSaveChat(chat, saveIndex: true, touchIndex: true);
        ChatUpdated?.Invoke();
        UserMessageSent?.Invoke();

        return message;
    }

    private bool TryDequeueBusySend(Guid chatId, out ChatMessage message)
    {
        message = null!;
        if (!_queuedBusySendPrompts.TryGetValue(chatId, out var pending) || pending.Count == 0)
            return false;

        message = pending[0];
        pending.RemoveAt(0);
        if (pending.Count == 0)
            _queuedBusySendPrompts.Remove(chatId);

        return true;
    }

    /// <summary>
    /// Delivers the oldest deferred send as a fresh turn once the chat is idle. Safe to call from any
    /// terminal/idle transition; the rest drain on the next one, in order.
    /// </summary>
    private async Task DrainQueuedBusySendAsync(Guid chatId)
    {
        if (!_queuedBusySendPrompts.ContainsKey(chatId))
            return;

        if (CurrentChat?.Id != chatId)
        {
            // A send can only be dispatched for the chat on screen.
            FailQueuedBusySends(chatId);
            return;
        }

        if (IsChatRuntimeActive(chatId))
            return;

        // Dequeue only once the send is certain, so the message cannot lose its place in the queue.
        if (!TryDequeueBusySend(chatId, out var message))
            return;

        await SendMessageCore(message.Content, consumeComposerPrompt: false, message);
    }

    /// <summary>
    /// Always posts, never runs inline, so callers can invoke it from the middle of a session-event
    /// handler. A single Stop schedules two drains (the stop and the session.idle it causes), so drains
    /// are coalesced per chat — two overlapping sends would fight over the same cancellation token.
    /// </summary>
    private void ScheduleQueuedBusySendDrain(Guid chatId)
    {
        if (_isDisposed)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed
                || !_queuedBusySendPrompts.ContainsKey(chatId)
                || !_drainingBusySends.Add(chatId))
            {
                return;
            }

            _ = DrainQueuedBusySendSafeAsync(chatId);
        });
    }

    private async Task DrainQueuedBusySendSafeAsync(Guid chatId)
    {
        try
        {
            await DrainQueuedBusySendAsync(chatId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Send] Deferred send drain failed for chat {chatId}: {ex.Message}");
        }
        finally
        {
            _drainingBusySends.Remove(chatId);
        }
    }

    /// <summary>
    /// Gives up on every deferred send for a chat. The messages stay in the transcript flagged "not
    /// delivered" so they can be resent in place, rather than being silently discarded.
    /// </summary>
    private void FailQueuedBusySends(Guid chatId)
    {
        if (!_queuedBusySendPrompts.Remove(chatId, out var pending))
            return;

        foreach (var message in pending)
            MarkQueuedBusySendFailed(message);
    }

    private void MarkQueuedBusySendFailed(ChatMessage message)
    {
        if (message.SteerDelivery != MessageSteerState.Queued)
            return;

        // The view model mirrors its state onto the model; without one, the model is all there is.
        if (ResolveQueuedViewModel(message) is { } viewModel)
            viewModel.SteerState = MessageSteerState.Failed;
        else
            message.SteerDelivery = MessageSteerState.Failed;
    }

    /// <summary>
    /// Flushes deferred sends into a turn that just became steerable, so they land in the running
    /// response instead of waiting for the whole run to finish. Skipped while a stop is pending — those
    /// are deliberately held back to start a fresh turn after the abort.
    /// </summary>
    private bool CanFlushQueuedSendsAsSteer(Guid chatId)
        => !_isDisposed
            && _queuedBusySendPrompts.ContainsKey(chatId)
            && _runtimeStates.TryGetValue(chatId, out var runtime)
            && !runtime.ManualStopRequested
            && !WasCancelledByUser(chatId)
            && CanSteerImmediately(runtime);

    private async Task FlushQueuedBusySendsAsSteerAsync(Guid chatId)
    {
        // Called straight from the turn-start handler on the UI thread, so the gate sees the runtime
        // state that handler just applied.
        if (CurrentChat?.Id != chatId || !CanFlushQueuedSendsAsSteer(chatId))
            return;

        // One at a time rather than emptying the list up front, so a send arriving mid-flush lines up
        // behind these instead of overtaking them.
        while (TryDequeueBusySend(chatId, out var message))
        {
            if (CurrentChat is not { } chat || chat.Id != chatId)
            {
                QueueBusySendPrompt(chatId, message.Content, message);
                return;
            }

            try
            {
                await SteerActiveTurnAsync(chat, message.Content, consumeComposerPrompt: false, message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Send] Deferred steer flush failed for chat {chatId}: {ex.Message}");
                QueueBusySendPrompt(chatId, message.Content, message);
                return;
            }

            // It re-deferred itself, so the turn is no longer steerable. Continuing would send later
            // messages ahead of it.
            if (IsQueuedBusySend(chatId, message))
                return;
        }
    }

    private bool IsQueuedBusySend(Guid chatId, ChatMessage message)
        => _queuedBusySendPrompts.TryGetValue(chatId, out var pending) && pending.Contains(message);

    private void ReleaseChatCancellation(Guid chatId, bool cancel)
    {
        if (!_ctsSources.Remove(chatId, out var cts))
            return;

        try
        {
            if (cancel)
                cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool ReleasePreviousTurnCancellation(Guid chatId)
    {
        if (!_ctsSources.ContainsKey(chatId))
            return false;

        if (IsChatRuntimeActive(chatId))
        {
            ReleaseChatCancellation(chatId, cancel: true);
            return true;
        }

        // The Copilot SDK may still hold the token after an idle turn. Drop our
        // reference, but don't cancel/dispose it while the session is being reused.
        _ctsSources.Remove(chatId);
        return false;
    }

    private void DropCompletedTurnState(Guid chatId, bool dropCancellation)
    {
        _inProgressMessages.Remove(chatId);

        if (!dropCancellation)
            return;

        // SessionIdle is emitted after background work is drained. Drop our
        // reference without cancelling/disposal, matching ReleasePreviousTurnCancellation.
        _ctsSources.Remove(chatId);
    }

    private void DisposeSessionSubscription(Guid chatId)
    {
        if (_sessionSubs.TryGetValue(chatId, out var sub))
        {
            sub.Dispose();
            _sessionSubs.Remove(chatId);
        }
        _activeMcpConfigs.TryRemove(chatId, out _);
        ForgetMcpOAuthState(chatId);
    }

    private void RemoveSuggestionTracking(Guid chatId)
    {
        _suggestionGenerationInFlightChats.Remove(chatId);
        _lastSuggestedAssistantMessageByChat.Remove(chatId);
    }

    private void DisposeBrowserService(Guid chatId)
    {
        if (_chatBrowserServices.TryRemove(chatId, out var browserSvc))
        {
            _ = browserSvc.DisposeAsync();
        }
    }

    /// <summary>
    /// Cancels a chat's tracked question tasks and marks any unanswered <c>ask_question</c> tool
    /// messages as expired. Returns <c>true</c> when it mutated a persisted message field, so the
    /// caller knows the chat's on-disk snapshot is now stale and must not be unloaded.
    /// </summary>
    private bool CancelPendingQuestions(Chat chat)
    {
        var pendingQuestionIds = chat.Messages
            .Where(static m => !string.IsNullOrWhiteSpace(m.QuestionId))
            .Select(static m => m.QuestionId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var questionId in pendingQuestionIds)
        {
            if (_pendingQuestions.TryGetValue(questionId, out var tcs))
            {
                tcs.TrySetCanceled();
                _pendingQuestions.Remove(questionId);
            }
        }

        // Mark unanswered ask_question tool messages as Failed so rebuild renders them as expired
        var markedExpired = false;
        var expiredAt = DateTimeOffset.UtcNow;
        foreach (var msg in chat.Messages)
        {
            if (msg.ToolName == "ask_question"
                && msg.ToolStatus == "InProgress"
                && string.IsNullOrEmpty(msg.ToolOutput))
            {
                msg.MarkToolFinished(expiredAt);
                msg.ToolStatus = "Failed";
                markedExpired = true;
            }
        }

        // Expire any live QuestionItem cards in the current transcript
        ExpireUnansweredQuestions(chat.Id);
        return markedExpired;
    }

    private bool MarkInProgressToolsStopped(Chat chat)
    {
        List<Guid>? stoppedMessageIds = null;
        var stoppedAt = DateTimeOffset.UtcNow;

        foreach (var message in chat.Messages)
        {
            if (message.ToolStatus != "InProgress" || string.IsNullOrWhiteSpace(message.ToolName))
                continue;

            message.MarkToolFinished(stoppedAt);
            message.ToolStatus = "Stopped";
            (stoppedMessageIds ??= []).Add(message.Id);
        }

        if (stoppedMessageIds is null)
            return false;

        if (CurrentChat?.Id == chat.Id)
        {
            var stoppedIds = stoppedMessageIds.ToHashSet();
            foreach (var vm in Messages.Where(vm => stoppedIds.Contains(vm.Message.Id)))
                vm.NotifyToolStatusChanged();
        }

        return true;
    }

    /// <summary>Sets IsExpired on all unanswered QuestionItems in the live transcript for the given chat.</summary>
    private void ExpireUnansweredQuestions(Guid chatId)
    {
        if (CurrentChat?.Id != chatId) return;

        foreach (var turn in TranscriptTurns)
        {
            foreach (var item in turn.Items)
            {
                if (item is QuestionItem q && !q.IsAnswered && !q.IsExpired)
                    q.IsExpired = true;
            }
        }
    }

    private void ReleaseSessionResources(Guid chatId, bool cancelActiveRequest, bool deleteServerSession)
    {
        // Drop any still-pending steer confirmations for this chat. Without this a chat deleted / released
        // while a steer is in flight leaks its entry (and the referenced ChatMessageViewModel), and — because
        // a remote-shutdown keeps CopilotSessionId for resume — a later Retry's turn-start echo could pop the
        // stale steer and flip it to a false "Steered into response".
        _pendingSteerConfirmations.Remove(chatId);
        ReleaseChatCancellation(chatId, cancelActiveRequest);
        ClearPendingTurnTracking(chatId);
        DisposeSessionSubscription(chatId);

        if (_sessionCache.Remove(chatId, out var session))
            TrackSessionRelease(chatId, session, deleteServerSession);

        if (_sessionsPendingResume.Remove(chatId, out var pendingSession)
            && (session is null
                || (!ReferenceEquals(pendingSession, session)
                    && !string.Equals(
                        pendingSession.SessionId,
                        session.SessionId,
                        StringComparison.Ordinal))))
        {
            TrackSessionRelease(chatId, pendingSession, deleteServerSession);
        }

        _inProgressMessages.Remove(chatId);
    }

    /// <summary>
    /// Starts an asynchronous release of a dropped Copilot session and tracks the in-flight task
    /// per chat. This is the single choke point for handing a session to disposal: releasing sends
    /// <c>session.destroy</c>, which reaps the session's host process and its MCP subprocesses.
    /// Simply dropping a session reference instead orphans those MCP subprocesses forever, because
    /// <c>CopilotSession</c>'s finalizer only removes it from the client dictionary and never sends
    /// destroy. Recording the task in <see cref="_sessionReleaseTasks"/> lets a subsequent
    /// create/resume for the same chat on THIS surface await it via
    /// <see cref="AwaitPendingSessionReleaseAsync"/>. Cross-surface sequencing — a *different*
    /// ChatViewModel resuming the same server session id — is handled by CopilotService, which
    /// registers the release by session id (<see cref="Services.CopilotService.ReleaseSessionAsync"/>)
    /// and awaits it inside <see cref="Services.CopilotService.ResumeSessionAsync"/>.
    /// </summary>
    private void TrackSessionRelease(Guid chatId, CopilotSession session, bool deleteServerSession)
    {
        var releaseTask = DisposeReleasedSessionAsync(session, deleteServerSession);
        _sessionReleaseTasks[chatId] = releaseTask;
        _ = releaseTask.ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (_sessionReleaseTasks.TryGetValue(chatId, out var trackedTask)
                    && ReferenceEquals(trackedTask, releaseTask))
                {
                    _sessionReleaseTasks.Remove(chatId);
                }
            }),
            TaskScheduler.Default);
    }

    // Routes every dropped session through CopilotService so the release is registered by server
    // session id — this is what lets a concurrent resume of the same id on ANOTHER surface wait for
    // the destroy to finish. The service owns fault-swallowing, so this best-effort call never
    // faults its caller.
    private Task DisposeReleasedSessionAsync(CopilotSession session, bool deleteServerSession)
        => _copilotService.ReleaseSessionAsync(session, deleteServerSession);

    private async Task AwaitPendingSessionReleaseAsync(Guid chatId, CancellationToken ct)
    {
        if (!_sessionReleaseTasks.TryGetValue(chatId, out var releaseTask))
            return;

        try
        {
            await releaseTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Failed while waiting for released session for chat {chatId}: {ex.Message}");
        }

        // Do NOT remove from _sessionReleaseTasks here: after ConfigureAwait(false) this runs on a
        // thread-pool thread, and _sessionReleaseTasks is a plain Dictionary mutated everywhere else
        // on the UI thread — touching it here would be a cross-thread mutation that can corrupt it.
        // Removal is already owned by TrackSessionRelease's completion continuation, which marshals
        // back to the UI thread and drops the entry (guarded by the same ReferenceEquals check).
    }

    private void ReleaseInactiveChatState(Chat chat, bool unloadMessages = false, int expectedMessageCount = -1)
    {
        if (CurrentChat?.Id == chat.Id || IsChatRuntimeActive(chat.Id))
            return;

        // CancelPendingQuestions can flip an unanswered ask_question tool message to "Failed" in
        // memory. That mutation happens AFTER the caller persisted this chat, so the on-disk
        // snapshot no longer matches memory. Unloading would then discard the Failed state and
        // reload the question as a stuck "live" card on next open, so skip the message unload when
        // it mutated — the chat becomes unloadable again once a later save persists the new state.
        var mutatedPersistedMessages = CancelPendingQuestions(chat);
        // The chat is neither displayed nor running, so nothing is left to deliver a deferred send.
        FailQueuedBusySends(chat.Id);
        ReleaseSessionResources(chat.Id, cancelActiveRequest: false, deleteServerSession: false);
        RemoveSuggestionTracking(chat.Id);
        // Intentionally keep the chat's BrowserService alive. A browser session belongs to the
        // chat, not its transient runtime state, so switching away and back restores the page
        // instead of losing the browser (and its toggle button). The service is disposed when the
        // chat is deleted (CleanupSession) or the app shuts down (Dispose).
        _runtimeStates.Remove(chat.Id);

        if (unloadMessages && !mutatedPersistedMessages)
            TryUnloadInactiveChatMessages(chat, expectedMessageCount);
    }

    /// <summary>
    /// Releases the in-memory <see cref="Chat.Messages"/> of an inactive chat to reclaim RAM.
    /// Every chat opened during a session otherwise stays fully loaded for the app's lifetime,
    /// so a long session that browses many (or large) chats accumulates their message models
    /// on the managed heap. The messages are lazily reloaded from the per-chat file on next open.
    ///
    /// Only ever called after the caller has just durably persisted the chat's messages, and it
    /// re-verifies the live count still matches what was persisted so a message added between the
    /// save and this (UI-thread) call is never dropped. Skipped when auto-save is off (no file to
    /// reload from) or the file is missing.
    /// </summary>
    private void TryUnloadInactiveChatMessages(Chat chat, int expectedMessageCount)
    {
        if (chat.Messages.Count == 0)
            return;

        // Never unload the visible chat or one with live work — belt-and-suspenders on top of the
        // ReleaseInactiveChatState guard, since this can run after an await hop.
        if (CurrentChat?.Id == chat.Id || IsChatRuntimeActive(chat.Id))
            return;

        // Only safe when the messages are durably on disk and unchanged since we persisted them.
        if (!_dataStore.Data.Settings.AutoSaveChats)
            return;
        if (expectedMessageCount >= 0 && chat.Messages.Count != expectedMessageCount)
            return;
        if (!_dataStore.HasStoredMessages(chat.Id))
            return;

        chat.MessageCount = chat.Messages.Count;
        chat.Messages.Clear();
        chat.Messages.TrimExcess();
    }

    /// <summary>
    /// Sweeps all tracked runtime states and releases any that belong to chats
    /// that are no longer active (not busy, not streaming, not the current chat).
    /// Call this on chat switch to catch states that event-driven cleanup missed
    /// (e.g. chats whose background work completed but cleanup was skipped).
    /// </summary>
    private void SweepInactiveChatStates()
    {
        var currentChatId = CurrentChat?.Id;
        var staleIds = _runtimeStates
            .Where(kvp => kvp.Key != currentChatId
                          && !kvp.Value.HasActiveWork)
            .Select(static kvp => kvp.Key)
            .ToList();

        foreach (var chatId in staleIds)
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
            if (chat is not null)
                ReleaseInactiveChatState(chat);
            else
            {
                // Chat was deleted but runtime state lingered — clean up directly
                ReleaseSessionResources(chatId, cancelActiveRequest: false, deleteServerSession: false);
                RemoveSuggestionTracking(chatId);
                DisposeBrowserService(chatId);
                _runtimeStates.Remove(chatId);
            }
        }
    }

}
