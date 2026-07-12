using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

/// <summary>
/// Copilot session subscription, runtime restoration, and per-chat session cleanup.
/// </summary>
public partial class ChatViewModel
{
    private const int StreamingUiUpdateThrottleMs = 50;

    internal static string? ResolveFinalAssistantContent(
        string? finalEventContent,
        string? streamedContent,
        string? existingStreamingContent)
    {
        var finalContent = NormalizeAssistantContent(finalEventContent);
        if (!string.IsNullOrWhiteSpace(finalContent))
            return finalContent;

        var streamContent = NormalizeAssistantContent(streamedContent);
        if (!string.IsNullOrWhiteSpace(streamContent))
            return streamContent;

        var existingContent = NormalizeAssistantContent(existingStreamingContent);
        return string.IsNullOrWhiteSpace(existingContent) ? null : existingContent;
    }

    internal static bool ShouldDeferAssistantMessageEvent(string? finalEventContent, string? phase)
        => string.IsNullOrWhiteSpace(finalEventContent) && string.IsNullOrWhiteSpace(phase);

    internal static bool FinalizeTerminalAssistantMessage(Chat chat, ChatMessage streamingMessage)
    {
        streamingMessage.IsStreaming = false;
        if (string.IsNullOrWhiteSpace(streamingMessage.Content))
            return false;

        if (!chat.Messages.Any(message => message.Id == streamingMessage.Id))
            chat.Messages.Add(streamingMessage);

        return true;
    }

    internal static void FinalizeTerminalReasoningMessage(ChatMessage reasoningMessage)
        => reasoningMessage.IsStreaming = false;

    private static string? NormalizeAssistantContent(string? content)
        => content?.TrimStart('\n', '\r');

