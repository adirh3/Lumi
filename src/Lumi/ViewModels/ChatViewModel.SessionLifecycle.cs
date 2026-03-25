using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitHub.Copilot.SDK;
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

    /// <summary>Subscribes to events on a CopilotSession. Each subscription captures its own
    /// streaming state via closures and always updates the Chat model. UI updates are gated
    /// on _activeSession so only the displayed chat's events touch the UI.</summary>
    private void SubscribeToSession(CopilotSession session, Chat chat)
    {
        // Dispose previous subscription for this chat (e.g., session was resumed)
        if (_sessionSubs.TryGetValue(chat.Id, out var oldSub))
            oldSub.Dispose();
        _sessionCache[chat.Id] = session;

        // Per-session streaming state — captured by closure, independent per subscription
        ChatMessage? streamingMsg = null;
        ChatMessage? reasoningMsg = null;
        ChatMessageViewModel? streamingVm = null;
        ChatMessageViewModel? reasoningVm = null;
        var agentName = ActiveAgent?.Name ?? Loc.Author_Lumi;
        var runtime = GetOrCreateRuntimeState(chat.Id);
        var toolParentById = new Dictionary<string, string?>(StringComparer.Ordinal);
        var terminalRootByToolCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        var externalToolCallIdByRequestId = new Dictionary<string, string>(StringComparer.Ordinal);
        StreamingTextAccumulator? assistantStream = null;
        StreamingTextAccumulator? reasoningStream = null;
        var activeSubagentSelectionDepth = 0;
        var activeSubagentExecutionDepth = 0;
        var subagentStateGate = new object();
        var activeSubagentToolCallIds = new List<string>();
        var subagentAssistantStreams = new Dictionary<string, StreamingTextAccumulator>(StringComparer.Ordinal);
        var subagentReasoningStreams = new Dictionary<string, StreamingTextAccumulator>(StringComparer.Ordinal);
        string? mostRecentSubagentToolCallId = null;


        bool IsSubagentOutputActive()
            => Volatile.Read(ref activeSubagentSelectionDepth) > 0
               || Volatile.Read(ref activeSubagentExecutionDepth) > 0;

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

        void UnregisterActiveSubagent(string? toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
                return;

            lock (subagentStateGate)
            {
                for (var i = activeSubagentToolCallIds.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(activeSubagentToolCallIds[i], toolCallId, StringComparison.Ordinal))
                        continue;

                    activeSubagentToolCallIds.RemoveAt(i);
                    break;
                }

                mostRecentSubagentToolCallId = activeSubagentToolCallIds.Count > 0
                    ? activeSubagentToolCallIds[^1]
                    : toolCallId;
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
                if (_activeSession == session)
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

            // Direct update for immediate UI visibility (runs on UI thread via UiThrottler)
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
            Volatile.Write(ref activeSubagentSelectionDepth, 0);
            Volatile.Write(ref activeSubagentExecutionDepth, 0);
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
                    Model = chat.LastModelUsed ?? SelectedModel
                };
                _inProgressMessages[chat.Id] = streamingMsg;
                if (_activeSession == session)
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
            if (_activeSession == session)
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
                if (_activeSession == session)
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
            if (_activeSession == session)
            {
                reasoningVm?.NotifyContentChanged();
                StatusText = runtime.StatusText;
                ScrollToEndRequested?.Invoke();
            }
        }

        assistantStream = new StreamingTextAccumulator(
            4096,
            TimeSpan.FromMilliseconds(StreamingUiUpdateThrottleMs),
            FlushAssistantDelta);
        reasoningStream = new StreamingTextAccumulator(
            1024,
            TimeSpan.FromMilliseconds(StreamingUiUpdateThrottleMs),
            FlushReasoningDelta);

        var sessionSubscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantTurnStartEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                        runtime.IsBusy = true;
                        runtime.IsStreaming = true;
                        runtime.StatusText = Loc.Status_Thinking;
                        if (_activeSession == session)
                        {
                            IsBusy = runtime.IsBusy;
                            IsStreaming = runtime.IsStreaming;
                            StatusText = runtime.StatusText;
                        }
                    });
                    break;

                case AssistantMessageDeltaEvent delta:
                    var activeSubagentToolCallIdForAssistantDelta =
                        GetSubagentToolCallIdFromParent(delta.Data.ParentToolCallId)
                        ?? GetCurrentSubagentOutputToolCallId();
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
                    var activeSubagentToolCallIdForAssistantMessage =
                        GetSubagentToolCallIdFromParent(msg.Data.ParentToolCallId)
                        ?? GetCurrentSubagentOutputToolCallId();
                    if (!string.IsNullOrWhiteSpace(activeSubagentToolCallIdForAssistantMessage))
                    {
                        var subagentAssistantStream = GetSubagentStream(
                            subagentAssistantStreams,
                            activeSubagentToolCallIdForAssistantMessage);
                        subagentAssistantStream?.CancelPending();
                        var capturedTranscript = msg.Data.Content;
                        var capturedReasoning = msg.Data.ReasoningText;
                        var capturedToolCallId = activeSubagentToolCallIdForAssistantMessage;
                        Dispatcher.UIThread.Post(() =>
                        {
                            _transcriptBuilder.UpdateSubagentTranscriptText(capturedToolCallId, capturedTranscript);
                            if (!string.IsNullOrWhiteSpace(capturedReasoning))
                                _transcriptBuilder.UpdateSubagentReasoningText(capturedToolCallId, capturedReasoning);
                        });
                        UpdateSubagentCardContent(
                            activeSubagentToolCallIdForAssistantMessage,
                            updateTranscript: true,
                            transcript: msg.Data.Content,
                            updateReasoning: !string.IsNullOrWhiteSpace(msg.Data.ReasoningText),
                            reasoning: msg.Data.ReasoningText);
                        subagentAssistantStream?.Clear();
                        break;
                    }
                    if (IsSubagentOutputActive())
                        break;
                    var capturedFinalContent = msg.Data.Content?.TrimStart('\n', '\r');
                    assistantStream.CancelPending();
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Flush any buffered deltas that CancelPending() may have
                        // prevented from reaching the UI. Without this, a fast
                        // completion can cancel the throttled flush before it ever
                        // creates the streaming message, causing the entire response
                        // to appear at once instead of streaming.
                        FlushAssistantDelta();

                        var finalContent = capturedFinalContent;
                        if (string.IsNullOrWhiteSpace(finalContent))
                        {
                            if (_activeSession == session && streamingVm is not null)
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
                                Model = chat.LastModelUsed ?? SelectedModel
                            };
                            if (_pendingSearchSources.Count > 0)
                            {
                                completedMessage.Sources.AddRange(_pendingSearchSources);
                                _pendingSearchSources.Clear();
                            }
                            if (_transcriptBuilder.PendingFetchedSkillRefs.Count > 0)
                            {
                                completedMessage.ActiveSkills.AddRange(_transcriptBuilder.PendingFetchedSkillRefs);
                                _transcriptBuilder.PendingFetchedSkillRefs.Clear();
                            }
                            chat.Messages.Add(completedMessage);
                            if (_activeSession == session)
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
                            if (_pendingSearchSources.Count > 0)
                            {
                                streamingMsg.Sources.AddRange(_pendingSearchSources);
                                _pendingSearchSources.Clear();
                            }
                            if (_transcriptBuilder.PendingFetchedSkillRefs.Count > 0)
                            {
                                streamingMsg.ActiveSkills.AddRange(_transcriptBuilder.PendingFetchedSkillRefs);
                                _transcriptBuilder.PendingFetchedSkillRefs.Clear();
                            }
                            chat.Messages.Add(streamingMsg);
                            if (_activeSession == session)
                                streamingVm?.NotifyStreamingEnded();
                        }

                        _inProgressMessages.Remove(chat.Id);
                        streamingMsg = null;
                        streamingVm = null;
                        assistantStream.Clear();
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
                            if (_activeSession == session)
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
                            if (_activeSession == session)
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
                    runtime.StatusText = ToolDisplayHelper.FormatProgressLabel(displayName);
                    var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == startToolCallId);
                    if (toolMsg is null)
                    {
                        toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = startToolCallId,
                            ParentToolCallId = toolStart.Data.ParentToolCallId,
                            ToolName = toolStart.Data.ToolName,
                            ToolStatus = "InProgress",
                            Content = toolStart.Data.Arguments?.ToString() ?? "",
                            Author = displayName
                        };
                        chat.Messages.Add(toolMsg);
                    }
                    else
                    {
                        toolMsg.ParentToolCallId = toolStart.Data.ParentToolCallId;
                        toolMsg.ToolName = toolStart.Data.ToolName;
                        toolMsg.ToolStatus = "InProgress";
                        toolMsg.Content = toolStart.Data.Arguments?.ToString() ?? "";
                        toolMsg.Author = displayName;
                    }

                    if (_activeSession == session)
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

                        StatusText = runtime.StatusText;
                    }
                    });
                    break;

                case ToolExecutionPartialResultEvent partial:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (_activeSession != session)
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
                    if (_activeSession != session)
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
                    Dispatcher.UIThread.Post(() =>
                    {
                    toolParentById[toolEnd.Data.ToolCallId] = toolEnd.Data.ParentToolCallId;

                    var success = toolEnd.Data.Success == true;
                    var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == toolEnd.Data.ToolCallId);
                    if (toolMsg is not null)
                    {
                        toolMsg.ToolStatus = success ? "Completed" : "Failed";
                        if (_activeSession == session)
                        {
                            var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == toolEnd.Data.ToolCallId);
                            vm?.NotifyToolStatusChanged();
                        }

                        var toolName = toolMsg.ToolName;

                        if (success)
                        {
                            // Track fetched skills for attachment to the assistant message
                            if (toolName == "fetch_skill")
                            {
                                string? skillName = null;
                                try
                                {
                                    using var doc = JsonDocument.Parse(toolMsg.Content);
                                    if (doc.RootElement.TryGetProperty("name", out var nameProp))
                                        skillName = nameProp.GetString();
                                }
                                catch { }
                                if (!string.IsNullOrEmpty(skillName))
                                {
                                    var skill = FindSkillByName(skillName);
                                    _transcriptBuilder.PendingFetchedSkillRefs.Add(new SkillReference
                                    {
                                        Name = skillName,
                                        Glyph = skill?.IconGlyph ?? "\u26A1",
                                        Description = skill?.Description ?? string.Empty
                                    });
                                }
                            }

                            if ((ToolDisplayHelper.IsFileCreationTool(toolName) || toolName == "powershell")
                                && toolEnd.Data.Result?.Contents is { Length: > 0 } contents)
                            {
                                foreach (var item in contents)
                                {
                                    if (item is ToolExecutionCompleteDataResultContentsItemResourceLink rl
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

                        if (ToolDisplayHelper.IsTerminalStreamingTool(toolName) && _activeSession == session)
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
                        }
                    }
                    });
                    break;


                case ExternalToolRequestedEvent externalToolRequest:
                    AdjustPendingToolCount(chat.Id, 1);
                    Dispatcher.UIThread.Post(() =>
                    {
                    externalToolCallIdByRequestId[externalToolRequest.Data.RequestId] = externalToolRequest.Data.ToolCallId;

                    var arguments = externalToolRequest.Data.Arguments?.ToString();
                    var displayName = ToolDisplayHelper.FormatToolStatusName(externalToolRequest.Data.ToolName, arguments);
                    runtime.StatusText = ToolDisplayHelper.FormatProgressLabel(displayName);

                    var toolMsg = chat.Messages.LastOrDefault(m => m.ToolCallId == externalToolRequest.Data.ToolCallId);
                    if (toolMsg is null)
                    {
                        toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = externalToolRequest.Data.ToolCallId,
                            ToolName = externalToolRequest.Data.ToolName,
                            ToolStatus = "InProgress",
                            Content = arguments ?? "",
                            Author = displayName
                        };
                        chat.Messages.Add(toolMsg);
                    }
                    else
                    {
                        toolMsg.ToolName = externalToolRequest.Data.ToolName;
                        toolMsg.ToolStatus = "InProgress";
                        toolMsg.Content = arguments ?? "";
                        toolMsg.Author = displayName;
                    }

                    if (_activeSession == session)
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

                        StatusText = runtime.StatusText;
                    }
                    });
                    break;

                case ExternalToolCompletedEvent externalToolComplete:
                    var shouldReconcileAfterExternalTool = AdjustPendingToolCount(chat.Id, -1);
                    if (shouldReconcileAfterExternalTool)
                        SchedulePostToolReconciliation(chat.Id);
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!externalToolCallIdByRequestId.TryGetValue(externalToolComplete.Data.RequestId, out var externalToolCallId))
                        return;

                    externalToolCallIdByRequestId.Remove(externalToolComplete.Data.RequestId);

                    foreach (var msg in chat.Messages.Where(m => m.ToolCallId == externalToolCallId))
                        msg.ToolStatus = "Completed";

                    if (_activeSession == session)
                    {
                        foreach (var vm in Messages.Where(m => m.Message.ToolCallId == externalToolCallId))
                            vm.NotifyToolStatusChanged();
                    }
                    });
                    break;

                case CommandQueuedEvent commandQueued:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = $"Queued command: {commandQueued.Data.Command}";
                    if (_activeSession == session)
                        StatusText = runtime.StatusText;
                    });
                    break;

                case AssistantTurnEndEvent:
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.IsBusy = false;
                    runtime.IsStreaming = false;
                    runtime.StatusText = "";
                    if (_activeSession == session)
                    {
                        _transcriptBuilder.HideTypingIndicator();
                        _transcriptBuilder.CloseCurrentToolGroup();
                        IsBusy = runtime.IsBusy;
                        IsStreaming = runtime.IsStreaming;
                        StatusText = runtime.StatusText;
                    }
                    QueueSaveChat(chat, saveIndex: true, touchIndex: true);
                    });
                    break;

                case SessionIdleEvent idle:
                    ClearPendingTurnTracking(chat.Id);

                    // The SDK tells us if background tasks (shells/agents) are still running.
                    var bg = idle.Data?.BackgroundTasks;
                    var hasPendingBgWork = (bg?.Shells is { Length: > 0 }) || (bg?.Agents is { Length: > 0 });
                    runtime.HasPendingBackgroundWork = hasPendingBgWork;

                    if (hasPendingBgWork)
                    {
                        // Background tasks still running — keep session alive, skip cleanup.
                        break;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                    // Mark chat as unread if user is on a different chat
                    if (CurrentChat?.Id != chat.Id)
                        chat.HasUnreadMessages = true;

                    if (_dataStore.Data.Settings.NotificationsEnabled)
                    {
                        var chatTitle = chat.Title;
                        var body = string.IsNullOrWhiteSpace(chatTitle)
                            ? Loc.Notification_ResponseReady
                            : $"{chatTitle} — {Loc.Notification_ResponseReady}";
                        NotificationService.ShowIfInactive(agentName, body);
                    }

                    // Flush file changes only when session is truly idle (not between agentic turns).
                    if (_activeSession == session)
                        _transcriptBuilder.FlushPendingFileEdits();

                    // Memory checkpoint + suggestions only when session is truly idle.
                    // Running these on every AssistantTurnEndEvent creates a storm of
                    // background sessions that can starve the CLI process and stall
                    // all active sessions.
                    QueueAutonomousMemoryCheckpoint(chat);

                    // Generate follow-up suggestions once the full assistant response is done.
                    if (_activeSession == session && CurrentChat?.Id == chat.Id)
                        QueueSuggestionGenerationForLatestAssistant(chat);

                    if (CurrentChat?.Id != chat.Id)
                        QueueSaveChat(chat, saveIndex: false, releaseIfInactive: true);
                    });
                    break;

                case SessionTitleChangedEvent title:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!_dataStore.Data.Settings.AutoGenerateTitles) return;
                    chat.Title = title.Data.Title;
                    _dataStore.MarkChatChanged(chat);
                    if (CurrentChat?.Id == chat.Id)
                        OnPropertyChanged(nameof(CurrentChatTitle));
                    if (_dataStore.Data.Settings.AutoSaveChats)
                        _ = SaveIndexAsync();
                    ChatTitleChanged?.Invoke(chat.Id, chat.Title);
                    });
                    break;

                case SessionErrorEvent err:
                    ClearPendingTurnTracking(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                    // Clean up any in-progress streaming message
                    if (streamingMsg is not null)
                    {
                        _inProgressMessages.Remove(chat.Id);
                        if (_activeSession == session)
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
                        if (_activeSession == session)
                        {
                            reasoningVm?.NotifyStreamingEnded();
                        }
                        reasoningMsg = null;
                        reasoningVm = null;
                    }
                    reasoningStream.Clear();

                    runtime.IsBusy = false;
                    runtime.IsStreaming = false;
                    runtime.StatusText = string.Format(Loc.Status_Error, err.Data.Message);
                    if (_activeSession == session)
                    {
                        // Clean up typing indicator and tool groups
                        _transcriptBuilder.HideTypingIndicator();
                        _transcriptBuilder.CloseCurrentToolGroup();
                        _transcriptBuilder.FlushPendingFileEdits();

                        StatusText = runtime.StatusText;
                        IsBusy = runtime.IsBusy;
                        IsStreaming = runtime.IsStreaming;

                        // Surface the error as a visible chat message
                        var errorMsg = new ChatMessage
                        {
                            Role = "error",
                            Author = Loc.Author_Lumi,
                            Content = string.Format(Loc.Status_Error, err.Data.Message)
                        };
                        chat.Messages.Add(errorMsg);
                        Messages.Add(new ChatMessageViewModel(errorMsg));
                        _transcriptBuilder.ProcessMessageToTranscript(Messages[^1]);
                        ScrollToEndRequested?.Invoke();
                    }
                    QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);
                    });
                    break;

                case SessionCompactionStartEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = Loc.Status_Compacting;
                    if (_activeSession == session)
                        StatusText = runtime.StatusText;
                    });
                    break;

                case SessionCompactionCompleteEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = "";
                    if (_activeSession == session)
                        StatusText = runtime.StatusText;
                    });
                    break;

                case SessionTruncationEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = Loc.Status_Truncated;
                    if (_activeSession == session)
                        StatusText = runtime.StatusText;
                    });
                    break;

                case SessionWarningEvent warn:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = string.Format(Loc.Status_Warning, warn.Data.WarningType);
                    if (_activeSession == session)
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
                        _transcriptBuilder.ProcessMessageToTranscript(Messages[^1]);
                        ScrollToEndRequested?.Invoke();
                    }
                    });
                    break;

                case AbortEvent abort:
                    ClearPendingTurnTracking(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                    // SDK-initiated abort — clean up streaming state
                    if (streamingMsg is not null)
                    {
                        streamingMsg.IsStreaming = false;
                        if (!string.IsNullOrWhiteSpace(streamingMsg.Content))
                        {
                            chat.Messages.Add(streamingMsg);
                            if (_activeSession == session)
                            {
                                streamingVm?.NotifyStreamingEnded();
                            }
                        }
                        else
                        {
                            _inProgressMessages.Remove(chat.Id);
                            if (_activeSession == session)
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
                        if (_activeSession == session)
                        {
                            reasoningVm?.NotifyStreamingEnded();
                        }
                        reasoningMsg = null;
                        reasoningVm = null;
                    }
                    reasoningStream.Clear();
                    runtime.IsBusy = false;
                    runtime.IsStreaming = false;
                    runtime.StatusText = Loc.Status_Stopped;
                    if (_activeSession == session)
                    {
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
                    ClearPendingTurnTracking(chat.Id);
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    ResetSubagentOutputState();
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Clear local session objects, but keep the persisted session ID so
                        // a later resume attempt can retry after the CLI/server recovers.
                        DetachSessionAfterRemoteShutdown(chat, wasActive: _activeSession == session);
                        assistantStream.Clear();
                        reasoningStream.Clear();
                        QueueSaveChat(chat, saveIndex: true, releaseIfInactive: CurrentChat?.Id != chat.Id, touchIndex: true);
                    });
                    break;

                // ── New SDK event handlers ──

                case AssistantIntentEvent intent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!string.IsNullOrWhiteSpace(intent.Data.Intent))
                    {
                        runtime.StatusText = intent.Data.Intent;
                        if (_activeSession == session)
                            StatusText = runtime.StatusText;
                    }
                    });
                    break;

                case SubagentSelectedEvent:
                    Interlocked.Increment(ref activeSubagentSelectionDepth);
                    break;

                case SubagentDeselectedEvent:
                    if (Volatile.Read(ref activeSubagentSelectionDepth) > 0)
                        Interlocked.Decrement(ref activeSubagentSelectionDepth);
                    if (!IsSubagentOutputActive())
                    {
                        lock (subagentStateGate)
                            mostRecentSubagentToolCallId = null;
                    }
                    break;

                case SubagentStartedEvent subStart:
                    Interlocked.Increment(ref activeSubagentExecutionDepth);
                    RegisterActiveSubagent(subStart.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
                    var displayName = subStart.Data.AgentDisplayName ?? subStart.Data.AgentName ?? "Agent";
                    runtime.StatusText = $"⚡ {displayName}";
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
                        if (_activeSession == session)
                        {
                            var vm = Messages.LastOrDefault(m => m.Message.ToolCallId == subStart.Data.ToolCallId);
                            vm?.NotifyContentChanged();
                            StatusText = runtime.StatusText;
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
                        if (_activeSession == session)
                        {
                            Messages.Add(new ChatMessageViewModel(toolMsg));
                            StatusText = runtime.StatusText;
                            ScrollToEndRequested?.Invoke();
                        }
                    }
                    });
                    break;

                case SubagentCompletedEvent subEnd:
                    if (Volatile.Read(ref activeSubagentExecutionDepth) > 0)
                        Interlocked.Decrement(ref activeSubagentExecutionDepth);
                    UnregisterActiveSubagent(subEnd.Data.ToolCallId);
                    CompleteSubagentStreams(subEnd.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
                    // Mark ALL messages with this ToolCallId as Completed
                    // (covers both ToolExecutionStart and SubagentStarted entries).
                    foreach (var msg in chat.Messages.Where(m => m.ToolCallId == subEnd.Data.ToolCallId))
                        msg.ToolStatus = "Completed";
                    if (_activeSession == session)
                    {
                        foreach (var vm in Messages.Where(m => m.Message.ToolCallId == subEnd.Data.ToolCallId))
                            vm.NotifyToolStatusChanged();
                    }
                    });
                    break;

                case SubagentFailedEvent subFail:
                    if (Volatile.Read(ref activeSubagentExecutionDepth) > 0)
                        Interlocked.Decrement(ref activeSubagentExecutionDepth);
                    UnregisterActiveSubagent(subFail.Data.ToolCallId);
                    CompleteSubagentStreams(subFail.Data.ToolCallId);
                    Dispatcher.UIThread.Post(() =>
                    {
                    // Mark ALL messages with this ToolCallId as Failed
                    foreach (var msg in chat.Messages.Where(m => m.ToolCallId == subFail.Data.ToolCallId))
                    {
                        msg.ToolStatus = "Failed";
                        msg.ToolOutput = subFail.Data.Error;
                    }
                    if (_activeSession == session)
                    {
                        foreach (var vm in Messages.Where(m => m.Message.ToolCallId == subFail.Data.ToolCallId))
                            vm.NotifyToolStatusChanged();
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
                        if (turnInput > 0)
                            runtime.ContextCurrentTokens = turnInput;
                        // Persist token counts to the Chat model so they survive restarts.
                        chat.TotalInputTokens = runtime.TotalInputTokens;
                        chat.TotalOutputTokens = runtime.TotalOutputTokens;
                        if (_activeSession == session)
                        {
                            TotalInputTokens = runtime.TotalInputTokens;
                            TotalOutputTokens = runtime.TotalOutputTokens;
                            ContextCurrentTokens = runtime.ContextCurrentTokens;
                            OnPropertyChanged(nameof(CurrentChat));
                        }
                    });
                    break;

                case SessionUsageInfoEvent sessionUsage:
                    Dispatcher.UIThread.Post(() =>
                    {
                        runtime.ContextTokenLimit = (long)sessionUsage.Data.TokenLimit;
                        if (_activeSession == session)
                            ContextTokenLimit = runtime.ContextTokenLimit;
                    });
                    break;

                case SkillInvokedEvent skillInvoked:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!string.IsNullOrWhiteSpace(skillInvoked.Data.Name))
                    {
                        var skill = FindSkillByName(skillInvoked.Data.Name);
                        _transcriptBuilder.PendingFetchedSkillRefs.Add(new SkillReference
                        {
                            Name = skillInvoked.Data.Name,
                            Glyph = skill?.IconGlyph ?? "\u26A1",
                            Description = skill?.Description ?? string.Empty
                        });
                    }
                    });
                    break;

                case SessionTaskCompleteEvent taskComplete:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (_activeSession == session && !string.IsNullOrWhiteSpace(taskComplete.Data.Summary))
                    {
                        runtime.StatusText = $"✓ {taskComplete.Data.Summary}";
                        StatusText = runtime.StatusText;
                    }
                    });
                    break;

                case SessionResumeEvent resume:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = "";
                    if (_activeSession == session)
                        StatusText = "";
                    });
                    break;

                case SessionModelChangeEvent modelChange:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (_activeSession == session && !string.IsNullOrWhiteSpace(modelChange.Data.NewModel))
                    {
                        if (!AvailableModels.Contains(modelChange.Data.NewModel))
                            AvailableModels.Add(modelChange.Data.NewModel);
                        SelectedModel = modelChange.Data.NewModel;
                        // Update in-flight streaming message with the actual model used
                        if (streamingMsg is not null)
                            streamingMsg.Model = modelChange.Data.NewModel;
                    }
                    });
                    break;

                case SessionSnapshotRewindEvent:
                    // Server-side history rewind (e.g., from message editing) — no UI action needed
                    break;

                case SessionContextChangedEvent:
                case SessionWorkspaceFileChangedEvent:
                case PendingMessagesModifiedEvent:
                case SessionHandoffEvent:
                case SessionInfoEvent:
                    // Acknowledged but no UI action needed currently
                    break;

                case SessionModeChangedEvent:
                    // Mode API removed — Lumi always uses the server default (interactive).
                    break;

                case SessionPlanChangedEvent planChanged:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (_activeSession != session) return;
                    switch (planChanged.Data.Operation)
                    {
                        case SessionPlanChangedDataOperation.Create:
                        case SessionPlanChangedDataOperation.Update:
                            HasPlan = true;
                            _ = RefreshPlan();
                            StagePlanCard(
                                planChanged.Data.Operation == SessionPlanChangedDataOperation.Create
                                    ? "Created a plan"
                                    : "Updated the plan");
                            PlanShowRequested?.Invoke();
                            break;
                        case SessionPlanChangedDataOperation.Delete:
                            HasPlan = false;
                            PlanContent = null;
                            PlanHideRequested?.Invoke();
                            break;
                    }
                    });
                    break;

                case ExitPlanModeRequestedEvent exitPlanMode:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (_activeSession != session)
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
        });
        _sessionSubs[chat.Id] = new DisposableGroup(
            sessionSubscription,
            assistantStream,
            reasoningStream);
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
        _dataStore.RemoveChatLoadLock(chatId);
    }

    private void DetachSessionAfterRemoteShutdown(Chat chat, bool wasActive)
    {
        DisposeSessionSubscription(chat.Id);
        _sessionCache.Remove(chat.Id);
        if (wasActive)
            _activeSession = null;

        var runtime = GetOrCreateRuntimeState(chat.Id);
        runtime.IsBusy = false;
        runtime.IsStreaming = false;
        runtime.StatusText = string.Empty;

        if (CurrentChat?.Id == chat.Id)
        {
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

    private void ResetAfterCopilotReconnect()
    {
        // Dispose all event subscriptions
        foreach (var sub in _sessionSubs.Values)
            sub.Dispose();
        _sessionSubs.Clear();

        // Clear session cache (objects reference the dead client)
        _sessionCache.Clear();
        _activeSession = null;

        // Cancel any in-flight requests
        foreach (var cts in _ctsSources.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _ctsSources.Clear();

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
            runtime.IsBusy = false;
            runtime.IsStreaming = false;
            runtime.StatusText = "";
        }

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
            };
            _runtimeStates[chatId] = runtime;
        }
        return runtime;
    }
}
