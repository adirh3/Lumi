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

        static string FormatPermissionResult(PermissionCompletedDataResultKind kind) => kind switch
        {
            PermissionCompletedDataResultKind.Approved => "approved",
            PermissionCompletedDataResultKind.DeniedByRules => "denied by rules",
            PermissionCompletedDataResultKind.DeniedNoApprovalRuleAndCouldNotRequestFromUser => "denied because approval could not be requested",
            PermissionCompletedDataResultKind.DeniedInteractivelyByUser => "denied by user",
            PermissionCompletedDataResultKind.DeniedByContentExclusionPolicy => "denied by policy",
            _ => kind.ToString()
        };

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
                    IsStreaming = true
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
                    assistantStream.Append(delta.Data.DeltaContent);
                    break;

                case AssistantMessageEvent msg:
                    assistantStream.CancelPending();
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Flush any buffered deltas that CancelPending() may have
                        // prevented from reaching the UI. Without this, a fast
                        // completion can cancel the throttled flush before it ever
                        // creates the streaming message, causing the entire response
                        // to appear at once instead of streaming.
                        FlushAssistantDelta();

                        var finalContent = msg.Data.Content;
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
                                IsStreaming = false
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
                    reasoningStream.Append(rd.Data.DeltaContent);
                    break;

                case AssistantReasoningEvent r:
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
                                        Glyph = skill?.IconGlyph ?? "\u26A1"
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

                case PermissionRequestedEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                    runtime.StatusText = "Awaiting permission approval";
                    if (_activeSession == session)
                        StatusText = runtime.StatusText;
                    });
                    break;

                case PermissionCompletedEvent permissionComplete:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (permissionComplete.Data.Result.Kind == PermissionCompletedDataResultKind.Approved)
                        return;

                    runtime.StatusText = "";
                    var permissionMsg = new ChatMessage
                    {
                        Role = "system",
                        Author = "Permission",
                        Content = $"Permission request was {FormatPermissionResult(permissionComplete.Data.Result.Kind)}."
                    };
                    chat.Messages.Add(permissionMsg);

                    if (_activeSession == session)
                    {
                        StatusText = runtime.StatusText;
                        Messages.Add(new ChatMessageViewModel(permissionMsg));
                        _transcriptBuilder.ProcessMessageToTranscript(Messages[^1]);
                        ScrollToEndRequested?.Invoke();
                    }
                    });
                    break;

                case ExternalToolRequestedEvent externalToolRequest:
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
                    QueueSaveChat(chat, saveIndex: true);
                    });
                    break;

                case SessionIdleEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
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
                    });
                    break;

                case SessionTitleChangedEvent title:
                    Dispatcher.UIThread.Post(() =>
                    {
                    if (!_dataStore.Data.Settings.AutoGenerateTitles) return;
                    chat.Title = title.Data.Title;
                    chat.UpdatedAt = DateTimeOffset.Now;
                    if (CurrentChat?.Id == chat.Id)
                        OnPropertyChanged(nameof(CurrentChatTitle));
                    if (_dataStore.Data.Settings.AutoSaveChats)
                        _ = SaveIndexAsync();
                    ChatTitleChanged?.Invoke(chat.Id, chat.Title);
                    });
                    break;

                case SessionErrorEvent err:
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
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
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
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
                    QueueSaveChat(chat, saveIndex: false);
                    });
                    break;

                case SessionShutdownEvent shutdown:
                    assistantStream.CancelPending();
                    reasoningStream.CancelPending();
                    Dispatcher.UIThread.Post(() =>
                    {
                    // Session terminated server-side — invalidate cached session
                    _sessionCache.Remove(chat.Id);
                    chat.CopilotSessionId = null;
                    var wasActive = _activeSession == session;
                    if (wasActive)
                        _activeSession = null;

                    runtime.IsBusy = false;
                    runtime.IsStreaming = false;
                    runtime.StatusText = "";
                    if (wasActive)
                    {
                        IsBusy = false;
                        IsStreaming = false;
                        StatusText = "";
                    }
                    assistantStream.Clear();
                    reasoningStream.Clear();
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
                    // Informational only — the subagent was selected but hasn't started yet.
                    // No UI action needed; SubagentStartedEvent will create the tool entry.
                    break;

                case SubagentDeselectedEvent:
                    // Informational only — the subagent was deselected after finishing.
                    break;

                case SubagentStartedEvent subStart:
                    Dispatcher.UIThread.Post(() =>
                    {
                    var displayName = subStart.Data.AgentDisplayName ?? subStart.Data.AgentName ?? "Agent";
                    runtime.StatusText = $"⚡ {displayName}";
                    var subagentPayload = BuildSubagentPayloadJson(
                        description: string.Empty,
                        agentName: subStart.Data.AgentName,
                        agentDisplayName: displayName,
                        agentDescription: subStart.Data.AgentDescription,
                        mode: string.Empty);

                    // The SDK fires ToolExecutionStartEvent before SubagentStartedEvent
                    // with the same ToolCallId — reuse that message instead of duplicating.
                    var existing = chat.Messages.LastOrDefault(m => m.ToolCallId == subStart.Data.ToolCallId);
                    if (existing is not null)
                    {
                        var existingDescription = ToolDisplayHelper.ExtractJsonField(existing.Content, "description") ?? string.Empty;
                        var existingMode = ToolDisplayHelper.ExtractJsonField(existing.Content, "mode") ?? string.Empty;
                        existing.ToolName = $"agent:{subStart.Data.AgentName}";
                        existing.Content = BuildSubagentPayloadJson(
                            description: existingDescription,
                            agentName: subStart.Data.AgentName,
                            agentDisplayName: displayName,
                            agentDescription: subStart.Data.AgentDescription,
                            mode: existingMode);
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
                        runtime.TotalInputTokens += (long)(d.InputTokens ?? 0);
                        runtime.TotalOutputTokens += (long)(d.OutputTokens ?? 0);
                        // Persist token counts to the Chat model so they survive restarts.
                        chat.TotalInputTokens = runtime.TotalInputTokens;
                        chat.TotalOutputTokens = runtime.TotalOutputTokens;
                        if (_activeSession == session)
                        {
                            TotalInputTokens = runtime.TotalInputTokens;
                            TotalOutputTokens = runtime.TotalOutputTokens;
                            OnPropertyChanged(nameof(CurrentChat));
                        }
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
                            Glyph = skill?.IconGlyph ?? "\u26A1"
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
        _sessionSubs[chat.Id] = new DisposableGroup(sessionSubscription, assistantStream, reasoningStream);
    }

    /// <summary>Cleans up session resources for a chat (e.g., on delete).</summary>
    public void CleanupSession(Guid chatId)
    {
        if (_ctsSources.TryGetValue(chatId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _ctsSources.Remove(chatId);
        }
        if (_sessionSubs.TryGetValue(chatId, out var sub))
        {
            sub.Dispose();
            _sessionSubs.Remove(chatId);
        }
        // Delete the server-side session so they don't accumulate
        if (_sessionCache.TryGetValue(chatId, out var session))
        {
            _ = _copilotService.DeleteSessionAsync(session.SessionId);
            _sessionCache.Remove(chatId);
        }
        _inProgressMessages.Remove(chatId);
        _runtimeStates.Remove(chatId);
        _suggestionGenerationInFlightChats.Remove(chatId);
        _lastSuggestedAssistantMessageByChat.Remove(chatId);

        // Dispose per-chat browser service
        if (_chatBrowserServices.TryGetValue(chatId, out var browserSvc))
        {
            _ = browserSvc.DisposeAsync();
            _chatBrowserServices.Remove(chatId);
        }
    }

    /// <summary>Called when the CopilotService reconnects (new CLI process).
    /// All cached sessions are from the old process and must be discarded.</summary>
    private void OnCopilotReconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Dispose all event subscriptions
            foreach (var sub in _sessionSubs.Values)
                sub.Dispose();
            _sessionSubs.Clear();

            // Clear session cache (objects reference the dead client)
            _sessionCache.Clear();
            _activeSession = null;

            // Reset CopilotSessionId on all chats so sessions are recreated
            foreach (var chat in _dataStore.Data.Chats)
                chat.CopilotSessionId = null;

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
                runtime.IsBusy = false;
                runtime.IsStreaming = false;
                runtime.StatusText = "";
            }

            _inProgressMessages.Clear();

            IsBusy = false;
            IsStreaming = false;
            StatusText = "";
        });
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