    private static ChatMessage? AttachSourcesToFinalAssistantMessage(
        Chat chat,
        IReadOnlyList<SearchSource> fetchedSources)
    {
        if (fetchedSources.Count == 0)
            return null;

        ChatMessage? target = null;
        for (var i = chat.Messages.Count - 1; i >= 0; i--)
        {
            var message = chat.Messages[i];
            if (message.Role == "assistant")
            {
                target = message;
                break;
            }

            if (message.Role == "user")
                break;
        }

        if (target is null)
            return null;

        var added = false;
        foreach (var source in fetchedSources)
        {
            if (target.Sources.Any(existing => string.Equals(existing.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
                continue;

            target.Sources.Add(source);
            added = true;
        }

        return added ? target : null;
    }

    /// <summary>
    /// Whether sub-agent output suppression is currently active for a chat. Driven SOLELY by
    /// genuine nested sub-agent execution (<c>subagent.started</c>/<c>subagent.completed</c>,
    /// tracked by <see cref="ChatRuntimeState.ActiveSubagentExecutionDepth"/>). The
    /// <c>subagent.selected</c>/<c>subagent.deselected</c> events must NOT feed into this — the
    /// CLI emits them only for the top-level configured agent (config.Agent), so gating on them
    /// dropped the entire main turn whenever a Lumi agent was selected.
    /// </summary>
    internal static bool SubagentOutputIsActive(ChatRuntimeState runtime)
        => Volatile.Read(ref runtime.ActiveSubagentExecutionDepth) > 0;

    // A successful task-tool completion only means the wrapper spawned the sub-agent.
    // Keep the card live until the authoritative subagent.completed/subagent.failed event.
    internal static string ResolveToolStartStatus(string? toolName, string? completedStatus)
        => ToolDisplayHelper.IsSubagentTool(toolName) && completedStatus == "Completed"
            ? "InProgress"
            : completedStatus ?? "InProgress";

    internal static bool ShouldApplyToolExecutionCompletionStatus(string? toolName, bool success)
        => !success || !ToolDisplayHelper.IsSubagentTool(toolName);

    internal static IReadOnlyList<ChatMessage> SetInProgressSubagentStatuses(
        Chat chat,
        string terminalStatus)
    {
        if (terminalStatus is not ("Completed" or "Failed" or "Stopped"))
            throw new ArgumentOutOfRangeException(nameof(terminalStatus), terminalStatus, "Expected a terminal tool status.");

        var changed = new List<ChatMessage>();
        foreach (var message in chat.Messages)
        {
            if (!string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(message.ToolStatus, "InProgress", StringComparison.OrdinalIgnoreCase)
                || !ToolDisplayHelper.IsSubagentTool(message.ToolName))
            {
                continue;
            }

            message.ToolStatus = terminalStatus;
            changed.Add(message);
        }

        return changed;
    }

    private static bool HasInProgressSubagentTools(Chat chat)
        => chat.Messages.Any(message =>
            string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase)
            && string.Equals(message.ToolStatus, "InProgress", StringComparison.OrdinalIgnoreCase)
            && ToolDisplayHelper.IsSubagentTool(message.ToolName));

    private void ReconcileInProgressSubagentTools(
        Chat chat,
        string terminalStatus,
        bool updateDisplayedChatUi = true)
    {
        var changed = SetInProgressSubagentStatuses(chat, terminalStatus);
        if (changed.Count == 0 || !updateDisplayedChatUi || CurrentChat?.Id != chat.Id)
            return;

        foreach (var message in changed)
        {
            var viewModel = Messages.FirstOrDefault(candidate =>
                ReferenceEquals(candidate.Message, message) || candidate.Message.Id == message.Id);
            viewModel?.NotifyToolStatusChanged();

            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
                _transcriptBuilder.UpdateSubagentToolStatus(message.ToolCallId, terminalStatus);
        }
    }

    /// <summary>Subscribes to events on a CopilotSession. Each subscription captures its own
    /// streaming state via closures and always updates the Chat model. UI updates are gated
    /// on _activeSession so only the displayed chat's events touch the UI.
    /// Returns <c>false</c> when this surface was disposed while the session was being
    /// created/resumed: the incoming session is released (not subscribed) and callers must NOT
    /// publish it as <c>_activeSession</c> or send on it.</summary>
    private bool SubscribeToSession(CopilotSession session, Chat chat, string workDir)
    {
        // Dispose previous subscription for this chat (e.g., session was resumed)
        if (_sessionSubs.TryGetValue(chat.Id, out var oldSub))
            oldSub.Dispose();

        // A resume/create for this chat can hand us a new CopilotSession object for a *different*
        // server session than the one still cached here (the send guard only re-enters session setup
        // when the cached handle's id no longer matches, and create assigns a brand-new id).
        // Overwriting the cache entry would drop that old session WITHOUT sending session.destroy,
        // orphaning its host process and MCP subprocesses forever — GC never reaps them because
        // CopilotSession's finalizer only calls RemoveFromClient. Release it explicitly. We guard on
        // the server id: a same-id handle shares the one server session (and its MCP) with the
        // incoming one, so destroying it would tear down the session we are about to use.
        if (_sessionCache.TryGetValue(chat.Id, out var previousSession)
            && !ReferenceEquals(previousSession, session)
            && !string.Equals(previousSession.SessionId, session.SessionId, StringComparison.Ordinal))
        {
            _sessionCache.Remove(chat.Id);
            TrackSessionRelease(chat.Id, previousSession, deleteServerSession: false);
        }

        // This surface may have been disposed while the session was being created/resumed (the user
        // switched away and the pool evicted us mid-await). Dispose() already swept _sessionCache, so
        // caching now would strand this session: nothing would ever release it, leaking its MCP
        // subprocesses. Release it immediately instead of subscribing.
        if (_isDisposed)
        {
            TrackSessionRelease(chat.Id, session, deleteServerSession: false);
            return false;
        }

        _sessionCache[chat.Id] = session;

        // Per-session streaming state — captured by closure, independent per subscription
        ChatMessage? streamingMsg = null;
        ChatMessage? reasoningMsg = null;
        ChatMessageViewModel? streamingVm = null;
        ChatMessageViewModel? reasoningVm = null;
        string? turnModelId = null;
        var agentName = chat.AgentId.HasValue
            ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == chat.AgentId.Value)?.Name ?? Loc.Author_Lumi
            : Loc.Author_Lumi;
        var runtime = GetOrCreateRuntimeState(chat.Id);
        var projectContextCatalog = GetProjectContextCatalog(chat, workDir);
        var toolParentById = new Dictionary<string, string?>(StringComparer.Ordinal);
        var terminalRootByToolCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        var externalToolCallIdByRequestId = new Dictionary<string, string>(StringComparer.Ordinal);
        var completedToolStatusesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        var completedToolOutputsByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        StreamingTextAccumulator? assistantStream = null;
        StreamingTextAccumulator? reasoningStream = null;
        var subagentStateGate = new object();
        var activeSubagentToolCallIds = new List<string>();
        var subagentAssistantStreams = new Dictionary<string, StreamingTextAccumulator>(StringComparer.Ordinal);
        var subagentReasoningStreams = new Dictionary<string, StreamingTextAccumulator>(StringComparer.Ordinal);
        string? mostRecentSubagentToolCallId = null;
        var pendingFetchedSources = new List<SearchSource>();
        var pendingFetchedSkillRefs = new List<SkillReference>();

        static void AddPendingSource(List<SearchSource> sources, SearchSource source)
        {
            if (sources.Any(existing => string.Equals(existing.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
                return;

            sources.Add(source);
        }

        void AttachPendingSourcesToFinalAssistantMessage()
        {
            if (pendingFetchedSources.Count == 0)
                return;

            var updatedMessage = AttachSourcesToFinalAssistantMessage(chat, pendingFetchedSources);
            pendingFetchedSources.Clear();

            if (updatedMessage is not null && IsDisplayedSession())
            {
                // Update the sources section in place. A full RebuildTranscript() here would
                // re-parse the entire mounted tail's markdown (heavy for long answers), which is
                // the stutter felt when a web-search chat finishes writing. Fall back to a rebuild
                // only if the live assistant item can't be found.
                if (!_transcriptBuilder.RefreshAssistantSources(updatedMessage))
                    RebuildTranscript();
            }
        }

        bool IsDisplayedSession() => CurrentChat?.Id == chat.Id && _activeSession == session;

        bool IsAuthoritativeSession()
            => !_sessionCache.TryGetValue(chat.Id, out var cachedSession)
               || ReferenceEquals(cachedSession, session);

        // Subscribe to CLI process exit — fires instantly when the process is killed,
        // unlike SDK events which go silent on crash.
        var cliExitGeneration = _copilotService.ConnectionGeneration;
        var cliExitHandled = 0;
        void OnCliProcessExited(long exitedGeneration)
        {
            if (exitedGeneration != cliExitGeneration) return;
            if (Interlocked.CompareExchange(ref cliExitHandled, 1, 0) != 0) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!runtime.IsBusy
                    && streamingMsg is null
                    && reasoningMsg is null
                    && !HasInProgressSubagentTools(chat))
                    return;

                var shouldUpdateDisplayedChatUi = CurrentChat?.Id == chat.Id
                    && (!_sessionCache.TryGetValue(chat.Id, out var cachedSession)
                        || ReferenceEquals(cachedSession, session));

                ClearPendingTurnTracking(chat.Id);
                ClearManualStopRequested(chat.Id);
                assistantStream?.CancelPending();
                reasoningStream?.CancelPending();
                FlushAssistantDelta();

                if (streamingMsg is not null)
                {
                    streamingMsg.IsStreaming = false;
                    _inProgressMessages.Remove(chat.Id);
                    if (!string.IsNullOrWhiteSpace(streamingMsg.Content))
                    {
                        chat.Messages.Add(streamingMsg);
                        if (shouldUpdateDisplayedChatUi)
                            streamingVm?.NotifyStreamingEnded();
                    }
                    else if (shouldUpdateDisplayedChatUi && streamingVm is not null)
                    {
                        Messages.Remove(streamingVm);
                    }

                    streamingMsg = null;
                    streamingVm = null;
                }
                assistantStream?.Clear();

                if (reasoningMsg is not null)
                {
                    reasoningMsg.IsStreaming = false;
                    if (shouldUpdateDisplayedChatUi)
                        reasoningVm?.NotifyStreamingEnded();
                    reasoningMsg = null;
                    reasoningVm = null;
                }
                reasoningStream?.Clear();

                if (IsAuthoritativeSession())
                    ReconcileInProgressSubagentTools(chat, "Failed");
                MarkRuntimeTerminal(runtime);
                var wasActive = _activeSession == session;
                if (shouldUpdateDisplayedChatUi)
                {
                    IsBusy = false;
                    IsStreaming = false;
                    StatusText = "";
                    _transcriptBuilder.HideTypingIndicator();
                    _transcriptBuilder.CloseCurrentToolGroup();
                    _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                    _transcriptBuilder.FlushPendingFileEdits();

                    _transcriptBuilder.AddConnectionLostError(
                        "Connection to Copilot was lost.",
                        new RelayCommand(() => _ = RetryAfterConnectionLossAsync()));
                    ScrollToEndRequested?.Invoke();
                }

                DetachSessionAfterRemoteShutdown(chat, wasActive: wasActive);
                QueueSaveChat(chat, saveIndex: true, releaseIfInactive: CurrentChat?.Id != chat.Id, touchIndex: true);

                // Reconnect is handled by CopilotService.AutoReconnectAndNotifyAsync —
                // no need to call ForceReconnectAsync from per-session handlers.
            });
        }
        _copilotService.CliProcessExited += OnCliProcessExited;


        bool IsSubagentOutputActive()
            => SubagentOutputIsActive(runtime);

        static string? GetSubagentToolCallIdFromParent(string? parentToolCallId)
            => string.IsNullOrWhiteSpace(parentToolCallId) ? null : parentToolCallId;

        string? GetActiveSubagentToolCallId()
        {
            lock (subagentStateGate)
                return activeSubagentToolCallIds.Count == 0 ? null : activeSubagentToolCallIds[^1];
        }

        string? GetCurrentSubagentOutputToolCallId()
        {
            var activeToolCallId = GetActiveSubagentToolCallId();
            if (!string.IsNullOrWhiteSpace(activeToolCallId))
                return activeToolCallId;

            if (!IsSubagentOutputActive())
                return null;

            lock (subagentStateGate)
                return mostRecentSubagentToolCallId;
        }

        void RegisterActiveSubagent(string? toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
                return;

            lock (subagentStateGate)
            {
                activeSubagentToolCallIds.Add(toolCallId);
                mostRecentSubagentToolCallId = toolCallId;
            }
        }

        bool UnregisterActiveSubagent(string? toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
                return false;

            lock (subagentStateGate)
            {
                var removed = false;
                for (var i = activeSubagentToolCallIds.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(activeSubagentToolCallIds[i], toolCallId, StringComparison.Ordinal))
                        continue;

                    activeSubagentToolCallIds.RemoveAt(i);
                    removed = true;
                    break;
                }

                mostRecentSubagentToolCallId = activeSubagentToolCallIds.Count > 0
                    ? activeSubagentToolCallIds[^1]
                    : toolCallId;
                return removed;
            }
        }

        StreamingTextAccumulator GetOrCreateSubagentStream(
            Dictionary<string, StreamingTextAccumulator> streams,
            string toolCallId,
            int initialCapacity,
            Action<string> flushAction)
        {
            lock (subagentStateGate)
            {
                if (!streams.TryGetValue(toolCallId, out var stream))
                {
                    stream = new StreamingTextAccumulator(
                        initialCapacity,
                        TimeSpan.FromMilliseconds(StreamingUiUpdateThrottleMs),
                        () => flushAction(toolCallId));
                    streams[toolCallId] = stream;
                }

                return stream;
            }
        }

        StreamingTextAccumulator? GetSubagentStream(
            Dictionary<string, StreamingTextAccumulator> streams,
            string toolCallId)
        {
            lock (subagentStateGate)
                return streams.TryGetValue(toolCallId, out var stream) ? stream : null;
        }

        void DisposeSubagentStream(
            Dictionary<string, StreamingTextAccumulator> streams,
            string toolCallId)
        {
            StreamingTextAccumulator? stream = null;
            lock (subagentStateGate)
            {
                if (streams.TryGetValue(toolCallId, out stream))
                    streams.Remove(toolCallId);
            }

            stream?.Dispose();
        }

        void UpdateSubagentCardContent(
            string toolCallId,
            bool updateTranscript = false,
            string? transcript = null,
            bool updateReasoning = false,
            string? reasoning = null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == toolCallId);
                if (toolMsg is null)
                    return;

                var description = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "description") ?? string.Empty;
                var agentName = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "agentName");
                if (string.IsNullOrWhiteSpace(agentName)
                    && toolMsg.ToolName?.StartsWith("agent:", StringComparison.Ordinal) == true)
                {
                    agentName = toolMsg.ToolName["agent:".Length..];
                }

                var agentDisplayName = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "agentDisplayName")
                    ?? toolMsg.Author
                    ?? agentName
                    ?? "Agent";
                var agentDescription = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "agentDescription");
                var mode = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "mode") ?? string.Empty;
                var model = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "model");
                var nextTranscript = updateTranscript
                    ? transcript ?? string.Empty
                    : ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "transcript");
                var nextReasoning = updateReasoning
                    ? reasoning ?? string.Empty
                    : ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "reasoning");

                var nextContent = BuildSubagentPayloadJson(
                    description,
                    agentName,
                    agentDisplayName,
                    agentDescription,
                    mode,
                    model,
                    nextTranscript,
                    nextReasoning);

                if (string.Equals(toolMsg.Content, nextContent, StringComparison.Ordinal))
                    return;

                toolMsg.Content = nextContent;
                if (IsDisplayedSession())
                {
                    var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == toolCallId);
                    vm?.NotifyContentChanged();
                }
            });
        }

        void FlushSubagentAssistantDelta(string toolCallId)
        {
            var stream = GetSubagentStream(subagentAssistantStreams, toolCallId);
            var currentContent = stream?.SnapshotOrNull();
            if (currentContent is null)
                return;

            if (IsDisplayedSession())
                _transcriptBuilder.UpdateSubagentTranscriptText(toolCallId, currentContent);
            // Persist to ChatMessage JSON for transcript rebuilds
            UpdateSubagentCardContent(toolCallId, updateTranscript: true, transcript: currentContent);
        }

        void FlushSubagentReasoningDelta(string toolCallId)
        {
            var stream = GetSubagentStream(subagentReasoningStreams, toolCallId);
            var currentContent = stream?.SnapshotOrNull();
            if (currentContent is null)
                return;

            if (IsDisplayedSession())
                _transcriptBuilder.UpdateSubagentReasoningText(toolCallId, currentContent);
            UpdateSubagentCardContent(toolCallId, updateReasoning: true, reasoning: currentContent);
        }

        void CompleteSubagentStreams(string? toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
                return;

            var assistantSubagentStream = GetSubagentStream(subagentAssistantStreams, toolCallId);
            assistantSubagentStream?.CancelPending();
            FlushSubagentAssistantDelta(toolCallId);
            DisposeSubagentStream(subagentAssistantStreams, toolCallId);

            var reasoningSubagentStream = GetSubagentStream(subagentReasoningStreams, toolCallId);
            reasoningSubagentStream?.CancelPending();
            FlushSubagentReasoningDelta(toolCallId);
            DisposeSubagentStream(subagentReasoningStreams, toolCallId);
        }

        void ResetSubagentOutputState()
        {
            Volatile.Write(ref runtime.ActiveSubagentExecutionDepth, 0);
            List<StreamingTextAccumulator> streamsToDispose = [];
            lock (subagentStateGate)
            {
                activeSubagentToolCallIds.Clear();
                mostRecentSubagentToolCallId = null;
                streamsToDispose.AddRange(subagentAssistantStreams.Values);
                streamsToDispose.AddRange(subagentReasoningStreams.Values);
                subagentAssistantStreams.Clear();
                subagentReasoningStreams.Clear();
            }

            foreach (var stream in streamsToDispose)
                stream.Dispose();
        }

        void FlushAssistantDelta()
        {
            var currentContent = assistantStream!.SnapshotOrNull();
            if (currentContent is null)
                return;

            runtime.StatusText = Loc.Status_Generating;
            if (streamingMsg is null)
            {
                streamingMsg = new ChatMessage
                {
                    Role = "assistant",
                    Author = agentName,
                    Content = currentContent,
                    IsStreaming = true,
                    Model = turnModelId
                };
                _inProgressMessages[chat.Id] = streamingMsg;
                if (IsDisplayedSession())
                {
                    streamingVm = new ChatMessageViewModel(streamingMsg);
                    Messages.Add(streamingVm);
                    StatusText = runtime.StatusText;
                    ScrollToEndRequested?.Invoke();
                }

                return;
            }

            if (string.Equals(streamingMsg.Content, currentContent, StringComparison.Ordinal))
                return;

            streamingMsg.Content = currentContent;
            if (IsDisplayedSession())
            {
                streamingVm?.NotifyContentChanged();
                StatusText = runtime.StatusText;
                ScrollToEndRequested?.Invoke();
            }
        }

        void FlushReasoningDelta()
        {
            var currentReasoning = reasoningStream!.SnapshotOrNull();
            if (currentReasoning is null)
                return;

            runtime.StatusText = Loc.Status_Reasoning;
            if (reasoningMsg is null)
            {
                reasoningMsg = new ChatMessage
                {
                    Role = "reasoning",
                    Author = Loc.Author_Thinking,
                    Content = currentReasoning,
                    IsStreaming = true
                };
                chat.Messages.Add(reasoningMsg);
                if (IsDisplayedSession())
                {
                    reasoningVm = new ChatMessageViewModel(reasoningMsg);
                    Messages.Add(reasoningVm);
                    StatusText = runtime.StatusText;
                    ScrollToEndRequested?.Invoke();
                }

                return;
            }

            if (string.Equals(reasoningMsg.Content, currentReasoning, StringComparison.Ordinal))
                return;

            reasoningMsg.Content = currentReasoning;
            if (IsDisplayedSession())
            {
                reasoningVm?.NotifyContentChanged();
                StatusText = runtime.StatusText;
                ScrollToEndRequested?.Invoke();
            }
        }

        void FinalizeCompletedTurnStreams(bool shouldUpdateDisplayedChatUi)
        {
            FlushAssistantDelta();
            if (streamingMsg is not null)
            {
                _inProgressMessages.Remove(chat.Id);
                if (FinalizeTerminalAssistantMessage(chat, streamingMsg))
                {
                    if (shouldUpdateDisplayedChatUi)
                        streamingVm?.NotifyStreamingEnded();
                }
                else if (shouldUpdateDisplayedChatUi && streamingVm is not null)
                {
                    Messages.Remove(streamingVm);
                }

                streamingMsg = null;
                streamingVm = null;
            }
            assistantStream.Clear();

            FlushReasoningDelta();
            if (reasoningMsg is not null)
            {
                FinalizeTerminalReasoningMessage(reasoningMsg);
                if (shouldUpdateDisplayedChatUi)
                    reasoningVm?.NotifyStreamingEnded();

                reasoningMsg = null;
                reasoningVm = null;
            }
            reasoningStream.Clear();
        }

        assistantStream = new StreamingTextAccumulator(
            4096,
            TimeSpan.FromMilliseconds(StreamingUiUpdateThrottleMs),
            FlushAssistantDelta);
        reasoningStream = new StreamingTextAccumulator(
            1024,
            TimeSpan.FromMilliseconds(StreamingUiUpdateThrottleMs),
            FlushReasoningDelta);

        var sessionSubscription = session.On<SessionEvent>(evt =>
        {
            try
            {
            switch (evt)
            {
                case AssistantTurnStartEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                        turnModelId = ResolveSelectedModelForChat(chat);
                        MarkRuntimeActive(runtime, Loc.Status_Thinking);
                        if (IsDisplayedSession())
                            ApplyDisplayedRuntimeState(runtime);
                    });
                    break;

                case AssistantMessageDeltaEvent delta:
#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; still required for sub-agent stream routing.
                    var activeSubagentToolCallIdForAssistantDelta =
                        GetSubagentToolCallIdFromParent(delta.Data.ParentToolCallId)
                        ?? GetCurrentSubagentOutputToolCallId();
#pragma warning restore CS0618
                    if (!string.IsNullOrWhiteSpace(activeSubagentToolCallIdForAssistantDelta))
                    {
                        GetOrCreateSubagentStream(
                            subagentAssistantStreams,
                            activeSubagentToolCallIdForAssistantDelta,
                            2048,
                            FlushSubagentAssistantDelta)
                            .Append(delta.Data.DeltaContent);
                        break;
                    }
                    if (IsSubagentOutputActive())
                        break;
                    assistantStream.Append(delta.Data.DeltaContent);
                    break;

                case AssistantMessageEvent msg:
#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; still required for sub-agent stream routing.
                    var activeSubagentToolCallIdForAssistantMessage =
                        GetSubagentToolCallIdFromParent(msg.Data.ParentToolCallId)
                        ?? GetCurrentSubagentOutputToolCallId();
#pragma warning restore CS0618
                    if (!string.IsNullOrWhiteSpace(activeSubagentToolCallIdForAssistantMessage))
                    {
                        var subagentAssistantStream = GetSubagentStream(
                            subagentAssistantStreams,
                            activeSubagentToolCallIdForAssistantMessage);
                        subagentAssistantStream?.CancelPending();
                        var capturedTranscript = ResolveFinalAssistantContent(
                            msg.Data.Content,
                            subagentAssistantStream?.SnapshotOrNull(),
                            existingStreamingContent: null) ?? string.Empty;
                        var capturedReasoning = msg.Data.ReasoningText;
                        var capturedToolCallId = activeSubagentToolCallIdForAssistantMessage;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!IsDisplayedSession())
                                return;

                            _transcriptBuilder.UpdateSubagentTranscriptText(capturedToolCallId, capturedTranscript);
                            if (!string.IsNullOrWhiteSpace(capturedReasoning))
                                _transcriptBuilder.UpdateSubagentReasoningText(capturedToolCallId, capturedReasoning);
                        });
                        UpdateSubagentCardContent(
                            activeSubagentToolCallIdForAssistantMessage,
                            updateTranscript: true,
                            transcript: capturedTranscript,
                            updateReasoning: !string.IsNullOrWhiteSpace(msg.Data.ReasoningText),
                            reasoning: msg.Data.ReasoningText);
                        subagentAssistantStream?.Clear();
                        break;
                    }
                    if (IsSubagentOutputActive())
                        break;
                    var capturedFinalContent = msg.Data.Content;
                    // Older CLI versions can emit an empty assistant envelope immediately before the
                    // substantive message for the same turn. Finalizing from buffered deltas here
                    // commits the stream once, then the following content event creates it again.
                    // Keep the stream open instead; the content event or turn-end fallback will finish it.
                    if (ShouldDeferAssistantMessageEvent(capturedFinalContent, msg.Data.Phase))
                        break;
                    assistantStream.CancelPending();
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Flush any buffered deltas that CancelPending() may have
                        // prevented from reaching the UI. Without this, a fast
                        // completion can cancel the throttled flush before it ever
                        // creates the streaming message, causing the entire response
                        // to appear at once instead of streaming.
                        FlushAssistantDelta();

                        var finalContent = ResolveFinalAssistantContent(
                            capturedFinalContent,
                            assistantStream?.SnapshotOrNull(),
                            streamingMsg?.Content);
                        if (string.IsNullOrWhiteSpace(finalContent))
                        {
                            if (IsDisplayedSession() && streamingVm is not null)
                                Messages.Remove(streamingVm);
                        }
                        else if (streamingMsg is null)
                        {
                            var completedMessage = new ChatMessage
                            {
                                Role = "assistant",
                                Author = agentName,
                                Content = finalContent,
                                IsStreaming = false,
                                Model = turnModelId
                            };
                            if (pendingFetchedSkillRefs.Count > 0)
                            {
                                completedMessage.ActiveSkills.AddRange(pendingFetchedSkillRefs);
                                pendingFetchedSkillRefs.Clear();
                            }
                            chat.Messages.Add(completedMessage);
                            if (IsDisplayedSession())
                            {
                                Messages.Add(new ChatMessageViewModel(completedMessage));
                                StatusText = runtime.StatusText;
                                ScrollToEndRequested?.Invoke();
                            }
                        }
                        else
                        {
                            streamingMsg.Content = finalContent;
                            streamingMsg.IsStreaming = false;
                            if (pendingFetchedSkillRefs.Count > 0)
                            {
                                streamingMsg.ActiveSkills.AddRange(pendingFetchedSkillRefs);
                                pendingFetchedSkillRefs.Clear();
                            }
                            chat.Messages.Add(streamingMsg);
                            if (IsDisplayedSession())
                                streamingVm?.NotifyStreamingEnded();
                        }

                        _inProgressMessages.Remove(chat.Id);
                        streamingMsg = null;
                        streamingVm = null;
                        assistantStream?.Clear();
                    });
                    break;

                case AssistantReasoningDeltaEvent rd:
                    var activeSubagentToolCallIdForReasoningDelta = GetCurrentSubagentOutputToolCallId();
                    if (!string.IsNullOrWhiteSpace(activeSubagentToolCallIdForReasoningDelta))
                    {
                        GetOrCreateSubagentStream(
                            subagentReasoningStreams,
                            activeSubagentToolCallIdForReasoningDelta,
                            1024,
                            FlushSubagentReasoningDelta)
                            .Append(rd.Data.DeltaContent);
                        break;
                    }

                    if (IsSubagentOutputActive())
                        break;
                    reasoningStream.Append(rd.Data.DeltaContent);
                    break;

                case AssistantReasoningEvent r:
                    var activeSubagentToolCallIdForReasoning = GetCurrentSubagentOutputToolCallId();
                    if (!string.IsNullOrWhiteSpace(activeSubagentToolCallIdForReasoning))
                    {
                        var subagentReasoningStream = GetSubagentStream(
                            subagentReasoningStreams,
                            activeSubagentToolCallIdForReasoning);
                        subagentReasoningStream?.CancelPending();
                        var capturedReasoningContent = r.Data.Content;
                        var capturedReasoningToolCallId = activeSubagentToolCallIdForReasoning;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!IsDisplayedSession())
                                return;

                            _transcriptBuilder.UpdateSubagentReasoningText(capturedReasoningToolCallId, capturedReasoningContent);
                        });
                        UpdateSubagentCardContent(
                            activeSubagentToolCallIdForReasoning,
                            updateReasoning: true,
                            reasoning: r.Data.Content);
                        subagentReasoningStream?.Clear();
                        break;
                    }

                    if (IsSubagentOutputActive())
                        break;
                    reasoningStream.CancelPending();
                    Dispatcher.UIThread.Post(() =>
                    {
                        FlushReasoningDelta();

                        var finalReasoning = r.Data.Content;
                        if (!string.IsNullOrWhiteSpace(finalReasoning) && reasoningMsg is null)
                        {
                            var completedReasoning = new ChatMessage
                            {
                                Role = "reasoning",
                                Author = Loc.Author_Thinking,
                                Content = finalReasoning,
                                IsStreaming = false
                            };
                            chat.Messages.Add(completedReasoning);
                            if (IsDisplayedSession())
                            {
                                Messages.Add(new ChatMessageViewModel(completedReasoning));
                                StatusText = runtime.StatusText;
                                ScrollToEndRequested?.Invoke();
                            }
                        }
                        else if (reasoningMsg is not null)
                        {
                            reasoningMsg.Content = finalReasoning;
                            reasoningMsg.IsStreaming = false;
                            if (IsDisplayedSession())
                            {
                                reasoningVm?.NotifyStreamingEnded();
                            }
                        }
                        reasoningMsg = null;
                        reasoningVm = null;
                        reasoningStream.Clear();
                    });
                    break;

                case ToolExecutionStartEvent toolStart:
                    AdjustPendingToolCount(chat.Id, 1);
                    Dispatcher.UIThread.Post(() =>
                    {
                    var startToolCallId = toolStart.Data.ToolCallId;
#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; still required for sub-agent tool grouping.
                    toolParentById[startToolCallId] = toolStart.Data.ParentToolCallId;
                    if (toolStart.Data.ToolName == "powershell")
                    {
                        terminalRootByToolCallId[startToolCallId] = startToolCallId;
                    }
                    else if (ToolDisplayHelper.IsTerminalStreamingTool(toolStart.Data.ToolName)
                             && !string.IsNullOrWhiteSpace(toolStart.Data.ParentToolCallId))
                    {
                        terminalRootByToolCallId[startToolCallId] = ToolDisplayHelper.ResolveRootTerminalToolCallId(
                            toolStart.Data.ParentToolCallId!, toolParentById, terminalRootByToolCallId);
                    }

                    var displayName = ToolDisplayHelper.FormatToolStatusName(toolStart.Data.ToolName, toolStart.Data.Arguments?.ToString());
                    MarkRuntimeActive(
                        runtime,
                        ToolDisplayHelper.FormatProgressLabel(displayName),
                        isStreaming: runtime.IsStreaming);
                    var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == startToolCallId);
                    var toolStatus = ResolveToolStartStatus(
                        toolStart.Data.ToolName,
                        completedToolStatusesByCallId.GetValueOrDefault(startToolCallId));
                    var completedToolOutput = toolStatus == "Failed"
                        && completedToolOutputsByCallId.TryGetValue(startToolCallId, out var cachedCompletedToolOutput)
                            ? cachedCompletedToolOutput
                            : null;
                    var shouldSaveCachedFailureOutput = toolStatus == "Failed" && !string.IsNullOrWhiteSpace(completedToolOutput);
                    if (toolMsg is null)
                    {
                        toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = startToolCallId,
                            ParentToolCallId = toolStart.Data.ParentToolCallId,
                            ToolName = toolStart.Data.ToolName,
                            ToolStatus = toolStatus,
                            ToolOutput = completedToolOutput,
                            Content = toolStart.Data.Arguments?.ToString() ?? "",
                            Author = displayName
                        };
                        chat.Messages.Add(toolMsg);
                    }
                    else
                    {
                        toolMsg.ParentToolCallId = toolStart.Data.ParentToolCallId;
#pragma warning restore CS0618
                        toolMsg.ToolName = toolStart.Data.ToolName;
                        toolMsg.ToolStatus = toolStatus;
                        if (toolStatus == "Failed")
                            toolMsg.ToolOutput = completedToolOutput;

                        toolMsg.Content = toolStart.Data.Arguments?.ToString() ?? "";
                        toolMsg.Author = displayName;
                    }

                    if (shouldSaveCachedFailureOutput)
                        QueueSaveChat(chat, saveIndex: false);

                    if (IsDisplayedSession())
                    {
                        var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == startToolCallId);
                        if (vm is null)
                        {
                            Messages.Add(new ChatMessageViewModel(toolMsg));
                            ScrollToEndRequested?.Invoke();
                        }
                        else
                        {
                            vm.NotifyContentChanged();
                            vm.NotifyToolStatusChanged();
                        }

                        ApplyDisplayedRuntimeState(runtime);
                    }
                    });
                    break;

                case ToolExecutionPartialResultEvent partial:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!IsDisplayedSession())
                        return;

                    var partialToolCallId = partial.Data.ToolCallId;
                    var partialToolName = chat.Messages.LastOrDefault(m => m.ToolCallId == partialToolCallId)?.ToolName;
                    if (!ToolDisplayHelper.IsTerminalStreamingTool(partialToolName)
                        && !terminalRootByToolCallId.ContainsKey(partialToolCallId))
                        return;

                    var rootToolCallId = ToolDisplayHelper.ResolveRootTerminalToolCallId(partialToolCallId, toolParentById, terminalRootByToolCallId);
                    var output = ToolDisplayHelper.CleanTerminalOutput(partial.Data.PartialOutput);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        ToolDisplayHelper.ApplyTerminalOutput(chat, rootToolCallId, output, replaceExistingOutput: false);
                        _transcriptBuilder.UpdateTerminalOutput(rootToolCallId, output, false);
                    }
                    });
                    break;

                case ToolExecutionProgressEvent progress:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!IsDisplayedSession())
                        return;

                    var progressToolCallId = progress.Data.ToolCallId;
                    var progressToolName = chat.Messages.LastOrDefault(m => m.ToolCallId == progressToolCallId)?.ToolName;
                    if (!ToolDisplayHelper.IsTerminalStreamingTool(progressToolName)
                        && !terminalRootByToolCallId.ContainsKey(progressToolCallId))
                        return;

                    var rootToolCallId = ToolDisplayHelper.ResolveRootTerminalToolCallId(progressToolCallId, toolParentById, terminalRootByToolCallId);
                    var output = ToolDisplayHelper.CleanTerminalOutput(progress.Data.ProgressMessage);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        ToolDisplayHelper.ApplyTerminalOutput(chat, rootToolCallId, output, replaceExistingOutput: false);
                        _transcriptBuilder.UpdateTerminalOutput(rootToolCallId, output, false);
                    }
                    });
                    break;

                case ToolExecutionCompleteEvent toolEnd:
                    var shouldReconcileAfterTool = AdjustPendingToolCount(chat.Id, -1);
                    if (shouldReconcileAfterTool)
                        SchedulePostToolReconciliation(chat.Id);
                    var completedToolStatus = toolEnd.Data.Success == true ? "Completed" : "Failed";
                    completedToolStatusesByCallId[toolEnd.Data.ToolCallId] = completedToolStatus;
                    var completedToolOutput = toolEnd.Data.Success == true
                        ? null
                        : ToolDisplayHelper.ExtractFailedToolOutput(toolEnd.Data.Error, toolEnd.Data.Result);
                    if (!string.IsNullOrWhiteSpace(completedToolOutput))
                        completedToolOutputsByCallId[toolEnd.Data.ToolCallId] = completedToolOutput;
                    else
                        completedToolOutputsByCallId.Remove(toolEnd.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
#pragma warning disable CS0618 // ParentToolCallId is deprecated in GitHub.Copilot.SDK 1.0.1 with no replacement; still required for sub-agent tool grouping.
                    toolParentById[toolEnd.Data.ToolCallId] = toolEnd.Data.ParentToolCallId;
#pragma warning restore CS0618

                    var success = toolEnd.Data.Success == true;
                    var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == toolEnd.Data.ToolCallId);
                    if (toolMsg is not null)
                    {
                        if (!success)
                            toolMsg.ToolOutput = completedToolOutput;

                        if (ShouldApplyToolExecutionCompletionStatus(toolMsg.ToolName, success))
                        {
                            toolMsg.ToolStatus = completedToolStatus;
                            if (IsDisplayedSession())
                            {
                                var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == toolEnd.Data.ToolCallId);
                                vm?.NotifyToolStatusChanged();
                            }
                        }

                        var toolName = toolMsg.ToolName;
                        if (!success)
                        {
                            if (!string.IsNullOrWhiteSpace(completedToolOutput))
                            {
                                QueueSaveChat(chat, saveIndex: false);
                            }
                        }

                        if (success)
                        {
                            // Keep the coding strip's git change count live as the agent
                            // edits files, independent of the IsBusy turn lifecycle. This
                            // tool-completion signal is reliable; SessionWorkspaceFileChangedEvent
                            // is not always emitted. powershell is included because the agent
                            // frequently mutates the repo (git, builds, file moves) through it.
                            if (IsDisplayedSession()
                                && (ToolDisplayHelper.IsFileCreationTool(toolName) || toolName == "powershell"))
                            {
                                QueueLiveGitRefresh();
                            }

                            // fetch_skill tracking is handled by TranscriptBuilder.ProcessToolMessage()

                            if (ToolDisplayHelper.IsWebFetchTool(toolName)
                                && ToolDisplayHelper.ExtractFetchSource(toolMsg.Content) is { } fetchedSource)
                            {
                                AddPendingSource(pendingFetchedSources, fetchedSource);
                            }

                            if (IsDisplayedSession()
                                && (ToolDisplayHelper.IsFileCreationTool(toolName) || toolName == "powershell")
                                && toolEnd.Data.Result?.Contents is { Length: > 0 } contents)
                            {
                                foreach (var item in contents)
                                {
                                    if (item is ToolExecutionCompleteContentResourceLink rl
                                        && !string.IsNullOrEmpty(rl.Uri))
                                    {
                                        var fp = ToolDisplayHelper.UriToLocalPath(rl.Uri);
                                        if (fp is not null && File.Exists(fp) && ToolDisplayHelper.IsUserFacingFile(fp) && _transcriptBuilder.ShownFileChips.Add(fp))
                                        {
                                            _transcriptBuilder.PendingToolFileChips.Add(new FileAttachmentItem(fp));
                                        }
                                    }
                                }
                            }
                        }

                        if (success && ToolDisplayHelper.IsTerminalStreamingTool(toolName) && IsDisplayedSession())
                        {
                            var rootToolCallId = ToolDisplayHelper.ResolveRootTerminalToolCallId(
                                toolEnd.Data.ToolCallId, toolParentById, terminalRootByToolCallId);
                            var output = ToolDisplayHelper.ExtractTerminalOutput(toolEnd.Data.Result);
                            if (!string.IsNullOrWhiteSpace(output))
                            {
                                ToolDisplayHelper.ApplyTerminalOutput(chat, rootToolCallId, output, replaceExistingOutput: true);
                                _transcriptBuilder.UpdateTerminalOutput(rootToolCallId, output, true);
                                QueueSaveChat(chat, saveIndex: false);
                            }

                            // An async shell's tool call returns in a fraction of a second while the OS
                            // process keeps running. Keep the card honestly "Running in background"
                            // (instead of flipping to "Completed") and let the Tasks-API monitor track it.
                            if (toolName == "powershell" && LooksLikeBackgroundShellArgs(toolMsg.Content))
                            {
                                var command = ToolDisplayHelper.ExtractJsonField(toolMsg.Content, "command") ?? string.Empty;
                                TrackBackgroundShell(rootToolCallId, command);
                            }
                        }
                    }
                    });
                    break;


                case ExternalToolRequestedEvent externalToolRequest:
                    // Client-side external tools have their own request/completion lifecycle
                    // and may not emit tool.execution_start/tool.execution_complete events.
                    externalToolCallIdByRequestId[externalToolRequest.Data.RequestId] = externalToolRequest.Data.ToolCallId;
                    AdjustPendingToolCount(chat.Id, 1);
                    Dispatcher.UIThread.Post(() =>
                    {
                    var arguments = externalToolRequest.Data.Arguments?.ToString();
                    var displayName = ToolDisplayHelper.FormatToolStatusName(externalToolRequest.Data.ToolName, arguments);
                    MarkRuntimeActive(
                        runtime,
                        ToolDisplayHelper.FormatProgressLabel(displayName),
                        isStreaming: runtime.IsStreaming);

                    var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == externalToolRequest.Data.ToolCallId);
                    var toolStatus = completedToolStatusesByCallId.GetValueOrDefault(externalToolRequest.Data.ToolCallId) ?? "InProgress";
                    var completedToolOutput = toolStatus == "Failed"
                        && completedToolOutputsByCallId.TryGetValue(externalToolRequest.Data.ToolCallId, out var cachedCompletedToolOutput)
                            ? cachedCompletedToolOutput
                            : null;
                    var shouldSaveCachedFailureOutput = toolStatus == "Failed" && !string.IsNullOrWhiteSpace(completedToolOutput);
                    if (toolMsg is null)
                    {
                        toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = externalToolRequest.Data.ToolCallId,
                            ToolName = externalToolRequest.Data.ToolName,
                            ToolStatus = toolStatus,
                            ToolOutput = completedToolOutput,
                            Content = arguments ?? "",
                            Author = displayName
                        };
                        chat.Messages.Add(toolMsg);
                    }
                    else
                    {
                        toolMsg.ToolName = externalToolRequest.Data.ToolName;
                        toolMsg.ToolStatus = toolStatus;
                        if (toolStatus == "Failed")
                            toolMsg.ToolOutput = completedToolOutput;

                        toolMsg.Content = arguments ?? "";
                        toolMsg.Author = displayName;
                    }

                    if (shouldSaveCachedFailureOutput)
                        QueueSaveChat(chat, saveIndex: false);

                    if (IsDisplayedSession())
                    {
                        var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == externalToolRequest.Data.ToolCallId);
                        if (vm is null)
                        {
                            Messages.Add(new ChatMessageViewModel(toolMsg));
                            ScrollToEndRequested?.Invoke();
                        }
                        else
                        {
                            vm.NotifyContentChanged();
                            vm.NotifyToolStatusChanged();
                        }

                        ApplyDisplayedRuntimeState(runtime);
                    }
                    });
                    break;

                case ExternalToolCompletedEvent externalToolComplete:
                    var shouldReconcileAfterExternalTool = AdjustPendingToolCount(chat.Id, -1);
                    if (shouldReconcileAfterExternalTool)
                        SchedulePostToolReconciliation(chat.Id);
                    if (externalToolCallIdByRequestId.TryGetValue(externalToolComplete.Data.RequestId, out var completedExternalToolCallId))
                        completedToolStatusesByCallId[completedExternalToolCallId] = "Completed";
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!externalToolCallIdByRequestId.TryGetValue(externalToolComplete.Data.RequestId, out var externalToolCallId))
                        return;

                    externalToolCallIdByRequestId.Remove(externalToolComplete.Data.RequestId);

                    foreach (var msg in chat.Messages.Where(m => m.ToolCallId == externalToolCallId))
                        msg.ToolStatus = "Completed";

                    if (IsDisplayedSession())
                    {
                        foreach (var vm in Messages.Where(m => m.Message.ToolCallId == externalToolCallId))
                            vm.NotifyToolStatusChanged();
                    }
                    });
                    break;

                case UserMessageEvent userMessage:
                    // The SDK echoes a user message when the agent actually CONSUMES it at a step boundary.
                    // For an immediate-mode steer this is the authoritative "the agent has now seen your
                    // message" signal, so flip the pending steer badge from "Steering…" to "Steered into
                    // response" only now — never merely when SendAsync accepted the inject into the queue.
                    // Autopilot continuations are the SDK's own internal messages, not user steers, so they
                    // must not consume a pending steer.
                    if (userMessage.Data?.IsAutopilotContinuation != true)
                        Dispatcher.UIThread.Post(() =>
                        {
                            // The turn-start user message echoes here too. Skip exactly that first echo so it
                            // can't be mistaken for a steer being consumed; every UserMessageEvent after it in
                            // the turn is a genuine mid-turn steer.
                            if (runtime.ExpectTurnStartUserEcho)
                            {
                                runtime.ExpectTurnStartUserEcho = false;
                                return;
                            }

                            ConfirmOldestPendingSteer(chat.Id);
                        });
                    break;

                case CommandQueuedEvent commandQueued:
                    Dispatcher.UIThread.Post(() =>
                    {
                    MarkRuntimeActive(runtime, $"Queued command: {commandQueued.Data.Command}", isStreaming: runtime.IsStreaming);
                    if (IsDisplayedSession())
                        ApplyDisplayedRuntimeState(runtime);
                    });
                    break;

                case AssistantTurnEndEvent:
                    ClearManualStopRequested(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    if (!IsSubagentOutputActive())
                        ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                        var shouldUpdateDisplayedChatUi = IsDisplayedSession();
                        if (IsAuthoritativeSession())
                            ReconcileInProgressSubagentTools(chat, "Completed");
                        FinalizeCompletedTurnStreams(shouldUpdateDisplayedChatUi);
                        DropCompletedTurnState(chat.Id, dropCancellation: false);
                        // Fallback: if any steered message never got an explicit consume echo, the turn
                        // ending means it's as delivered as it will ever be — resolve it so it can't stick
                        // on "Steering…" forever.
                        ResolvePendingSteersAsDelivered(chat.Id);
                        MarkRuntimeWaitingForSessionIdle(runtime);
                        if (runtime.IsBusy)
                            SchedulePostToolReconciliation(chat.Id, treatCompletedTurnAsIdle: true);
                        if (shouldUpdateDisplayedChatUi)
                        {
                            if (!runtime.IsBusy)
                            {
                                _transcriptBuilder.HideTypingIndicator();
                                _transcriptBuilder.CloseCurrentToolGroup();
                                _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                            }

                            ApplyDisplayedRuntimeState(runtime);
                        }
                        QueueSaveChat(chat, saveIndex: true, touchIndex: true);
                    });
                    break;

                case SessionBackgroundTasksChangedEvent:
                    // SDK 0.2.2 emits this both when background work starts and again
                    // after session.idle when the background queue drains. Treat it as
                    // "pending" only while the current turn is still active locally.
                    if (ShouldMarkBackgroundWorkPending(runtime))
                    {
                        runtime.HasPendingBackgroundWork = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            MarkRuntimeActive(runtime, isStreaming: false, hasPendingBackgroundWork: true);
                            if (IsDisplayedSession())
                            {
                                EnsureBackgroundShellMonitorRunning();
                                ApplyDisplayedRuntimeState(runtime);
                            }
                        });
                    }
                    break;

                case SessionIdleEvent:
                    ClearManualStopRequested(chat.Id);
                    ClearPendingTurnTracking(chat.Id);
                    DropCompletedTurnState(chat.Id, dropCancellation: true);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();

                    Dispatcher.UIThread.Post(() =>
                    {
                        var shouldUpdateDisplayedChatUi = IsDisplayedSession();
                        FinalizeCompletedTurnStreams(shouldUpdateDisplayedChatUi);
                        AttachPendingSourcesToFinalAssistantMessage();

                        // Final safety net for steer badges in case no AssistantTurnEnd preceded idle
                        // (e.g. abort paths): never leave a steered message stuck on "Steering…".
                        ResolvePendingSteersAsDelivered(chat.Id);

                        // In SDK 0.2.2+, session.idle is only emitted once background work is drained.
                        // Clearing IsBusy updates Chat.IsRunning, so keep it on the UI thread.
                        MarkRuntimeTerminal(runtime);

                        // Session idle == all attached background work has drained, so any terminal
                        // cards still shown "running in background" for this chat are now finished.
                        if (shouldUpdateDisplayedChatUi)
                            CompleteAllBackgroundShellsAndStop();

                        // Mark chat as unread if user is on a different chat
                        if (CurrentChat?.Id != chat.Id)
                            chat.HasUnreadMessages = true;

                        if (_dataStore.Data.Settings.NotificationsEnabled)
                        {
                            var chatTitle = chat.Title;
                            var body = string.IsNullOrWhiteSpace(chatTitle)
                                ? Loc.Notification_ResponseReady
                                : $"{chatTitle} — {Loc.Notification_ResponseReady}";
                            NotificationService.ShowIfInactive(agentName, body, chat.Id);
                        }

                        // Flush file changes only when session is truly idle (not between agentic turns).
                        if (shouldUpdateDisplayedChatUi)
                        {
                            // Show model label once at the very end of the assistant turn
                            // (not per-message during agentic loops).
                            _transcriptBuilder.AppendModelLabel(turnModelId);
                            ApplyDisplayedRuntimeState(runtime);
                            _transcriptBuilder.CloseCurrentToolGroup();
                            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                            _transcriptBuilder.FlushPendingFileEdits();
                            // FlushPendingFileEdits appends this turn's file-change summary *after*
                            // the IsBusy=false rebuild already ran, so rebuild once more — otherwise
                            // the Workspace Changes/Files tabs miss this turn's edits until the next one.
                            RebuildWorkspacePanel();
                            ScrollToEndRequested?.Invoke();
                        }

                        // Memory checkpoint + suggestions only when session is truly idle.
                        // Running these on every AssistantTurnEndEvent creates a storm of
                        // background sessions that can starve the CLI process and stall
                        // all active sessions.
                        QueueChatCompletionFollowUps(chat);

                        if (CurrentChat?.Id != chat.Id)
                            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: true);
                        else
                            QueueSaveChat(chat, saveIndex: false);
                    });
                    break;

                case SessionTitleChangedEvent:
                    // SDK session titles are based on the transport prompt. After resume
                    // or message editing that prompt can be a full transcript replay, so
                    // Lumi uses its own guarded title generator instead.
                    break;

                case SessionErrorEvent err:
                    ClearManualStopRequested(chat.Id);
                    ClearPendingTurnTracking(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                        var shouldUpdateDisplayedChatUi = CurrentChat?.Id == chat.Id
                            && (!_sessionCache.TryGetValue(chat.Id, out var cachedSession)
                                || ReferenceEquals(cachedSession, session));
                        // Skip if CLI crash handler already claimed cleanup
                        if (Volatile.Read(ref cliExitHandled) == 1)
                        {
                            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
                            return;
                        }
                        // Clean up any in-progress streaming message
                        if (streamingMsg is not null)
                        {
                            _inProgressMessages.Remove(chat.Id);
                            if (shouldUpdateDisplayedChatUi)
                            {
                                if (streamingVm is not null) Messages.Remove(streamingVm);
                            }
                            streamingMsg = null;
                            streamingVm = null;
                        }
                        assistantStream.Clear();
                        if (reasoningMsg is not null)
                        {
                            reasoningMsg.IsStreaming = false;
                            if (shouldUpdateDisplayedChatUi)
                            {
                                reasoningVm?.NotifyStreamingEnded();
                            }
                            reasoningMsg = null;
                            reasoningVm = null;
                        }
                        reasoningStream.Clear();

                        // A PROVABLY transient backend-internal failure (twirp/usersd, or a 5xx
                        // "unavailable") is not a real logout: the credential is still valid and a
                        // plain resend recovers. Surface it as a one-click retry affordance instead
                        // of a terminal error. A bare/ambiguous 401/403 is NOT matched, so a genuine
                        // logout falls through to the terminal error path below and routes to re-auth.
                        if (CopilotService.IsTransientServerAuthError(
                                err.Data.StatusCode, err.Data.ErrorType, err.Data.Message))
                        {
                            if (IsAuthoritativeSession())
                                ApplyUnexpectedAbortState(chat, Loc.Status_TransientAuthRetry, shouldUpdateDisplayedChatUi);
                            return;
                        }

                        if (IsAuthoritativeSession())
                            ReconcileInProgressSubagentTools(chat, "Failed");

                        // Classify the terminal error. Anything that is NOT a hard auth / quota /
                        // context / policy limit is recoverable by rebuilding the session from the
                        // transcript AS TEXT, which safely drops any poisoned server-side history —
                        // e.g. an image the backend refuses to process (a tool-result screenshot, an
                        // attachment, …) re-sent on every turn that would otherwise brick the chat
                        // forever, since there is no SDK API to remove a single asset. Arm a reset so
                        // the next send (a new message OR the Retry button) rebuilds a fresh session;
                        // arming here (not only in the displayed branch) recovers a background chat too.
                        // Shared decision (identical logic to HandleSendError's). A structured
                        // session.error carries HTTP status + errorType, so a fatal-by-type failure —
                        // a genuine logout, or a content-policy image block whose message also says
                        // "could not process image" — is correctly NOT recoverable and NOT shown the
                        // image copy: the fatal verdict gates the image flag inside ClassifySendFailure.
                        // Without that gate the recovery-implying copy (which carries no fatal keyword)
                        // would be re-read as recoverable on reopen and dangle a false Retry.
                        var (recoverable, isImageError) = CopilotService.ClassifySendFailure(
                            err.Data.StatusCode, err.Data.ErrorType, err.Data.Message,
                            hasTerminalOverride: false);
                        var display = isImageError
                            ? Loc.Status_ImageRejectedReset
                            : string.Format(Loc.Status_Error, err.Data.Message);

                        if (recoverable)
                            _pendingSessionInvalidations.Add(chat.Id);

                        MarkRuntimeTerminal(runtime, display);
                        // The turn errored out before consuming any in-flight steer — report it as not
                        // delivered rather than leaving it pending (a recoverable error rebuilds the session
                        // on the next send, so no idle fallback resolves it for this turn).
                        ResolvePendingSteersAsFailed(chat.Id);
                        if (shouldUpdateDisplayedChatUi)
                        {
                            // Clean up typing indicator and tool groups
                            _transcriptBuilder.HideTypingIndicator();
                            _transcriptBuilder.CloseCurrentToolGroup();
                            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                            _transcriptBuilder.FlushPendingFileEdits();

                            StatusText = runtime.StatusText;
                            IsBusy = runtime.IsBusy;
                            IsStreaming = runtime.IsStreaming;

                            // Surface the error as a visible chat message. Adding to Messages renders
                            // it via the CollectionChanged handler (no explicit ProcessMessageToTranscript
                            // — a second call here would render a duplicate error card).
                            var errorMsg = new ChatMessage
                            {
                                Role = "error",
                                Author = Loc.Author_Lumi,
                                Content = display
                            };
                            chat.Messages.Add(errorMsg);
                            Messages.Add(new ChatMessageViewModel(errorMsg));
                            // Pass the authoritative (structured) recoverability decision: a
                            // fatal-by-ErrorType error (e.g. a genuine logout) loses its type once
                            // persisted as plain "Error: {message}", so the affordance must not
                            // re-derive it from that lossy text and offer a false Retry.
                            UpdateStuckChatRetryAffordance(recoverable);
                            ScrollToEndRequested?.Invoke();
                        }
                        QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
                    });
                    break;

                case SessionCompactionStartEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    MarkRuntimeActive(runtime, Loc.Status_Compacting, isStreaming: false);
                    if (IsDisplayedSession())
                        ApplyDisplayedRuntimeState(runtime);
                    });
                    break;

                case SessionCompactionCompleteEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = "";
                    if (IsDisplayedSession())
                        StatusText = runtime.StatusText;
                    });
                    break;

                case SessionTruncationEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = Loc.Status_Truncated;
                    if (IsDisplayedSession())
                        StatusText = runtime.StatusText;
                    });
                    break;

                case SessionWarningEvent warn:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = string.Format(Loc.Status_Warning, warn.Data.WarningType);
                    if (IsDisplayedSession())
                    {
                        StatusText = runtime.StatusText;
                        // Surface the warning as a visible chat message
                        var warnMsg = new ChatMessage
                        {
                            Role = "system",
                            Author = "⚠ Warning",
                            Content = warn.Data.Message
                        };
                        chat.Messages.Add(warnMsg);
                        Messages.Add(new ChatMessageViewModel(warnMsg));
                        ScrollToEndRequested?.Invoke();
                    }
                    });
                    break;

                case AbortEvent abort:
                    var wasUserStopRequested = ConsumeManualStopRequested(chat.Id);
                    ClearPendingTurnTracking(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                        var shouldUpdateDisplayedChatUi = CurrentChat?.Id == chat.Id
                            && (!_sessionCache.TryGetValue(chat.Id, out var cachedSession)
                                || ReferenceEquals(cachedSession, session));
                        if (Volatile.Read(ref cliExitHandled) == 1)
                        {
                            QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
                            return;
                        }

                        FlushAssistantDelta();
                        // Finalize any partial assistant content before classifying the abort.
                        if (streamingMsg is not null)
                        {
                            _inProgressMessages.Remove(chat.Id);
                            streamingMsg.IsStreaming = false;
                            if (!string.IsNullOrWhiteSpace(streamingMsg.Content))
                            {
                                chat.Messages.Add(streamingMsg);
                                if (shouldUpdateDisplayedChatUi)
                                {
                                    streamingVm?.NotifyStreamingEnded();
                                }
                            }
                            else
                            {
                                if (shouldUpdateDisplayedChatUi)
                                {
                                    if (streamingVm is not null) Messages.Remove(streamingVm);
                                }
                            }
                            streamingMsg = null;
                            streamingVm = null;
                        }
                        assistantStream.Clear();
                        if (reasoningMsg is not null)
                        {
                            reasoningMsg.IsStreaming = false;
                            if (shouldUpdateDisplayedChatUi)
                            {
                                reasoningVm?.NotifyStreamingEnded();
                            }
                            reasoningMsg = null;
                            reasoningVm = null;
                        }
                        reasoningStream.Clear();
                        if (wasUserStopRequested && IsAuthoritativeSession())
                            ReconcileInProgressSubagentTools(chat, "Stopped");
                        MarkRuntimeTerminal(runtime);

                        if (!wasUserStopRequested)
                        {
                            if (IsAuthoritativeSession())
                                ApplyUnexpectedAbortState(chat, GetUnexpectedAbortMessage(), updateDisplayedChatUi: shouldUpdateDisplayedChatUi);
                            return;
                        }

                        runtime.StatusText = Loc.Status_Stopped;
                        if (shouldUpdateDisplayedChatUi)
                        {
                            _transcriptBuilder.HideTypingIndicator();
                            _transcriptBuilder.CloseCurrentToolGroup();
                            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                            IsBusy = false;
                            IsStreaming = false;
                            StatusText = runtime.StatusText;
                        }
                        // SDK session already records the aborted turn in its event log,
                        // so the LLM will see the partial content on the next turn automatically.
                        QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
                    });
                    break;

                case SessionShutdownEvent shutdown:
                    ClearManualStopRequested(chat.Id);
                    ClearPendingTurnTracking(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                        var shouldUpdateDisplayedChatUi = CurrentChat?.Id == chat.Id
                            && (!_sessionCache.TryGetValue(chat.Id, out var cachedSession)
                                || ReferenceEquals(cachedSession, session));
                        // Clean up any in-progress streaming state
                        if (streamingMsg is not null)
                        {
                            _inProgressMessages.Remove(chat.Id);
                            if (shouldUpdateDisplayedChatUi && streamingVm is not null)
                                Messages.Remove(streamingVm);
                            streamingMsg = null;
                            streamingVm = null;
                        }
                        assistantStream.Clear();
                        if (reasoningMsg is not null)
                        {
                            reasoningMsg.IsStreaming = false;
                            if (shouldUpdateDisplayedChatUi)
                                reasoningVm?.NotifyStreamingEnded();
                            reasoningMsg = null;
                            reasoningVm = null;
                        }
                        reasoningStream.Clear();

                        var isError = shutdown.Data.ShutdownType == ShutdownType.Error;
                        if (IsAuthoritativeSession())
                            ReconcileInProgressSubagentTools(chat, isError ? "Failed" : "Stopped");
                        if (isError && shouldUpdateDisplayedChatUi)
                        {
                            _transcriptBuilder.HideTypingIndicator();
                            _transcriptBuilder.CloseCurrentToolGroup();
                            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                            _transcriptBuilder.FlushPendingFileEdits();

                            var reason = shutdown.Data.ErrorReason ?? Loc.Status_CopilotStoppedResponding;
                            _transcriptBuilder.AddConnectionLostError(
                                reason,
                                new RelayCommand(() => _ = RetryAfterConnectionLossAsync()));
                            ScrollToEndRequested?.Invoke();
                        }

                        // A remote shutdown ends the turn abnormally and (below) disposes this session's
                        // subscription, so no turn-end/idle fallback will follow: resolve any in-flight steer
                        // here as NOT delivered, both to tell the truth and to stop the badge sticking on
                        // "Steering…" forever.
                        ResolvePendingSteersAsFailed(chat.Id);

                        // Clear local session objects, but keep the persisted session ID so
                        // a later resume attempt can retry after the CLI/server recovers.
                        DetachSessionAfterRemoteShutdown(chat, wasActive: _activeSession == session);
                        QueueSaveChat(chat, saveIndex: true, releaseIfInactive: CurrentChat?.Id != chat.Id, touchIndex: true);

                        // Session shutdown (even with error type) is a per-session event —
                        // the CLI process is still alive and serving other sessions.
                        // Do NOT call ForceReconnectAsync here; that would kill the entire
                        // CLI process and cascade-disconnect all other active sessions.
                    });
                    break;

                // ── New SDK event handlers ──

                case AssistantIntentEvent intent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!string.IsNullOrWhiteSpace(intent.Data.Intent))
                    {
                        MarkRuntimeActive(runtime, intent.Data.Intent, isStreaming: runtime.IsStreaming);
                        if (IsDisplayedSession())
                            ApplyDisplayedRuntimeState(runtime);
                    }
                    });
                    break;

                case SubagentSelectedEvent:
                case SubagentDeselectedEvent:
                    // Intentionally ignored for output routing. The CLI emits
                    // subagent.selected/deselected ONLY for the top-level configured agent
                    // (config.Agent) — once per session, with no tool call id and no matching
                    // subagent.started/completed. They describe the MAIN agent persona, not a
                    // nested sub-agent, so they must never gate output suppression: doing so
                    // dropped the entire turn whenever a Lumi agent was selected, and again
                    // after removing the agent (the resumed session still re-emits selected for
                    // its original agent). Genuine nested sub-agents are bracketed by
                    // subagent.started/subagent.completed (with a tool call id) and are handled
                    // via ActiveSubagentExecutionDepth + tool-call-id routing.
                    break;

                case SubagentStartedEvent subStart:
                    Interlocked.Increment(ref runtime.ActiveSubagentExecutionDepth);
                    RegisterActiveSubagent(subStart.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
                    var displayName = subStart.Data.AgentDisplayName ?? subStart.Data.AgentName ?? "Agent";
                    MarkRuntimeActive(runtime, $"⚡ {displayName}", isStreaming: false);
                    var subagentPayload = BuildSubagentPayloadJson(
                        description: string.Empty,
                        agentName: subStart.Data.AgentName,
                        agentDisplayName: displayName,
                        agentDescription: subStart.Data.AgentDescription,
                        mode: string.Empty,
                        transcript: GetSubagentStream(subagentAssistantStreams, subStart.Data.ToolCallId)?.SnapshotOrNull(),
                        reasoning: GetSubagentStream(subagentReasoningStreams, subStart.Data.ToolCallId)?.SnapshotOrNull());

                    // The SDK fires ToolExecutionStartEvent before SubagentStartedEvent
                    // with the same ToolCallId — reuse that message instead of duplicating.
                    var existing = chat.Messages.LastOrDefault(m => m.ToolCallId == subStart.Data.ToolCallId);
                    if (existing is not null)
                    {
                        var existingDescription = ToolDisplayHelper.ExtractJsonField(existing.Content, "description") ?? string.Empty;
                        var existingMode = ToolDisplayHelper.ExtractJsonField(existing.Content, "mode") ?? string.Empty;
                        var existingModel = ToolDisplayHelper.ExtractJsonField(existing.Content, "model");
                        var existingTranscript = ToolDisplayHelper.ExtractJsonField(existing.Content, "transcript")
                            ?? GetSubagentStream(subagentAssistantStreams, subStart.Data.ToolCallId)?.SnapshotOrNull();
                        var existingReasoning = ToolDisplayHelper.ExtractJsonField(existing.Content, "reasoning")
                            ?? GetSubagentStream(subagentReasoningStreams, subStart.Data.ToolCallId)?.SnapshotOrNull();
                        existing.ToolName = $"agent:{subStart.Data.AgentName}";
                        existing.ToolStatus = "InProgress";
                        existing.Content = BuildSubagentPayloadJson(
                            description: existingDescription,
                            agentName: subStart.Data.AgentName,
                            agentDisplayName: displayName,
                            agentDescription: subStart.Data.AgentDescription,
                            mode: existingMode,
                            model: existingModel,
                            transcript: existingTranscript,
                            reasoning: existingReasoning);
                        existing.Author = displayName;
                        if (IsDisplayedSession())
                        {
                            var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == subStart.Data.ToolCallId);
                            vm?.NotifyContentChanged();
                            vm?.NotifyToolStatusChanged();
                            ApplyDisplayedRuntimeState(runtime);
                        }
                    }
                    else
                    {
                        // Fallback: no prior ToolExecutionStartEvent — create the message
                        var toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = subStart.Data.ToolCallId,
                            ToolName = $"agent:{subStart.Data.AgentName}",
                            ToolStatus = "InProgress",
                            Content = subagentPayload,
                            Author = displayName
                        };
                        chat.Messages.Add(toolMsg);
                        if (IsDisplayedSession())
                        {
                            Messages.Add(new ChatMessageViewModel(toolMsg));
                            ApplyDisplayedRuntimeState(runtime);
                            ScrollToEndRequested?.Invoke();
                        }
                    }
                    });
                    break;

                case SubagentCompletedEvent subEnd:
                    // Decrement once per still-registered sub-agent so duplicate or
                    // out-of-order completion/failure events for the same tool call cannot
                    // under-count the depth (which would clear busy while siblings run).
                    if (UnregisterActiveSubagent(subEnd.Data.ToolCallId)
                        && Volatile.Read(ref runtime.ActiveSubagentExecutionDepth) > 0)
                        Interlocked.Decrement(ref runtime.ActiveSubagentExecutionDepth);
                    CompleteSubagentStreams(subEnd.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
                    // Mark ALL messages with this ToolCallId as Completed
                    // (covers both ToolExecutionStart and SubagentStarted entries).
                    foreach (var msg in chat.Messages.Where(m => m.ToolCallId == subEnd.Data.ToolCallId))
                        msg.ToolStatus = "Completed";
                    if (IsDisplayedSession())
                    {
                        foreach (var vm in Messages.Where(m => m.Message.ToolCallId == subEnd.Data.ToolCallId))
                            vm.NotifyToolStatusChanged();
                        _transcriptBuilder.UpdateSubagentToolStatus(subEnd.Data.ToolCallId, "Completed");
                    }
                    });
                    break;

                case SubagentFailedEvent subFail:
                    if (UnregisterActiveSubagent(subFail.Data.ToolCallId)
                        && Volatile.Read(ref runtime.ActiveSubagentExecutionDepth) > 0)
                        Interlocked.Decrement(ref runtime.ActiveSubagentExecutionDepth);
                    CompleteSubagentStreams(subFail.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
                    // Mark ALL messages with this ToolCallId as Failed
                    foreach (var msg in chat.Messages.Where(m => m.ToolCallId == subFail.Data.ToolCallId))
                    {
                        msg.ToolStatus = "Failed";
                        msg.ToolOutput = subFail.Data.Error;
                    }
                    if (IsDisplayedSession())
                    {
                        foreach (var vm in Messages.Where(m => m.Message.ToolCallId == subFail.Data.ToolCallId))
                            vm.NotifyToolStatusChanged();
                        _transcriptBuilder.UpdateSubagentToolStatus(subFail.Data.ToolCallId, "Failed");
                    }
                    });
                    break;

                case AssistantUsageEvent usage:
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Track usage data in runtime state for display/debug metrics.
                        var d = usage.Data;
                        var turnInput = (long)(d.InputTokens ?? 0);
                        runtime.TotalInputTokens += turnInput;
                        runtime.TotalOutputTokens += (long)(d.OutputTokens ?? 0);
                        // Each API call sends the full conversation context, so the latest
                        // InputTokens is the best proxy for current context window usage.
                        var usageModel = string.IsNullOrWhiteSpace(d.Model)
                            ? ResolveSelectedModelForChat(chat)
                            : d.Model;
                        var (fallbackModelId, fallbackTier) = ResolveCatalogFallbackContextWindowSelection(
                            chat,
                            runtime,
                            usageModel);
                        var knownTokenLimit = ResolveKnownContextTokenLimit(fallbackModelId, fallbackTier);
                        ApplyContextUsage(
                            chat,
                            runtime,
                            turnInput > 0 ? turnInput : null,
                            knownTokenLimit > 0 ? knownTokenLimit : null,
                            ContextTokenLimitSource.Catalog,
                            IsDisplayedSession());
                        // Persist token counts to the Chat model so they survive restarts.
                        chat.TotalInputTokens = runtime.TotalInputTokens;
                        chat.TotalOutputTokens = runtime.TotalOutputTokens;
                        if (IsDisplayedSession())
                        {
                            TotalInputTokens = runtime.TotalInputTokens;
                            TotalOutputTokens = runtime.TotalOutputTokens;
                            OnPropertyChanged(nameof(CurrentChat));
                        }
                    });
                    break;

                case SessionUsageInfoEvent sessionUsage:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var currentTokens = NormalizeTokenCount(sessionUsage.Data.CurrentTokens);
                        var eventTokenLimit = NormalizeTokenCount(sessionUsage.Data.TokenLimit);
                        var requestedModel = ResolveSelectedModelForChat(chat);
                        var (fallbackModelId, fallbackTier) = ResolveCatalogFallbackContextWindowSelection(
                            chat,
                            runtime,
                            requestedModel);
                        var knownTokenLimit = ResolveKnownContextTokenLimit(fallbackModelId, fallbackTier);
                        var (tokenLimit, tokenLimitSource) = ResolveContextTokenLimitFromSessionUsage(
                            eventTokenLimit,
                            knownTokenLimit);
                        ApplyContextUsage(
                            chat,
                            runtime,
                            currentTokens > 0 ? currentTokens : null,
                            tokenLimit > 0 ? tokenLimit : null,
                            tokenLimitSource,
                            IsDisplayedSession());
                    });
                    break;

                case SkillInvokedEvent skillInvoked:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!string.IsNullOrWhiteSpace(skillInvoked.Data.Name))
                    {
                        var skill = FindSkillReferenceByName(skillInvoked.Data.Name, projectContextCatalog);
                        pendingFetchedSkillRefs.Add(new SkillReference
                        {
                            Name = skill?.Name ?? skillInvoked.Data.Name,
                            Glyph = skill?.Glyph ?? "\u26A1",
                            Description = !string.IsNullOrWhiteSpace(skillInvoked.Data.Description)
                                ? skillInvoked.Data.Description
                                : skill?.Description ?? string.Empty,
                            Content = skillInvoked.Data.Content
                        });
                    }
                    });
                    break;

                case SessionStartEvent start:
                    Dispatcher.UIThread.Post(() =>
                    {
                    var effectiveModel = !string.IsNullOrWhiteSpace(start.Data.SelectedModel)
                        ? start.Data.SelectedModel
                        : ResolveSelectedModelForChat(chat);
                    if (IsDisplayedSession() && !string.IsNullOrWhiteSpace(effectiveModel) && !AvailableModels.Contains(effectiveModel))
                        AvailableModels.Add(effectiveModel);

                    ApplySessionModelState(
                        chat,
                        runtime,
                        effectiveModel,
                        start.Data.ReasoningEffort,
                        start.Data.ContextTier,
                        IsDisplayedSession());

                    QueueModelSelectionSave(chat);
                    });
                    break;

                case SessionTaskCompleteEvent taskComplete:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (IsDisplayedSession() && !string.IsNullOrWhiteSpace(taskComplete.Data.Summary))
                    {
                        runtime.StatusText = $"✓ {taskComplete.Data.Summary}";
                        StatusText = runtime.StatusText;
                    }
                    });
                    break;

                case SessionResumeEvent resume:
                    Dispatcher.UIThread.Post(() =>
                    {
                    var effectiveModel = !string.IsNullOrWhiteSpace(resume.Data.SelectedModel)
                        ? resume.Data.SelectedModel
                        : ResolveSelectedModelForChat(chat);
                    if (IsDisplayedSession() && !string.IsNullOrWhiteSpace(effectiveModel) && !AvailableModels.Contains(effectiveModel))
                        AvailableModels.Add(effectiveModel);

                    ApplySessionModelState(
                        chat,
                        runtime,
                        effectiveModel,
                        resume.Data.ReasoningEffort,
                        resume.Data.ContextTier,
                        IsDisplayedSession());

                    runtime.StatusText = "";
                    if (IsDisplayedSession())
                        StatusText = "";

                    QueueModelSelectionSave(chat);
                    });
                    break;

                case SessionModelChangeEvent modelChange:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!string.IsNullOrWhiteSpace(modelChange.Data.NewModel))
                    {
                        if (IsDisplayedSession() && !AvailableModels.Contains(modelChange.Data.NewModel))
                            AvailableModels.Add(modelChange.Data.NewModel);
                        ApplySessionModelState(
                            chat,
                            runtime,
                            modelChange.Data.NewModel,
                            modelChange.Data.ReasoningEffort,
                            modelChange.Data.ContextTier,
                            IsDisplayedSession());
                        // Update in-flight streaming message with the actual model used
                        if (streamingMsg is not null)
                            streamingMsg.Model = modelChange.Data.NewModel;
                    }

                    QueueModelSelectionSave(chat);
                    });
                    break;

                case SessionSnapshotRewindEvent:
                    // Server-side history rewind (e.g., from message editing) — no UI action needed
                    break;

                case SessionContextChangedEvent:
                case PendingMessagesModifiedEvent:
                case SessionHandoffEvent:
                case SessionInfoEvent:
                    // Acknowledged but no UI action needed currently
                    break;

                case SessionWorkspaceFileChangedEvent workspaceFileChanged:
                    var workspaceFilePath = ResolveWorkspaceFileChangedPath(chat, workspaceFileChanged.Data.Path);
                    var workspaceFileChangePayload = BuildWorkspaceFileChangedPayloadJson(
                        workspaceFilePath,
                        workspaceFileChanged.Data.Operation);
                    Dispatcher.UIThread.Post(() =>
                    {
                        var toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolName = ToolDisplayHelper.WorkspaceFileChangedToolName,
                            ToolStatus = "Completed",
                            Content = workspaceFileChangePayload,
                        };
                        chat.Messages.Add(toolMsg);

                        if (IsDisplayedSession())
                        {
                            Messages.Add(new ChatMessageViewModel(toolMsg));
                            ScrollToEndRequested?.Invoke();
                            // Keep the coding strip's change count live as the agent edits
                            // files, independent of the IsBusy turn lifecycle.
                            QueueLiveGitRefresh();
                        }
                    });
                    break;

                case SessionModeChangedEvent:
                    // Mode API removed — Lumi always uses the server default (interactive).
                    break;

                case SessionMcpServerStatusChangedEvent mcpStatusChanged:
                    // Live MCP lifecycle: keep the composer chip in sync as servers connect, drop, or
                    // need auth mid-conversation, and drive interactive OAuth when a remote server
                    // requests it. Fire-and-forget; the handler marshals its own UI updates.
                    _ = HandleMcpServerStatusAsync(
                        session,
                        chat.Id,
                        mcpStatusChanged.Data.ServerName,
                        mcpStatusChanged.Data.Status,
                        mcpStatusChanged.Data.Error,
                        CancellationToken.None);
                    break;

                case SessionPlanChangedEvent planChanged:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsDisplayedSession()) return;

                        var operation = planChanged.Data.Operation;
                        if (operation == PlanChangedOperation.Create || operation == PlanChangedOperation.Update)
                        {
                            HasPlan = true;
                            _ = RefreshPlan();
                            StagePlanCard(
                                operation == PlanChangedOperation.Create
                                    ? "Created a plan"
                                    : "Updated the plan");
                            PlanShowRequested?.Invoke();
                        }
                        else if (operation == PlanChangedOperation.Delete)
                        {
                            HasPlan = false;
                            PlanContent = null;
                            PlanHideRequested?.Invoke();
                        }
                    });
                    break;

                case ExitPlanModeRequestedEvent exitPlanMode:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!IsDisplayedSession())
                        return;

                    HasPlan = !string.IsNullOrWhiteSpace(exitPlanMode.Data.PlanContent);
                    PlanContent = exitPlanMode.Data.PlanContent;
                    runtime.StatusText = string.IsNullOrWhiteSpace(exitPlanMode.Data.Summary)
                        ? "Plan ready to execute"
                        : exitPlanMode.Data.Summary;

                    if (HasPlan)
                    {
                        StagePlanCard(runtime.StatusText);
                        PlanShowRequested?.Invoke();
                    }

                    StatusText = runtime.StatusText;
                    });
                    break;
            }
            }
            catch (Exception ex)
            {
                // Never let an unhandled exception escape the event handler — the SDK
                // stops delivering events to a faulted subscriber, which would leave
                // IsStreaming/IsBusy stuck permanently.
                System.Diagnostics.Debug.WriteLine($"[Lumi] Session event handler error: {ex}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsAuthoritativeSession())
                        ReconcileInProgressSubagentTools(chat, "Failed");
                    MarkRuntimeTerminal(runtime, string.Format(Loc.Status_Error, ex.Message));
                    if (IsDisplayedSession())
                    {
                        IsBusy = false;
                        IsStreaming = false;
                        StatusText = runtime.StatusText;
                        _transcriptBuilder.HideTypingIndicator();
                        _transcriptBuilder.CloseCurrentToolGroup();
                        _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
                    }
                });
            }
        });
        _sessionSubs[chat.Id] = new DisposableGroup(
            sessionSubscription,
            assistantStream,
            reasoningStream,
            new ActionDisposable(() => _copilotService.CliProcessExited -= OnCliProcessExited));
        return true;
    }

    private static void MarkRuntimeTerminal(ChatRuntimeState runtime, string? statusText = null)
    {
        runtime.IsBusy = false;
        runtime.IsStreaming = false;
        runtime.TurnInProgress = false;
        runtime.HasPendingBackgroundWork = false;
        runtime.ActiveSubagentExecutionDepth = 0;
        runtime.ExpectTurnStartUserEcho = false;
        runtime.StatusText = statusText ?? string.Empty;
    }

    private static void MarkRuntimeActive(
        ChatRuntimeState runtime,
        string? statusText = null,
        bool isStreaming = true,
        bool hasPendingBackgroundWork = false)
    {
        runtime.StatusText = string.IsNullOrWhiteSpace(statusText)
            ? string.IsNullOrWhiteSpace(runtime.StatusText) ? Loc.Status_Thinking : runtime.StatusText
            : statusText;
        runtime.IsStreaming = isStreaming;
        // TurnInProgress is set true at exactly the same points IsStreaming is (turn initiation / an
        // actively streaming turn), which makes it a strict superset of the old IsStreaming steer signal.
        // But — unlike IsStreaming — it is only cleared at turn end / terminal. Mid-turn updates
        // (compaction, sub-agent, background-task drain) and the post-turn keep-busy path all call this
        // with isStreaming:false, so they must NOT touch TurnInProgress: mid-turn it stays true (keeping
        // the turn steerable), and post-turn it stays false (already cleared by
        // MarkRuntimeWaitingForSessionIdle, so steering correctly falls back to the queue).
        if (isStreaming)
            runtime.TurnInProgress = true;
        if (hasPendingBackgroundWork)
            runtime.HasPendingBackgroundWork = true;
        runtime.IsBusy = true;
    }

    private void ApplyDisplayedRuntimeState(ChatRuntimeState runtime)
    {
        StatusText = runtime.StatusText;
        IsStreaming = runtime.IsStreaming;
        IsBusy = runtime.IsBusy;
    }

    private static void MarkRuntimeWaitingForSessionIdle(ChatRuntimeState runtime)
    {
        runtime.IsStreaming = false;
        // The assistant turn has ended; only background/idle draining may remain. Immediate steering
        // cannot inject into a turn that already ended, so drop the "turn running" signal here.
        runtime.TurnInProgress = false;
        // The turn is over, so its turn-start echo window is closed — clear the skip flag (belt-and-suspenders
        // for the rare case where the turn-start echo never arrived) so it can't leak into the next turn.
        runtime.ExpectTurnStartUserEcho = false;
        if (ShouldKeepRuntimeBusyUntilSessionIdle(runtime))
        {
            MarkRuntimeActive(
                runtime,
                string.IsNullOrWhiteSpace(runtime.StatusText) ? Loc.Status_Thinking : runtime.StatusText,
                isStreaming: false,
                hasPendingBackgroundWork: runtime.HasPendingBackgroundWork);
            return;
        }

        MarkRuntimeTerminal(runtime);
    }

    private static bool ShouldKeepRuntimeBusyUntilSessionIdle(ChatRuntimeState runtime)
        => runtime.PendingSessionUserMessageCount > 0
           || runtime.ActiveToolCount > 0
           || runtime.ActiveSubagentExecutionDepth > 0
           || runtime.HasPendingBackgroundWork;

    private static bool ShouldMarkBackgroundWorkPending(ChatRuntimeState runtime)
        => runtime.PendingSessionUserMessageCount > 0
           || runtime.ActiveToolCount > 0;

    private string ResolveWorkspaceFileChangedPath(Chat chat, string path)
    {
        var trimmedPath = path.Trim();
        if (Path.IsPathFullyQualified(trimmedPath))
            return trimmedPath;

        return Path.GetFullPath(Path.Combine(GetEffectiveWorkingDirectoryForChat(chat), trimmedPath));
    }

    private string GetEffectiveWorkingDirectoryForChat(Chat chat)
        => ResolveEffectiveWorkingDirectory(chat.ProjectId, chat.WorktreePath);

    private static string BuildWorkspaceFileChangedPayloadJson(
        string filePath,
        WorkspaceFileChangedOperation operation)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("filePath", filePath);
            writer.WriteString("operation", operation.ToString());
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Cleans up session resources for a chat (e.g., on delete).</summary>
    public void CleanupSession(Guid chatId)
    {
        var chat = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId);
        if (chat is not null)
            CancelPendingQuestions(chat);

        ReleaseSessionResources(chatId, cancelActiveRequest: true, deleteServerSession: true);
        _runtimeStates.Remove(chatId);
        RemoveSuggestionTracking(chatId);
        DisposeBrowserService(chatId);
    }

    private void DetachSessionAfterRemoteShutdown(Chat chat, bool wasActive)
    {
        DisposeSessionSubscription(chat.Id);
        // The session already ended server-side (SessionShutdownEvent), so its host runtime and MCP
        // subprocesses are already reaped — dropping the handle here leaks nothing, and a destroy RPC
        // would just fail. The persisted CopilotSessionId is intentionally kept so a later send can
        // resume once the CLI/server recovers.
        _sessionCache.Remove(chat.Id);
        if (wasActive)
            _activeSession = null;

        var runtime = GetOrCreateRuntimeState(chat.Id);
        MarkRuntimeTerminal(runtime);

        if (CurrentChat?.Id == chat.Id)
        {
            // Resolve any visible "Running in background" card and stop the 1.5s monitor. The card's
            // own 1s elapsed clock ticks independently of the monitor, so merely dropping the session
            // would leave the card counting up forever — CompleteAllBackgroundShellsAndStop flips
            // IsRunningInBackground=false and clears this chat's running-shell map + timer.
            CompleteAllBackgroundShellsAndStop();
            _transcriptBuilder.HideTypingIndicator();
            _transcriptBuilder.CloseCurrentToolGroup();
            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
            IsBusy = false;
            IsStreaming = false;
            StatusText = string.Empty;
        }
    }

    /// <summary>Called when the CopilotService reconnects (new CLI process).
    /// All cached session objects are from the old process and must be discarded,
    /// but persisted session IDs can still be resumed on the new client.</summary>
    private void OnCopilotReconnected()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ResetAfterCopilotReconnect();
            return;
        }

        Dispatcher.UIThread.Post(ResetAfterCopilotReconnect);
    }

    /// <summary>Handles server-side session deletion by detaching the local session
    /// so the next send creates a fresh one instead of failing on resume.</summary>
    private void OnSessionDeletedRemotely(string sessionId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var chat = _dataStore.Data.Chats.FirstOrDefault(
                c => string.Equals(c.CopilotSessionId, sessionId, StringComparison.Ordinal));
            if (chat is null) return;

            DetachPersistedSession(chat, sessionId);
        });
    }

    private void ResetAfterCopilotReconnect()
    {
        // Dispose all event subscriptions
        foreach (var sub in _sessionSubs.Values)
            sub.Dispose();
        _sessionSubs.Clear();

        // Clear session cache: every cached session belongs to the OLD CLI process, which has died
        // (that is why we are reconnecting). Its child MCP subprocesses died with it, and a destroy
        // RPC can't be delivered over the dead connection anyway, so there is nothing to reap here —
        // dropping the handles is correct. Persisted CopilotSessionIds remain resumable on the new client.
        _sessionCache.Clear();
        _activeSession = null;

        // Those sessions can never reconnect, so drop their per-session OAuth chip/login state too
        // (keyed by sessionId, this map would otherwise orphan entries on every reconnect).
        lock (_mcpOAuthLoginLock)
        {
            _mcpOAuthLoginAttempts.Clear();
            _mcpOAuthResolvedMessages.Clear();
        }

        // Cancel any in-flight requests
        foreach (var cts in _ctsSources.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _ctsSources.Clear();
        _modelSelectionSaveCts?.Cancel();
        _modelSelectionSaveCts?.Dispose();
        _modelSelectionSaveCts = null;
        _modelSelectionSyncCts?.Cancel();
        _modelSelectionSyncCts?.Dispose();
        _modelSelectionSyncCts = null;

        // Reset busy state on all runtimes
        foreach (var runtime in _runtimeStates.Values)
        {
            runtime.PendingSessionUserMessageCount = 0;
            runtime.PendingAssistantMessageCount = 0;
            runtime.ActiveToolCount = 0;
            runtime.PendingTurnSequence++;
            runtime.PostToolReconciliationCts?.Cancel();
            runtime.PostToolReconciliationCts?.Dispose();
            runtime.PostToolReconciliationCts = null;
            // Every session died with the old CLI process, so no background shell can still be running;
            // drop each chat's running-shell seed map so a later switch-back does not resurrect a stuck
            // "Running in background" card (RebuildTranscript would otherwise re-seed from it).
            runtime.RunningBackgroundShells.Clear();
            if (runtime.Chat is { } runtimeChat)
                ReconcileInProgressSubagentTools(runtimeChat, "Failed");
            MarkRuntimeTerminal(runtime);
        }

        // Resolve any visible running-background card on the current chat and stop the monitor timer
        // (its card clock ticks independently of the session, so it must be flipped off, not just
        // abandoned).
        CompleteAllBackgroundShellsAndStop();

        _inProgressMessages.Clear();

        IsBusy = false;
        IsStreaming = false;
        StatusText = "";
    }

    private ChatRuntimeState GetOrCreateRuntimeState(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
        {
            var chat = _dataStore.Data.Chats.Find(c => c.Id == chatId);
            runtime = new ChatRuntimeState
            {
                Chat = chat,
                TotalInputTokens = chat?.TotalInputTokens ?? 0,
                TotalOutputTokens = chat?.TotalOutputTokens ?? 0,
                ContextCurrentTokens = chat?.ContextCurrentTokens ?? 0,
                ContextTokenLimit = chat?.ContextTokenLimit ?? 0,
            };
            if (chat is not null)
                ApplyKnownContextTokenLimit(chat, runtime, ResolveSelectedModelForChat(chat), updateDisplayed: false);
            _runtimeStates[chatId] = runtime;
        }
        return runtime;
    }
}
