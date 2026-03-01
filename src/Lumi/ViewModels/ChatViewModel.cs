using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot.SDK;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;

using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly BrowserService _browserService;
    private readonly MemoryAgentService _memoryAgentService;
    private readonly CodingToolService _codingToolService;
    private readonly UIAutomationService _uiAutomation = new();
    private readonly object _chatLoadSync = new();
    private CancellationTokenSource? _chatLoadCts;
    private long _chatLoadRequestId;
    /// <summary>Maps chat ID → CancellationTokenSource for per-chat cancellation.</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _ctsSources = new();
    private readonly HashSet<string> _shownFileChips = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SearchSource> _pendingSearchSources = [];
    private readonly List<SkillReference> _pendingFetchedSkillRefs = [];

    /// <summary>The CopilotSession for the currently displayed chat. Events for this session update the UI.</summary>
    private CopilotSession? _activeSession;
    /// <summary>Maps chat ID → CopilotSession. Sessions survive chat switches.</summary>
    private readonly Dictionary<Guid, CopilotSession> _sessionCache = new();
    /// <summary>Maps chat ID → event subscription. Never disposed except on chat delete.</summary>
    private readonly Dictionary<Guid, IDisposable> _sessionSubs = new();
    /// <summary>Maps chat ID → in-progress streaming message not yet committed to Chat.Messages.</summary>
    private readonly Dictionary<Guid, ChatMessage> _inProgressMessages = new();
    /// <summary>Per-chat runtime state sourced from live session events.</summary>
    private readonly Dictionary<Guid, ChatRuntimeState> _runtimeStates = new();
    /// <summary>Chats awaiting title generation after the first assistant turn.</summary>
    private readonly HashSet<Guid> _pendingTitleChats = new();
    /// <summary>Skills activated mid-chat (after session exists). Consumed on next SendMessage to inject into prompt.</summary>
    private readonly List<Guid> _pendingSkillInjections = new();
    /// <summary>Per-chat guard so suggestion generation is queued at most once concurrently.</summary>
    private readonly HashSet<Guid> _suggestionGenerationInFlightChats = new();
    /// <summary>Tracks the last assistant message ID that already produced suggestions per chat.</summary>
    private readonly Dictionary<Guid, Guid> _lastSuggestedAssistantMessageByChat = new();

    private sealed class ChatRuntimeState
    {
        public bool IsBusy { get; set; }
        public bool IsStreaming { get; set; }
        public string StatusText { get; set; } = "";
    }

    /// <summary>True while LoadChat is bulk-adding messages. The View skips CollectionChanged.Add during this.</summary>
    public bool IsLoadingChat { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChatTitle))]
    private Chat? _currentChat;

    /// <summary>Exposes CurrentChat.Title so the header binding updates without toggling CurrentChat.</summary>
    public string? CurrentChatTitle => CurrentChat?.Title;

    [ObservableProperty] private string? _promptText;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private LumiAgent? _activeAgent;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<string> AvailableModels { get; } = [];
    public ObservableCollection<string> PendingAttachments { get; } = [];

    /// <summary>Skills currently active for this chat session — shown as chips in the composer.</summary>
    public ObservableCollection<object> ActiveSkillChips { get; } = [];

    /// <summary>Skill IDs active for the current chat.</summary>
    public List<Guid> ActiveSkillIds { get; } = [];

    /// <summary>MCP servers currently active for this chat session — shown as chips in the composer.</summary>
    public ObservableCollection<object> ActiveMcpChips { get; } = [];

    /// <summary>MCP server names active for the current chat (empty = use all enabled).</summary>
    public List<string> ActiveMcpServerNames { get; } = [];

    [ObservableProperty] private string _suggestionA = "";
    [ObservableProperty] private string _suggestionB = "";
    [ObservableProperty] private string _suggestionC = "";
    [ObservableProperty] private bool _isSuggestionsGenerating;

    // Events for the view to react to
    public event Action? ScrollToEndRequested;
    public event Action? UserMessageSent;
    public event Action? ChatUpdated;
    public event Action<Guid, string>? ChatTitleChanged;
    public event Action<string>? FileCreatedByTool;
    public event Action<List<WebSearchService.SearchResult>>? SearchResultsCollected;
    public event Action? AgentChanged;
    public event Action? BrowserHideRequested;
    /// <summary>Raised when a file-edit tool wants to show a diff in the preview island. Args: filePath, oldText, newText.</summary>
    public event Action<string, string?, string?>? DiffShowRequested;
    /// <summary>Raised to hide the diff preview island.</summary>
    public event Action? DiffHideRequested;
    /// <summary>Fires terminal output updates (rootToolCallId, output, replaceExistingOutput).</summary>
    public event Action<string, string, bool>? TerminalOutputReceived;

    /// <summary>Raised when the LLM calls ask_question. Args: questionId, question, options (comma-separated), allowFreeText.</summary>
    public event Action<string, string, string, bool>? QuestionAsked;

    /// <summary>Pending question completions keyed by question ID.</summary>
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingQuestions = new();

    public ChatViewModel(DataStore dataStore, CopilotService copilotService, BrowserService browserService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _browserService = browserService;
        _memoryAgentService = new MemoryAgentService(dataStore, copilotService);
        _codingToolService = new CodingToolService(copilotService, GetCurrentCancellationToken);
        _selectedModel = dataStore.Data.Settings.PreferredModel;

        // Seed with preferred model so the ComboBox has an initial selection
        if (!string.IsNullOrWhiteSpace(_selectedModel))
            AvailableModels.Add(_selectedModel);

        // Default all enabled MCPs to active so the MCP picker shows them checked
        PopulateDefaultMcps();
    }

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
        var accContent = "";
        var accReasoning = "";
        ChatMessage? streamingMsg = null;
        ChatMessage? reasoningMsg = null;
        var agentName = ActiveAgent?.Name ?? Loc.Author_Lumi;
        var runtime = GetOrCreateRuntimeState(chat.Id);
        var toolParentById = new Dictionary<string, string?>(StringComparer.Ordinal);
        var terminalRootByToolCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        _sessionSubs[chat.Id] = session.On(evt =>
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
                    Dispatcher.UIThread.Post(() =>
                    {
                        accContent += delta.Data.DeltaContent;
                        runtime.StatusText = Loc.Status_Generating;
                        if (streamingMsg is not null)
                        {
                            streamingMsg.Content = accContent;
                            if (_activeSession == session)
                            {
                                var vm = Messages.LastOrDefault(m => m.Message == streamingMsg);
                                vm?.NotifyContentChanged();
                                StatusText = runtime.StatusText;
                                ScrollToEndRequested?.Invoke();
                            }
                        }
                        else
                        {
                            streamingMsg = new ChatMessage
                            {
                                Role = "assistant",
                                Author = agentName,
                                Content = accContent,
                                IsStreaming = true
                            };
                            _inProgressMessages[chat.Id] = streamingMsg;
                            if (_activeSession == session)
                            {
                                Messages.Add(new ChatMessageViewModel(streamingMsg));
                                StatusText = runtime.StatusText;
                                ScrollToEndRequested?.Invoke();
                            }
                        }
                    });
                    break;

                case AssistantMessageEvent msg:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (streamingMsg is not null)
                        {
                            var finalContent = msg.Data.Content;
                            if (string.IsNullOrWhiteSpace(finalContent))
                            {
                                // Empty assistant message (SDK artifact) — discard it so
                                // preceding reasoning/tool blocks merge with the real reply.
                                _inProgressMessages.Remove(chat.Id);
                                if (_activeSession == session)
                                {
                                    var vm = Messages.LastOrDefault(m => m.Message == streamingMsg);
                                    if (vm is not null) Messages.Remove(vm);
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
                                if (_pendingFetchedSkillRefs.Count > 0)
                                {
                                    streamingMsg.ActiveSkills.AddRange(_pendingFetchedSkillRefs);
                                    _pendingFetchedSkillRefs.Clear();
                                }
                                chat.Messages.Add(streamingMsg);
                                _inProgressMessages.Remove(chat.Id);
                                if (_activeSession == session)
                                {
                                    var vm = Messages.LastOrDefault(m => m.Message == streamingMsg);
                                    vm?.NotifyStreamingEnded();
                                }
                            }
                        }
                        streamingMsg = null;
                        accContent = "";
                    });
                    break;

                case AssistantReasoningDeltaEvent rd:
                    Dispatcher.UIThread.Post(() =>
                    {
                        accReasoning += rd.Data.DeltaContent;
                        runtime.StatusText = Loc.Status_Reasoning;
                        if (reasoningMsg is null)
                        {
                            reasoningMsg = new ChatMessage
                            {
                                Role = "reasoning",
                                Author = Loc.Author_Thinking,
                                Content = accReasoning,
                                IsStreaming = true
                            };
                            chat.Messages.Add(reasoningMsg);
                            if (_activeSession == session)
                            {
                                Messages.Add(new ChatMessageViewModel(reasoningMsg));
                                StatusText = runtime.StatusText;
                                ScrollToEndRequested?.Invoke();
                            }
                        }
                        else
                        {
                            reasoningMsg.Content = accReasoning;
                            if (_activeSession == session)
                            {
                                var vm = Messages.LastOrDefault(m => m.Message == reasoningMsg);
                                vm?.NotifyContentChanged();
                                StatusText = runtime.StatusText;
                                ScrollToEndRequested?.Invoke();
                            }
                        }
                    });
                    break;

                case AssistantReasoningEvent r:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (reasoningMsg is not null)
                        {
                            reasoningMsg.Content = r.Data.Content;
                            reasoningMsg.IsStreaming = false;
                            if (_activeSession == session)
                            {
                                var vm = Messages.LastOrDefault(m => m.Message == reasoningMsg);
                                vm?.NotifyStreamingEnded();
                            }
                        }
                        reasoningMsg = null;
                        accReasoning = "";
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
                        else if (IsTerminalStreamingTool(toolStart.Data.ToolName)
                                 && !string.IsNullOrWhiteSpace(toolStart.Data.ParentToolCallId))
                        {
                            terminalRootByToolCallId[startToolCallId] = ResolveRootTerminalToolCallId(
                                toolStart.Data.ParentToolCallId!, toolParentById, terminalRootByToolCallId);
                        }

                        var displayName = FormatToolDisplayName(toolStart.Data.ToolName, toolStart.Data.Arguments?.ToString());
                        runtime.StatusText = string.Format(Loc.Status_Running, displayName);
                        var toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = startToolCallId,
                            ToolName = toolStart.Data.ToolName,
                            ToolStatus = "InProgress",
                            Content = toolStart.Data.Arguments?.ToString() ?? "",
                            Author = displayName
                        };
                        chat.Messages.Add(toolMsg);
                        if (_activeSession == session)
                        {
                            Messages.Add(new ChatMessageViewModel(toolMsg));
                            StatusText = runtime.StatusText;
                            ScrollToEndRequested?.Invoke();
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
                        if (!IsTerminalStreamingTool(partialToolName)
                            && !terminalRootByToolCallId.ContainsKey(partialToolCallId))
                            return;

                        var rootToolCallId = ResolveRootTerminalToolCallId(partialToolCallId, toolParentById, terminalRootByToolCallId);
                        var output = CleanTerminalOutput(partial.Data.PartialOutput);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            ApplyTerminalOutput(chat, rootToolCallId, output, replaceExistingOutput: false);
                            TerminalOutputReceived?.Invoke(rootToolCallId, output, false);
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
                        if (!IsTerminalStreamingTool(progressToolName)
                            && !terminalRootByToolCallId.ContainsKey(progressToolCallId))
                            return;

                        var rootToolCallId = ResolveRootTerminalToolCallId(progressToolCallId, toolParentById, terminalRootByToolCallId);
                        var output = CleanTerminalOutput(progress.Data.ProgressMessage);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            ApplyTerminalOutput(chat, rootToolCallId, output, replaceExistingOutput: false);
                            TerminalOutputReceived?.Invoke(rootToolCallId, output, false);
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
                                        _pendingFetchedSkillRefs.Add(new SkillReference
                                        {
                                            Name = skillName,
                                            Glyph = skill?.IconGlyph ?? "\u26A1"
                                        });
                                    }
                                }

                                if ((IsFileCreationTool(toolName) || toolName == "powershell")
                                    && toolEnd.Data.Result?.Contents is { Length: > 0 } contents)
                                {
                                    foreach (var item in contents)
                                    {
                                        if (item is ToolExecutionCompleteDataResultContentsItemResourceLink rl
                                            && !string.IsNullOrEmpty(rl.Uri))
                                        {
                                            var fp = UriToLocalPath(rl.Uri);
                                            if (fp is not null && File.Exists(fp) && IsUserFacingFile(fp) && _shownFileChips.Add(fp))
                                            {
                                                if (_activeSession == session)
                                                    FileCreatedByTool?.Invoke(fp);
                                            }
                                        }
                                    }
                                }
                            }

                            if (IsTerminalStreamingTool(toolName) && _activeSession == session)
                            {
                                var rootToolCallId = ResolveRootTerminalToolCallId(
                                    toolEnd.Data.ToolCallId, toolParentById, terminalRootByToolCallId);
                                var output = ExtractTerminalOutput(toolEnd.Data.Result);
                                if (!string.IsNullOrWhiteSpace(output))
                                {
                                    ApplyTerminalOutput(chat, rootToolCallId, output, replaceExistingOutput: true);
                                    TerminalOutputReceived?.Invoke(rootToolCallId, output, true);
                                    QueueSaveChat(chat, saveIndex: false);
                                }
                            }
                        }
                    });
                    break;

                case AssistantTurnEndEvent:
                    Dispatcher.UIThread.Post(() =>
                    {
                        runtime.IsBusy = false;
                        runtime.IsStreaming = false;
                        runtime.StatusText = "";
                        if (_activeSession == session)
                        {
                            IsBusy = runtime.IsBusy;
                            IsStreaming = runtime.IsStreaming;
                            StatusText = runtime.StatusText;
                        }
                        QueueSaveChat(chat, saveIndex: true);
                        QueueAutonomousMemoryCheckpoint(chat);
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

                        // Generate follow-up suggestions once the full assistant response is done.
                        if (_activeSession == session && CurrentChat?.Id == chat.Id)
                            QueueSuggestionGenerationForLatestAssistant(chat);
                    });
                    break;

                case SessionTitleChangedEvent title:
                    Dispatcher.UIThread.Post(() =>
                    {
                        var wasFirstTitle = _pendingTitleChats.Remove(chat.Id);
                        if (!_dataStore.Data.Settings.AutoGenerateTitles) return;
                        chat.Title = title.Data.Title;
                        chat.UpdatedAt = DateTimeOffset.Now;
                        if (CurrentChat?.Id == chat.Id)
                            OnPropertyChanged(nameof(CurrentChatTitle));
                        if (_dataStore.Data.Settings.AutoSaveChats)
                            _ = SaveIndexAsync();
                        if (wasFirstTitle)
                            ChatTitleChanged?.Invoke(chat.Id, chat.Title);
                    });
                    break;

                case SessionErrorEvent err:
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Clean up any in-progress streaming message
                        if (streamingMsg is not null)
                        {
                            _inProgressMessages.Remove(chat.Id);
                            if (_activeSession == session)
                            {
                                var vm = Messages.LastOrDefault(m => m.Message == streamingMsg);
                                if (vm is not null) Messages.Remove(vm);
                            }
                            streamingMsg = null;
                            accContent = "";
                        }
                        if (reasoningMsg is not null)
                        {
                            reasoningMsg.IsStreaming = false;
                            if (_activeSession == session)
                            {
                                var vm = Messages.LastOrDefault(m => m.Message == reasoningMsg);
                                vm?.NotifyStreamingEnded();
                            }
                            reasoningMsg = null;
                            accReasoning = "";
                        }

                        runtime.IsBusy = false;
                        runtime.IsStreaming = false;
                        runtime.StatusText = string.Format(Loc.Status_Error, err.Data.Message);
                        if (_activeSession == session)
                        {
                            StatusText = runtime.StatusText;
                            IsBusy = runtime.IsBusy;
                            IsStreaming = runtime.IsStreaming;

                            // Surface the error as a visible chat message
                            var errorMsg = new ChatMessage
                            {
                                Role = "system",
                                Author = Loc.Author_Lumi,
                                Content = string.Format(Loc.Status_Error, err.Data.Message)
                            };
                            chat.Messages.Add(errorMsg);
                            Messages.Add(new ChatMessageViewModel(errorMsg));
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
                            StatusText = runtime.StatusText;
                    });
                    break;

                case AbortEvent abort:
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
                                    var vm = Messages.LastOrDefault(m => m.Message == streamingMsg);
                                    vm?.NotifyStreamingEnded();
                                }
                            }
                            else
                            {
                                _inProgressMessages.Remove(chat.Id);
                                if (_activeSession == session)
                                {
                                    var vm = Messages.LastOrDefault(m => m.Message == streamingMsg);
                                    if (vm is not null) Messages.Remove(vm);
                                }
                            }
                            streamingMsg = null;
                            accContent = "";
                        }
                        if (reasoningMsg is not null)
                        {
                            reasoningMsg.IsStreaming = false;
                            if (_activeSession == session)
                            {
                                var vm = Messages.LastOrDefault(m => m.Message == reasoningMsg);
                                vm?.NotifyStreamingEnded();
                            }
                            reasoningMsg = null;
                            accReasoning = "";
                        }
                        runtime.IsBusy = false;
                        runtime.IsStreaming = false;
                        runtime.StatusText = Loc.Status_Stopped;
                        if (_activeSession == session)
                        {
                            IsBusy = false;
                            IsStreaming = false;
                            StatusText = runtime.StatusText;
                        }
                    });
                    break;

                case SessionShutdownEvent shutdown:
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
                    });
                    break;
            }
        });
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
        _pendingTitleChats.Remove(chatId);
        _suggestionGenerationInFlightChats.Remove(chatId);
        _lastSuggestedAssistantMessageByChat.Remove(chatId);
    }

    private ChatRuntimeState GetOrCreateRuntimeState(Guid chatId)
    {
        if (!_runtimeStates.TryGetValue(chatId, out var runtime))
        {
            runtime = new ChatRuntimeState();
            _runtimeStates[chatId] = runtime;
        }
        return runtime;
    }

    private (long RequestId, CancellationTokenSource Source) BeginChatLoad(CancellationToken outerCancellationToken)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        long requestId;

        lock (_chatLoadSync)
        {
            previous = _chatLoadCts;
            current = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
            _chatLoadCts = current;
            requestId = ++_chatLoadRequestId;
        }

        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        return (requestId, current);
    }

    private bool IsCurrentChatLoad(long requestId, CancellationTokenSource source)
    {
        lock (_chatLoadSync)
            return requestId == _chatLoadRequestId && ReferenceEquals(_chatLoadCts, source);
    }

    /// <summary>Creates or resumes a Copilot session for the given chat, building
    /// system prompt, tools, agents, skill dirs, and MCP servers as needed.</summary>
    private async Task EnsureSessionAsync(Chat chat, CancellationToken ct)
    {
        var allSkills = _dataStore.Data.Skills;
        var activeSkills = ActiveSkillIds.Count > 0
            ? allSkills.Where(s => ActiveSkillIds.Contains(s.Id)).ToList()
            : new List<Skill>();
        var memories = _dataStore.Data.Memories;
        var project = chat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, ActiveAgent, project, allSkills, activeSkills, memories);

        var skillDirs = new List<string>();
        if (ActiveSkillIds.Count > 0)
        {
            var dir = await _dataStore.SyncSkillFilesForIdsAsync(ActiveSkillIds);
            skillDirs.Add(dir);
        }

        var customAgents = BuildCustomAgents();
        var customTools = BuildCustomTools();
        var mcpServers = BuildMcpServers();
        var workDir = GetWorkingDirectory();

        if (chat.CopilotSessionId is null)
        {
            var session = await _copilotService.CreateSessionAsync(
                systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools, mcpServers, ct);
            chat.CopilotSessionId = session.SessionId;
            _activeSession = session;
            SubscribeToSession(session, chat);
        }
        else
        {
            try
            {
                var session = await _copilotService.ResumeSessionAsync(
                    chat.CopilotSessionId, systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools, mcpServers, ct);
                _activeSession = session;
                SubscribeToSession(session, chat);
            }
            catch
            {
                // Session expired or broken — fall back to a fresh session
                StatusText = Loc.Status_SessionExpired;
                var session = await _copilotService.CreateSessionAsync(
                    systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools, mcpServers, ct);
                chat.CopilotSessionId = session.SessionId;
                _activeSession = session;
                SubscribeToSession(session, chat);
                await SaveCurrentChatAsync();
            }
        }
    }

    public async Task LoadChatAsync(Chat chat, CancellationToken cancellationToken = default)
    {
        var (requestId, loadCts) = BeginChatLoad(cancellationToken);
        var loadToken = loadCts.Token;

        if (CurrentChat?.Id == chat.Id && chat.Messages.Count > 0)
        {
            lock (_chatLoadSync)
            {
                if (ReferenceEquals(_chatLoadCts, loadCts))
                {
                    _chatLoadCts = null;
                    IsLoadingChat = false;
                }
            }
            loadCts.Dispose();
            return;
        }

        if (CurrentChat?.Id != chat.Id)
        {
            BrowserHideRequested?.Invoke();
            DiffHideRequested?.Invoke();
            HasUsedBrowser = false;
            ClearSuggestions();
        }

        IsLoadingChat = true;
        try
        {
            // Load messages from per-chat file if not already in memory
            await _dataStore.LoadChatMessagesAsync(chat, loadToken);

            if (loadToken.IsCancellationRequested || !IsCurrentChatLoad(requestId, loadCts))
                return;

            // Set the active session (don't dispose anything — background sessions keep running)
            _sessionCache.TryGetValue(chat.Id, out var cachedSession);
            _activeSession = cachedSession;

            // Clear pending skill injections from any previous chat
            _pendingSkillInjections.Clear();

            // Restore real runtime state for this session/chat
            var runtime = GetOrCreateRuntimeState(chat.Id);
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            Messages.Clear();
            foreach (var msg in chat.Messages)
            {
                // Skip empty assistant messages (SDK artifact)
                if (msg.Role == "assistant" && string.IsNullOrWhiteSpace(msg.Content))
                    continue;
                Messages.Add(new ChatMessageViewModel(msg));
            }

            // If there's an in-progress streaming message not yet committed, show it
            if (_inProgressMessages.TryGetValue(chat.Id, out var inProgress))
                Messages.Add(new ChatMessageViewModel(inProgress));

            CurrentChat = chat;

            // Restore active skills from chat
            ActiveSkillIds.Clear();
            ActiveSkillChips.Clear();
            var skillsById = new Dictionary<Guid, Skill>();
            foreach (var skill in _dataStore.Data.Skills)
            {
                if (!skillsById.ContainsKey(skill.Id))
                    skillsById[skill.Id] = skill;
            }
            foreach (var skillId in chat.ActiveSkillIds)
            {
                if (skillsById.TryGetValue(skillId, out var skill))
                {
                    ActiveSkillIds.Add(skillId);
                    ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
                }
            }

            // Restore active MCP servers from chat (default to all enabled if none saved)
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            var enabledServersByName = new Dictionary<string, McpServer>(StringComparer.Ordinal);
            foreach (var server in _dataStore.Data.McpServers)
            {
                if (server.IsEnabled && !enabledServersByName.ContainsKey(server.Name))
                    enabledServersByName[server.Name] = server;
            }

            if (chat.ActiveMcpServerNames.Count > 0)
            {
                foreach (var name in chat.ActiveMcpServerNames)
                {
                    if (enabledServersByName.ContainsKey(name))
                    {
                        ActiveMcpServerNames.Add(name);
                        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name));
                    }
                }
            }
            else
            {
                // No MCPs saved — default to all enabled
                foreach (var server in enabledServersByName.Values)
                {
                    ActiveMcpServerNames.Add(server.Name);
                    ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(server.Name));
                }
            }

            // Restore active agent from chat
            ActiveAgent = chat.AgentId.HasValue
                ? _dataStore.Data.Agents.FirstOrDefault(a => a.Id == chat.AgentId.Value)
                : null;
        }
        catch (OperationCanceledException) when (loadToken.IsCancellationRequested)
        {
            // A newer chat selection superseded this load.
        }
        finally
        {
            lock (_chatLoadSync)
            {
                if (ReferenceEquals(_chatLoadCts, loadCts))
                {
                    _chatLoadCts = null;
                    IsLoadingChat = false;
                }
            }
            loadCts.Dispose();
        }
    }

    public void ClearChat()
    {
        lock (_chatLoadSync)
        {
            _chatLoadRequestId++;
            try { _chatLoadCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        BrowserHideRequested?.Invoke();
        DiffHideRequested?.Invoke();
        HasUsedBrowser = false;

        // Detach from current chat without destroying its session.
        // Sessions are cleaned only when a chat is deleted via CleanupSession(chatId).
        _activeSession = null;

        Messages.Clear();
        CurrentChat = null;
        IsBusy = false;
        IsStreaming = false;
        _shownFileChips.Clear();
        _pendingSearchSources.Clear();
        _pendingFetchedSkillRefs.Clear();
        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        ActiveMcpServerNames.Clear();
        ActiveMcpChips.Clear();
        PopulateDefaultMcps();
        _pendingProjectId = null;
        _pendingSkillInjections.Clear();
        StatusText = "";
    }

    /// <summary>
    /// Called when MCP server config changes so the next message creates a fresh session with updated MCP servers.
    /// </summary>
    public void InvalidateMcpSession()
    {
        if (CurrentChat is not null)
        {
            // Remove from session cache so LoadChatAsync doesn't restore the stale session
            _sessionCache.Remove(CurrentChat.Id);
            CurrentChat.CopilotSessionId = null;
            _activeSession = null;
            // New session will include all active skills in the system prompt,
            // so clear pending injections to avoid duplication
            _pendingSkillInjections.Clear();
        }
    }

    [RelayCommand]
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(PromptText))
            return;

        if (!_copilotService.IsConnected)
        {
            StatusText = Loc.Status_NotConnected;
            try { await _copilotService.ConnectAsync(); }
            catch { StatusText = Loc.Status_CheckAccess; return; }
        }

        var prompt = PromptText!.Trim();
        PromptText = "";
        ClearSuggestions();

        var attachments = TakePendingAttachments();

        // Create chat if needed
        if (CurrentChat is null)
        {
            var chat = new Chat
            {
                Title = prompt.Length > 40 ? prompt[..40].Trim() + "…" : prompt,
                AgentId = ActiveAgent?.Id,
                ProjectId = _pendingProjectId,
                ActiveSkillIds = new List<Guid>(ActiveSkillIds)
            };
            _pendingProjectId = null;
            _dataStore.Data.Chats.Add(chat);
            CurrentChat = chat;
            if (_dataStore.Data.Settings.AutoGenerateTitles)
            {
                _pendingTitleChats.Add(chat.Id);
                _ = GenerateChatTitleAsync(chat, prompt);
            }
            await SaveCurrentChatAsync();
            ChatUpdated?.Invoke();
        }

        // Add user message
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = _dataStore.Data.Settings.UserName ?? Loc.Author_You,
            Attachments = attachments?.Select(a => a.Path).ToList() ?? [],
            ActiveSkills = ActiveSkillIds
                .Select(id => _dataStore.Data.Skills.FirstOrDefault(s => s.Id == id))
                .Where(s => s is not null)
                .Select(s => new SkillReference { Name = s!.Name, Glyph = s.IconGlyph })
                .ToList()
        };
        CurrentChat.Messages.Add(userMsg);
        Messages.Add(new ChatMessageViewModel(userMsg));
        await SaveCurrentChatAsync();
        UserMessageSent?.Invoke();

        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = CurrentChat.Id;
            if (_ctsSources.TryGetValue(chatId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            var cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

            var needsSessionSetup = _activeSession?.SessionId != CurrentChat.CopilotSessionId
                                    || CurrentChat.CopilotSessionId is null;

            if (needsSessionSetup)
                await EnsureSessionAsync(CurrentChat, cts.Token);

            var sendOptions = new MessageOptions { Prompt = prompt };

            // Inject newly activated skills as context in the message (explicit activation in existing chat)
            if (_pendingSkillInjections.Count > 0)
            {
                var allSkills = _dataStore.Data.Skills;
                var injectedSkills = _pendingSkillInjections
                    .Select(id => allSkills.FirstOrDefault(s => s.Id == id))
                    .Where(s => s is not null)
                    .ToList();
                _pendingSkillInjections.Clear();

                if (injectedSkills.Count > 0)
                {
                    var skillContext = "\n\n--- Activated Skills (apply these to help with the request) ---\n";
                    foreach (var skill in injectedSkills)
                        skillContext += $"\n### {skill!.Name}\n{skill.Content}\n";
                    sendOptions.Prompt = prompt + skillContext;
                }
            }

            if (attachments is { Count: > 0 })
                sendOptions.Attachments = attachments.Cast<UserMessageDataAttachmentsItem>().ToList();

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            await _activeSession!.SendAsync(sendOptions, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected, no error to surface
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Loc.Status_Error, ex.Message);
            IsBusy = false;
            IsStreaming = false;
            if (CurrentChat is not null)
            {
                var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
                runtime.IsBusy = false;
                runtime.IsStreaming = false;
                runtime.StatusText = StatusText;
            }
        }
    }

    [RelayCommand]
    private async Task StopGeneration()
    {
        if (CurrentChat is null) return;

        var chatId = CurrentChat.Id;
        if (_ctsSources.TryGetValue(chatId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _ctsSources.Remove(chatId);
        }

        // Get the session for this specific chat (not _activeSession which may differ)
        if (_sessionCache.TryGetValue(chatId, out var session))
        {
            try { await session.AbortAsync(); }
            catch { /* Best-effort abort */ }
        }

        var runtime = GetOrCreateRuntimeState(chatId);
        runtime.IsBusy = false;
        runtime.IsStreaming = false;
        runtime.StatusText = Loc.Status_Stopped;

        // Only update UI properties if this is still the displayed chat
        if (CurrentChat?.Id == chatId)
        {
            IsBusy = false;
            IsStreaming = false;
            StatusText = Loc.Status_Stopped;
        }
    }

    private async Task SaveCurrentChatAsync(bool saveIndex = true)
    {
        if (CurrentChat is null) return;
        await SaveChatAsync(CurrentChat, saveIndex);
    }

    private void QueueSaveChat(Chat chat, bool saveIndex)
    {
        _ = SaveChatAsync(chat, saveIndex);
    }

    private void QueueAutonomousMemoryCheckpoint(Chat chat)
    {
        if (!_dataStore.Data.Settings.EnableMemoryAutoSave)
            return;

        var checkpoint = CreateMemoryCheckpoint(chat);
        if (checkpoint is null)
            return;

        _ = _memoryAgentService.ProcessCheckpointAsync(checkpoint);
    }

    private MemoryAgentCheckpoint? CreateMemoryCheckpoint(Chat chat)
    {
        var assistantIndex = -1;
        for (var i = chat.Messages.Count - 1; i >= 0; i--)
        {
            var message = chat.Messages[i];
            if (message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content))
            {
                assistantIndex = i;
                break;
            }
        }

        if (assistantIndex <= 0)
            return null;

        var userIndex = -1;
        for (var i = assistantIndex - 1; i >= 0; i--)
        {
            var message = chat.Messages[i];
            if (message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
            {
                userIndex = i;
                break;
            }
        }

        if (userIndex < 0)
            return null;

        var userMessage = chat.Messages[userIndex];
        var assistantMessage = chat.Messages[assistantIndex];
        var recentConversation = chat.Messages
            .Where(m => (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(8)
            .Select(m => new MemoryAgentConversationItem
            {
                Role = m.Role,
                Content = m.Content
            })
            .ToList();

        var memories = _dataStore.Data.Memories
            .Select(m => new MemoryAgentSnapshot
            {
                Key = m.Key,
                Content = m.Content,
                Category = m.Category
            })
            .ToList();

        return new MemoryAgentCheckpoint
        {
            ChatId = chat.Id,
            InteractionSignature = $"{userMessage.Id:N}:{assistantMessage.Id:N}",
            UserMessage = userMessage.Content,
            AssistantMessage = assistantMessage.Content,
            UserName = _dataStore.Data.Settings.UserName,
            ExistingMemories = memories,
            RecentConversation = recentConversation
        };
    }

    private async Task GenerateChatTitleAsync(Chat chat, string userMessage)
    {
        try
        {
            var title = await _copilotService.GenerateTitleAsync(userMessage);
            if (string.IsNullOrWhiteSpace(title)) { _pendingTitleChats.Remove(chat.Id); return; }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _pendingTitleChats.Remove(chat.Id);
                chat.Title = title.Trim();
                chat.UpdatedAt = DateTimeOffset.Now;
                if (CurrentChat?.Id == chat.Id)
                    OnPropertyChanged(nameof(CurrentChatTitle));
                await SaveIndexAsync();
                ChatTitleChanged?.Invoke(chat.Id, chat.Title);
            });
        }
        catch
        {
            // Silently fail — the default truncated title is already set
            _pendingTitleChats.Remove(chat.Id);
        }
    }

    private void QueueSuggestionGenerationForLatestAssistant(Chat chat)
    {
        if (_suggestionGenerationInFlightChats.Contains(chat.Id))
            return;

        var lastAssistant = chat.Messages.LastOrDefault(m =>
            m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        if (lastAssistant is null)
            return;

        if (_lastSuggestedAssistantMessageByChat.TryGetValue(chat.Id, out var lastSuggestedId)
            && lastSuggestedId == lastAssistant.Id)
            return;

        _suggestionGenerationInFlightChats.Add(chat.Id);
        _ = GenerateSuggestionsAsync(chat, lastAssistant.Id);
    }

    private async Task GenerateSuggestionsAsync(Chat chat, Guid assistantMessageId)
    {
        var hasTargetAssistant = false;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CurrentChat?.Id == chat.Id)
                    IsSuggestionsGenerating = true;
            });

            // Resolve the specific assistant message that completed on idle.
            var assistantIndex = chat.Messages.FindIndex(m => m.Id == assistantMessageId);
            if (assistantIndex < 0)
                return;

            var assistantMessage = chat.Messages[assistantIndex];
            if (assistantMessage.Role != "assistant" || string.IsNullOrWhiteSpace(assistantMessage.Content))
                return;

            hasTargetAssistant = true;

            // Use the user message that led to this assistant reply for tighter context.
            var lastUser = chat.Messages
                .Take(assistantIndex)
                .LastOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));

            var suggestions = await _copilotService.GenerateSuggestionsAsync(
                assistantMessage.Content, lastUser?.Content);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CurrentChat?.Id != chat.Id) return;

                // If another assistant message arrived, don't overwrite with stale suggestions.
                var latestAssistantId = chat.Messages
                    .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))?.Id;
                if (latestAssistantId != assistantMessageId)
                    return;

                SuggestionA = suggestions?.ElementAtOrDefault(0) ?? "";
                SuggestionB = suggestions?.ElementAtOrDefault(1) ?? "";
                SuggestionC = suggestions?.ElementAtOrDefault(2) ?? "";
            });
        }
        catch
        {
            // Silently fail — suggestions are non-critical
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (CurrentChat?.Id == chat.Id)
                    IsSuggestionsGenerating = false;

                _suggestionGenerationInFlightChats.Remove(chat.Id);
                if (hasTargetAssistant)
                    _lastSuggestedAssistantMessageByChat[chat.Id] = assistantMessageId;
            });
        }
    }

    private void ClearSuggestions()
    {
        SuggestionA = "";
        SuggestionB = "";
        SuggestionC = "";
        IsSuggestionsGenerating = false;
    }

    private async Task SaveChatAsync(Chat chat, bool saveIndex)
    {
        chat.UpdatedAt = DateTimeOffset.Now;
        if (!_dataStore.Data.Settings.AutoSaveChats) return;

        try
        {
            await _dataStore.SaveChatAsync(chat);
            if (saveIndex)
                await _dataStore.SaveAsync();
        }
        catch
        {
            // Avoid surfacing persistence races/IO failures as hard UI errors.
        }
    }

    private async Task SaveIndexAsync()
    {
        try
        {
            await _dataStore.SaveAsync();
        }
        catch
        {
            // Best-effort persistence for UX responsiveness.
        }
    }

    /// <summary>Whether the agent can still be changed (only before the first message is sent).</summary>
    public bool CanChangeAgent => CurrentChat is null || CurrentChat.Messages.Count == 0;

    public void SetActiveAgent(LumiAgent? agent)
    {
        // Don't allow switching agents once a chat has messages
        if (!CanChangeAgent) return;

        ActiveAgent = agent;
        if (CurrentChat is not null)
        {
            CurrentChat.AgentId = agent?.Id;
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
        AgentChanged?.Invoke();
    }

    /// <summary>Assigns a project to the current (or next) chat. Called when a project filter is active.</summary>
    public void SetProjectId(Guid projectId)
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ProjectId = projectId;
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
        else
        {
            // Will be applied when the chat is created in SendMessage
            _pendingProjectId = projectId;
        }
    }

    private Guid? _pendingProjectId;

    public void AddSkill(Skill skill)
    {
        if (ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
        // If added to an existing chat with a session, inject via next message instead of system prompt
        if (CurrentChat?.CopilotSessionId is not null)
            _pendingSkillInjections.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    /// <summary>Registers a skill ID without adding a chip (composer already added it).</summary>
    public void RegisterSkillIdByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is null || ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        // If added to an existing chat with a session, inject via next message
        if (CurrentChat?.CopilotSessionId is not null)
            _pendingSkillInjections.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    private void SyncActiveSkillsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveSkillIds = new List<Guid>(ActiveSkillIds);
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    public void RemoveSkillByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is null) return;
        ActiveSkillIds.Remove(skill.Id);
        var chip = ActiveSkillChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name == name);
        if (chip is not null) ActiveSkillChips.Remove(chip);
        SyncActiveSkillsToChat();
    }

    public void AddMcpServer(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        var server = _dataStore.Data.McpServers.FirstOrDefault(s => s.Name == name);
        if (server is null) return;
        ActiveMcpServerNames.Add(name);
        ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name));
        SyncActiveMcpsToChat();
    }

    /// <summary>Registers an MCP server name without adding a chip (composer already added it).</summary>
    public void RegisterMcpByName(string name)
    {
        if (ActiveMcpServerNames.Contains(name)) return;
        var server = _dataStore.Data.McpServers.FirstOrDefault(s => s.Name == name);
        if (server is null) return;
        ActiveMcpServerNames.Add(name);
        SyncActiveMcpsToChat();
    }

    public void RemoveMcpByName(string name)
    {
        ActiveMcpServerNames.Remove(name);
        var chip = ActiveMcpChips.OfType<StrataTheme.Controls.StrataComposerChip>()
            .FirstOrDefault(c => c.Name == name);
        if (chip is not null) ActiveMcpChips.Remove(chip);
        SyncActiveMcpsToChat();
    }

    public void SyncActiveMcpsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveMcpServerNames = new List<string>(ActiveMcpServerNames);
            QueueSaveChat(CurrentChat, saveIndex: true);
        }
    }

    /// <summary>Populate ActiveMcpChips and ActiveMcpServerNames with all enabled MCP servers (default state).</summary>
    public void PopulateDefaultMcps()
    {
        IsLoadingChat = true;
        try
        {
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            foreach (var server in _dataStore.Data.McpServers.Where(s => s.IsEnabled))
            {
                ActiveMcpServerNames.Add(server.Name);
                ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(server.Name));
            }
        }
        finally
        {
            IsLoadingChat = false;
        }
    }

    private List<CustomAgentConfig> BuildCustomAgents()
    {
        var agents = new List<CustomAgentConfig>();
        foreach (var agent in _dataStore.Data.Agents)
        {
            // Skip the currently active agent (already in main system prompt)
            if (ActiveAgent?.Id == agent.Id) continue;

            var agentConfig = new CustomAgentConfig
            {
                Name = agent.Name,
                DisplayName = agent.Name,
                Description = agent.Description,
                Prompt = agent.SystemPrompt,
            };

            if (agent.ToolNames.Count > 0)
                agentConfig.Tools = agent.ToolNames;

            agents.Add(agentConfig);
        }
        return agents;
    }

    private static readonly HashSet<string> CodingToolNames = ["code_review", "generate_tests", "explain_code", "analyze_project"];
    private static readonly HashSet<string> BrowserToolNames = ["browser", "browser_look", "browser_find", "browser_do", "browser_js"];
    private static readonly HashSet<string> UIToolNames = ["ui_list_windows", "ui_inspect", "ui_find", "ui_click", "ui_type", "ui_press_keys", "ui_read"];

    private CancellationToken GetCurrentCancellationToken()
    {
        if (CurrentChat is { } chat && _ctsSources.TryGetValue(chat.Id, out var cts))
            return cts.Token;
        return CancellationToken.None;
    }

    private bool ActiveAgentAllows(HashSet<string> toolGroup)
    {
        // No active agent or no restrictions → allow everything
        if (ActiveAgent is not { ToolNames.Count: > 0 } agent) return true;
        return agent.ToolNames.Exists(toolGroup.Contains);
    }

    private List<AIFunction> BuildCustomTools()
    {
        var tools = new List<AIFunction>();
        tools.AddRange(BuildMemoryTools());
        tools.Add(BuildAnnounceFileTool());
        tools.Add(BuildFetchSkillTool());
        tools.Add(BuildAskQuestionTool());
        tools.AddRange(BuildWebTools());
        if (ActiveAgentAllows(BrowserToolNames))
            tools.AddRange(BuildBrowserTools());
        if (ActiveAgentAllows(CodingToolNames))
            tools.AddRange(_codingToolService.BuildCodingTools());
        if (OperatingSystem.IsWindows() && ActiveAgentAllows(UIToolNames))
            tools.AddRange(BuildUIAutomationTools());
        return tools;
    }

    private Dictionary<string, object>? BuildMcpServers()
    {
        var allServers = _dataStore.Data.McpServers.Where(s => s.IsEnabled).ToList();

        // If an active agent has MCP server IDs, use only those
        if (ActiveAgent is { McpServerIds.Count: > 0 })
        {
            var agentServerIds = ActiveAgent.McpServerIds;
            allServers = allServers.Where(s => agentServerIds.Contains(s.Id)).ToList();
        }
        // If the user has selected specific MCP servers via chips, use only those
        else if (ActiveMcpServerNames.Count > 0)
        {
            allServers = allServers.Where(s => ActiveMcpServerNames.Contains(s.Name)).ToList();
        }

        if (allServers.Count == 0) return null;

        var dict = new Dictionary<string, object>();
        foreach (var server in allServers)
        {
            if (server.ServerType == "remote")
            {
                var remote = new McpRemoteServerConfig
                {
                    Url = server.Url,
                    Type = "sse",
                    Tools = server.Tools.Count > 0 ? server.Tools.ToList() : ["*"]
                };
                if (server.Headers.Count > 0)
                    remote.Headers = new Dictionary<string, string>(server.Headers);
                if (server.Timeout.HasValue)
                    remote.Timeout = server.Timeout.Value;
                dict[server.Name] = remote;
            }
            else
            {
                var local = new McpLocalServerConfig
                {
                    Command = server.Command,
                    Args = server.Args.ToList(),
                    Type = "stdio",
                    Tools = server.Tools.Count > 0 ? server.Tools.ToList() : ["*"]
                };
                if (server.Env.Count > 0)
                    local.Env = new Dictionary<string, string>(server.Env);
                if (server.Timeout.HasValue)
                    local.Timeout = server.Timeout.Value;
                dict[server.Name] = local;
            }
        }
        return dict;
    }

    private List<AIFunction> BuildWebTools()
    {
        return
        [
            AIFunctionFactory.Create(
                async ([Description("The search query to look up on the web")] string query,
                 [Description("Number of results to return (default 5, max 10)")] int count = 5) =>
                {
                    count = Math.Clamp(count, 1, 10);
                    var (text, results) = await WebSearchService.SearchWithResultsAsync(query, count);
                    if (results.Count > 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            foreach (var r in results)
                                _pendingSearchSources.Add(new SearchSource { Title = r.Title, Snippet = r.Snippet, Url = r.Url });
                            SearchResultsCollected?.Invoke(results);
                        });
                    }
                    return text;
                },
                "lumi_search",
                "Search the web for information. Returns titles, snippets, and URLs from search results. Use this to find current information, answer factual questions, research topics, find product reviews, or discover relevant web pages to fetch."),

            AIFunctionFactory.Create(
                ([Description("The full URL to fetch (must start with http:// or https://)")] string url) =>
                {
                    return WebFetchService.FetchAsync(url);
                },
                "lumi_fetch",
                "Fetch a webpage and return its text content. If this fails, do NOT retry the same URL — try a different source instead. After 2 consecutive failures, stop and answer with what you have."),
        ];
    }

    private List<AIFunction> BuildBrowserTools()
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("The full URL to navigate to (e.g. https://mail.google.com)")] string url) =>
                {
                    Dispatcher.UIThread.Post(() => { HasUsedBrowser = true; BrowserShowRequested?.Invoke(); });
                    return _browserService.OpenAndSnapshotAsync(url);
                },
                "browser",
                "Open a URL in the browser and return the page with numbered interactive elements and a text preview. The browser has persistent cookies/sessions — the user may already be logged in. Returns element numbers you can use with browser_do. If the URL triggers a file download (e.g. an export URL), the download is detected automatically and reported instead of a page snapshot."),

            AIFunctionFactory.Create(
                ([Description("Optional text filter to narrow elements (e.g. 'button', 'download', 'search', 'Export'). Omit to see all.")] string? filter = null) =>
                {
                    return _browserService.LookAsync(filter);
                },
                "browser_look",
                "Returns the current page state: numbered interactive elements and text preview. Use filter to narrow results."),

            AIFunctionFactory.Create(
                ([Description("What to find on the page (e.g. 'download', 'export csv', 'save', 'submit').")]
                    string query,
                 [Description("Maximum matches to return (1-50).")]
                    int limit = 12) =>
                {
                    return _browserService.FindElementsAsync(query, limit, preferDialog: true);
                },
                "browser_find",
                "Find and rank interactive elements by query. Matches against text, aria-label, tooltip, title, and href. Returns stable element indices usable with browser_do."),

            AIFunctionFactory.Create(
                ([Description("Action to perform: click, type, press, select, scroll, back, wait, download")] string action,
                 [Description("Target: element number from browser/browser_look (e.g. '3'), button text (e.g. 'Export'), CSS selector (e.g. '.btn'), key name (for press), direction (for scroll), or file pattern (for download)")] string? target = null,
                 [Description("Value: text to type (for type action), option text (for select), pixels (for scroll)")] string? value = null) =>
                {
                    var act = (action ?? "").Trim().ToLowerInvariant();
                    if (act is "click" or "type" or "press" or "select" or "download" or "back")
                        Dispatcher.UIThread.Post(() => { HasUsedBrowser = true; BrowserShowRequested?.Invoke(); });
                    return _browserService.DoAsync(action ?? "", target, value);
                },
                "browser_do",
                "Interact with the page. Actions: click (target: element #, text, or CSS selector), type (target: element # or selector, value: text), press (target: key name), select (target: element # or selector, value: option text), scroll (target: up/down), back, wait (target: CSS selector), download (target: file glob pattern — checks for a pending download, does NOT trigger one)."),

            AIFunctionFactory.Create(
                ([Description("JavaScript code to execute in the page context")] string script) =>
                {
                    return _browserService.EvaluateAsync(script);
                },
                "browser_js",
                "Run JavaScript in the browser page context."),
        ];
    }

    /// <summary>Raised when a browser tool requests the browser panel to be visible.</summary>
    public event Action? BrowserShowRequested;

    /// <summary>True if browser tools have been used in the current session.</summary>
    [ObservableProperty] bool _hasUsedBrowser;

    /// <summary>True when the browser panel is currently visible.</summary>
    [ObservableProperty] bool _isBrowserOpen;

    /// <summary>Allows the view to request the browser panel to be shown.</summary>
    public void RequestShowBrowser() => BrowserShowRequested?.Invoke();

    /// <summary>Toggles the browser panel visibility.</summary>
    public void ToggleBrowser()
    {
        if (IsBrowserOpen)
            BrowserHideRequested?.Invoke();
        else
            BrowserShowRequested?.Invoke();
    }

    /// <summary>True when the diff preview panel is currently visible.</summary>
    [ObservableProperty] bool _isDiffOpen;

    /// <summary>Shows a file diff in the preview island.</summary>
    public void ShowDiff(string filePath, string? oldText, string? newText)
        => DiffShowRequested?.Invoke(filePath, oldText, newText);

    /// <summary>Hides the diff preview island.</summary>
    public void HideDiff() => DiffHideRequested?.Invoke();

    partial void OnSelectedModelChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _dataStore.Data.Settings.PreferredModel = value;
        _ = SaveIndexAsync();
    }

    private List<AIFunction> BuildUIAutomationTools()
    {
        return
        [
            AIFunctionFactory.Create(
                () => _uiAutomation.ListWindows(),
                "ui_list_windows",
                "List all visible windows on the user's desktop. Returns window titles, process names, and PIDs. Call this first to find which window to target."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match) to inspect. The window will be auto-focused.")] string title,
                 [Description("How deep to walk the UI tree (1-5, default 3). Use 2 for overview, 3-4 for detail.")] int depth = 3) =>
                {
                    depth = Math.Clamp(depth, 1, 5);
                    return _uiAutomation.InspectWindow(title, depth);
                },
                "ui_inspect",
                "Inspect the UI element tree of a window (auto-focuses it). Returns numbered elements tagged with [clickable], [editable], [toggleable] etc. Use element numbers with ui_click, ui_type, ui_press_keys, and ui_read. Prefer this over ui_find for first contact with a window."),

            AIFunctionFactory.Create(
                ([Description("Window title (partial match) to search in")] string title,
                 [Description("Search query — matches against element name, automation ID, control type, class name, and help text")] string query) =>
                    _uiAutomation.FindElements(title, query),
                "ui_find",
                "Find UI elements in a window matching a search query. Returns numbered elements you can interact with. Use when you know what you're looking for (e.g. 'Save', 'OK', 'Edit') instead of browsing the whole tree."),

            AIFunctionFactory.Create(
                ([Description("Element number from ui_inspect or ui_find")] int elementId) =>
                    _uiAutomation.ClickElement(elementId),
                "ui_click",
                "Click a UI element by its number. Uses the best interaction pattern: Invoke for buttons, Toggle for checkboxes, Select for list items/tabs, Expand for combo boxes, or mouse click as fallback. After clicking, the UI may change — re-run ui_inspect to get fresh element numbers if needed."),

            AIFunctionFactory.Create(
                ([Description("Element number from ui_inspect or ui_find")] int elementId,
                 [Description("Text to type or set in the element")] string text) =>
                    _uiAutomation.TypeText(elementId, text),
                "ui_type",
                "Type or set text in a UI element by its number. Uses the Value pattern for text fields, or falls back to keyboard input."),

            AIFunctionFactory.Create(
                ([Description("Key combination to send, e.g. 'Ctrl+N', 'Ctrl+S', 'Alt+F4', 'Enter', 'Tab', 'Ctrl+Shift+T'. Single keys: A-Z, 0-9, F1-F12, Enter, Tab, Escape, Delete, Home, End, PageUp, PageDown, Up, Down, Left, Right, Space.")] string keys,
                 [Description("Optional: element number to focus before sending keys. If omitted, keys go to the currently focused window.")] int? elementId = null) =>
                    _uiAutomation.SendKeys(keys, elementId),
                "ui_press_keys",
                "Send keyboard shortcuts or key presses to the focused window. Use for shortcuts like Ctrl+N (new), Ctrl+S (save), Ctrl+Z (undo), Alt+F4 (close), Tab/Enter (navigate forms), arrow keys, etc. Optionally target a specific element by number."),

            AIFunctionFactory.Create(
                ([Description("Element number from ui_inspect or ui_find")] int elementId) =>
                    _uiAutomation.ReadElement(elementId),
                "ui_read",
                "Read detailed information about a UI element: type, name, value, toggle state, selection state, supported interactions, bounds, and more."),
        ];
    }

    private AIFunction BuildAnnounceFileTool()
    {
        return AIFunctionFactory.Create(
            ([Description("Absolute path of the file that was created, converted, or produced for the user")] string filePath) =>
            {
                if (File.Exists(filePath) && IsUserFacingFile(filePath))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_shownFileChips.Add(filePath))
                            FileCreatedByTool?.Invoke(filePath);
                    });
                }
                return $"File announced: {filePath}";
            },
            "announce_file",
            "Show a file attachment chip to the user for a file you created or produced. Call this ONCE for each final deliverable file (e.g. the PDF, DOCX, PPTX, image, etc.). Do NOT call for intermediate/temporary files like scripts.");
    }

    private AIFunction BuildFetchSkillTool()
    {
        return AIFunctionFactory.Create(
            ([Description("The exact name of the skill to retrieve (as listed in Available Skills)")] string name) =>
            {
                var skill = _dataStore.Data.Skills
                    .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (skill is null)
                    return $"Skill not found: {name}. Check the Available Skills list for exact names.";
                return $"# {skill.Name}\n\n{skill.Content}";
            },
            "fetch_skill",
            "Retrieve the full content of a skill by name. Use this when the user asks to use a skill, or when their request closely matches a skill's description. The skill content contains detailed instructions on how to perform the task.");
    }

    private AIFunction BuildAskQuestionTool()
    {
        return AIFunctionFactory.Create(
            async ([Description("The question to ask the user")] string question,
             [Description("Comma-separated list of option labels for the user to choose from")] string options,
             [Description("Whether to allow the user to type a free-text answer in addition to the options. Default: true")] bool? allowFreeText) =>
            {
                var freeText = allowFreeText ?? true;
                var questionId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingQuestions[questionId] = tcs;

                Dispatcher.UIThread.Post(() =>
                {
                    QuestionAsked?.Invoke(questionId, question, options, freeText);
                    ScrollToEndRequested?.Invoke();
                });

                var answer = await tcs.Task;
                _pendingQuestions.Remove(questionId);

                // Persist the answer on the tool message so it survives reload
                var resultText = $"User answered: {answer}";
                Dispatcher.UIThread.Post(() =>
                {
                    var chat = CurrentChat;
                    if (chat is not null)
                    {
                        var toolMsg = chat.Messages.LastOrDefault(m =>
                            m.ToolName == "ask_question" && m.ToolStatus == "InProgress");
                        if (toolMsg is not null)
                            toolMsg.ToolOutput = resultText;
                    }
                });

                return resultText;
            },
            "ask_question",
            "Ask the user a question with predefined options to choose from. Use this when you need the user to pick from a set of choices (e.g. selecting a template, confirming a direction, choosing between alternatives). The answer will be returned as text. Only use this for genuinely useful choices — don't ask unnecessary questions.");
    }

    /// <summary>Called by the View when the user selects an answer on a question card.</summary>
    public void SubmitQuestionAnswer(string questionId, string answer)
    {
        if (_pendingQuestions.TryGetValue(questionId, out var tcs))
            tcs.TrySetResult(answer);
    }

    private List<AIFunction> BuildMemoryTools()
    {
        return _memoryAgentService.BuildRecallMemoryTools();
    }

    /// <summary>Returns StrataComposerChip items for all agents (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetAgentChips()
    {
        return _dataStore.Data.Agents
            .Select(a => new StrataTheme.Controls.StrataComposerChip(a.Name, a.IconGlyph))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all skills (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetSkillChips()
    {
        return _dataStore.Data.Skills
            .Select(s => new StrataTheme.Controls.StrataComposerChip(s.Name, s.IconGlyph))
            .ToList();
    }

    /// <summary>Returns StrataComposerChip items for all enabled MCP servers (for composer autocomplete).</summary>
    public List<StrataTheme.Controls.StrataComposerChip> GetMcpChips()
    {
        return _dataStore.Data.McpServers
            .Where(s => s.IsEnabled)
            .Select(s => new StrataTheme.Controls.StrataComposerChip(s.Name))
            .ToList();
    }

    /// <summary>Selects an agent by name (called from composer autocomplete).</summary>
    public void SelectAgentByName(string name)
    {
        var agent = _dataStore.Data.Agents.FirstOrDefault(a => a.Name == name);
        SetActiveAgent(agent);
    }

    /// <summary>Adds a skill by name (called from composer autocomplete).</summary>
    public void AddSkillByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is not null) AddSkill(skill);
    }

    /// <summary>Finds a skill by name for display purposes (e.g. fetching icon glyph).</summary>
    public Skill? FindSkillByName(string name)
    {
        return _dataStore.Data.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void AddAttachment(string filePath)
    {
        if (!PendingAttachments.Contains(filePath))
            PendingAttachments.Add(filePath);
    }

    public void RemoveAttachment(string filePath)
    {
        PendingAttachments.Remove(filePath);
    }

    private List<UserMessageDataAttachmentsItemFile>? TakePendingAttachments()
    {
        if (PendingAttachments.Count == 0) return null;
        var items = PendingAttachments.Select(fp => new UserMessageDataAttachmentsItemFile
        {
            Path = fp,
            DisplayName = Path.GetFileName(fp)
        }).ToList();
        PendingAttachments.Clear();
        return items;
    }

    public static bool IsFileCreationTool(string? toolName)
    {
        return toolName is "write_file" or "create_file" or "create" or "edit_file"
            or "str_replace_editor" or "str_replace" or "create_and_write_file"
            or "replace_string_in_file" or "multi_replace_string_in_file"
            or "insert" or "write" or "save_file";
    }

    private static bool IsTerminalStreamingTool(string? toolName)
    {
        return toolName is "powershell" or "read_powershell" or "write_powershell" or "stop_powershell";
    }

    private static string ResolveRootTerminalToolCallId(
        string toolCallId,
        Dictionary<string, string?> toolParentById,
        Dictionary<string, string> terminalRootByToolCallId)
    {
        if (terminalRootByToolCallId.TryGetValue(toolCallId, out var knownRoot))
            return knownRoot;

        var current = toolCallId;
        for (var depth = 0; depth < 24; depth++)
        {
            if (terminalRootByToolCallId.TryGetValue(current, out var mappedRoot))
            {
                terminalRootByToolCallId[toolCallId] = mappedRoot;
                return mappedRoot;
            }

            if (!toolParentById.TryGetValue(current, out var parent) || string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        terminalRootByToolCallId[toolCallId] = current;
        return current;
    }

    private static void ApplyTerminalOutput(Chat chat, string rootToolCallId, string output, bool replaceExistingOutput)
    {
        if (string.IsNullOrWhiteSpace(rootToolCallId) || string.IsNullOrWhiteSpace(output))
            return;

        var rootToolMessage = chat.Messages.LastOrDefault(m => m.ToolCallId == rootToolCallId);
        if (rootToolMessage is null)
            return;

        rootToolMessage.ToolOutput = MergeTerminalOutput(rootToolMessage.ToolOutput, output, replaceExistingOutput);
    }

    private static string MergeTerminalOutput(string? existingOutput, string incomingOutput, bool replaceExistingOutput)
    {
        if (string.IsNullOrWhiteSpace(incomingOutput))
            return existingOutput ?? string.Empty;

        if (replaceExistingOutput || string.IsNullOrWhiteSpace(existingOutput))
            return incomingOutput;

        if (incomingOutput.StartsWith(existingOutput, StringComparison.Ordinal))
            return incomingOutput;

        if (existingOutput.EndsWith(incomingOutput, StringComparison.Ordinal))
            return existingOutput;

        return existingOutput + Environment.NewLine + incomingOutput;
    }

    /// <summary>Converts a file:// URI or plain path to a local filesystem path.</summary>
    private static string? UriToLocalPath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;
        // Already a plain path
        if (Path.IsPathRooted(uri))
            return uri;
        return null;
    }

    /// <summary>Extracts terminal/text output from SDK tool result fields.</summary>
    private static string? ExtractTerminalOutput(ToolExecutionCompleteDataResult? result)
    {
        if (result is null)
            return null;

        var detailed = CleanTerminalOutput(result.DetailedContent);
        if (!string.IsNullOrWhiteSpace(detailed))
            return detailed;

        var contentsText = ExtractTerminalOutput(result.Contents);
        if (!string.IsNullOrWhiteSpace(contentsText))
            return CleanTerminalOutput(contentsText);

        return CleanTerminalOutput(result.Content);
    }

    /// <summary>Extracts terminal/text output from tool execution result contents.</summary>
    private static string? ExtractTerminalOutput(ToolExecutionCompleteDataResultContentsItem[]? contents)
    {
        if (contents is not { Length: > 0 })
            return null;

        var chunks = new List<string>();
        foreach (var item in contents)
        {
            if (item is ToolExecutionCompleteDataResultContentsItemTerminal terminal)
            {
                if (!string.IsNullOrWhiteSpace(terminal.Text))
                    chunks.Add(terminal.Text);
                continue;
            }

            if (item is ToolExecutionCompleteDataResultContentsItemText text
                && !string.IsNullOrWhiteSpace(text.Text))
            {
                chunks.Add(text.Text);
            }
        }

        return chunks.Count > 0 ? string.Join(Environment.NewLine, chunks) : null;
    }

    /// <summary>Strips SDK metadata lines (e.g. exit code markers) from terminal output.</summary>
    private static string? CleanTerminalOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return output;
        // Remove trailing "<exited with exit code N>" lines
        return Regex.Replace(output, @"\s*<exited with exit code \d+>\s*$", "", RegexOptions.IgnoreCase).TrimEnd();
    }

    /// <summary>Extensions for intermediary/script files that shouldn't appear as attachment chips.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".ps1", ".bat", ".cmd", ".sh", ".bash", ".vbs", ".wsf", ".js", ".mjs", ".ts"
    };

    /// <summary>
    /// Picks the best model from a list of model IDs using name/version heuristics.
    /// Prefers: flagship tiers (opus > sonnet > pro > base gpt) with highest version,
    /// avoids: mini, fast, codex, haiku, preview variants.
    /// </summary>
    public static string? PickBestModel(IReadOnlyList<string> models)
    {
        if (models.Count == 0) return null;

        return models
            .OrderByDescending(ScoreModel)
            .ThenByDescending(m => m) // alphabetical tiebreaker (higher version strings win)
            .First();
    }

    private static int ScoreModel(string id)
    {
        var m = id.ToLowerInvariant();
        int score = 0;

        // ── Tier scoring (primary) ──
        if (m.Contains("opus"))        score += 5000;
        else if (m.Contains("sonnet")) score += 4000;
        else if (m.Contains("pro"))    score += 3000;
        else if (m.Contains("haiku"))  score += 1000;
        else                           score += 2000; // gpt-N, etc.

        // ── Version extraction: find the first N.N or N pattern ──
        var versionMatch = VersionRegex().Match(m);
        if (versionMatch.Success)
        {
            var major = int.Parse(versionMatch.Groups[1].Value);
            var minor = versionMatch.Groups[2].Success ? int.Parse(versionMatch.Groups[2].Value) : 0;
            score += major * 100 + minor * 10;
        }

        // ── Penalties for specialized/diminished variants ──
        if (m.Contains("mini"))    score -= 800;
        if (m.Contains("fast"))    score -= 400;
        if (m.Contains("codex"))   score -= 300;
        if (m.Contains("preview")) score -= 200;

        return score;
    }

    /// <summary>Returns true if the file looks like a user-facing deliverable, not a temp script.</summary>
    public static bool IsUserFacingFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return !ScriptExtensions.Contains(ext);
    }

    [GeneratedRegex(@"(?:^|[\s`""'(\[])([A-Za-z]:\\[^\s`""'<>|*?\[\]]+\.\w{1,10})|(?:^|[\s`""'(\[])((?:/|~/)[^\s`""'<>|*?\[\]]+\.\w{1,10})", RegexOptions.Multiline)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    public static string[] ExtractFilePathsFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var matches = FilePathRegex().Matches(content);
        return matches
            .Select(m => !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Removes the user message and its response, then resends.
    /// The message content may have been edited before calling this.
    /// </summary>
    public async Task ResendFromMessageAsync(ChatMessage userMessage)
    {
        if (CurrentChat is null) return;

        // Stop any active generation first
        if (IsBusy)
            await StopGeneration();

        var idx = CurrentChat.Messages.IndexOf(userMessage);
        if (idx < 0) return;

        var prompt = userMessage.Content;

        // Remove the user message and everything after it
        while (CurrentChat.Messages.Count > idx)
            CurrentChat.Messages.RemoveAt(CurrentChat.Messages.Count - 1);

        // Rebuild the UI without the removed messages
        Messages.Clear();
        foreach (var msg in CurrentChat.Messages.Where(m =>
            m.Role != "reasoning"
            && !(m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content))))
            Messages.Add(new ChatMessageViewModel(msg));

        _shownFileChips.Clear();
        _pendingSearchSources.Clear();
        _pendingFetchedSkillRefs.Clear();

        // Invalidate the session — the server-side history contains the unedited exchange.
        // TODO: replay prior history once SDK supports seeding sessions with messages.
        _sessionCache.Remove(CurrentChat.Id);
        CurrentChat.CopilotSessionId = null;
        _activeSession = null;

        // Re-add the user message as a fresh entry
        var newUserMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = userMessage.Author
        };
        CurrentChat.Messages.Add(newUserMsg);
        Messages.Add(new ChatMessageViewModel(newUserMsg));
        await SaveCurrentChatAsync();
        ScrollToEndRequested?.Invoke();

        // Resend
        if (!_copilotService.IsConnected)
        {
            StatusText = Loc.Status_NotConnected;
            try { await _copilotService.ConnectAsync(); }
            catch { StatusText = Loc.Status_ConnectionFailedShort; return; }
        }

        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = CurrentChat.Id;
            if (_ctsSources.TryGetValue(chatId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            var cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

            var needsSessionSetup = _activeSession?.SessionId != CurrentChat.CopilotSessionId
                                    || CurrentChat.CopilotSessionId is null;
            if (needsSessionSetup)
                await EnsureSessionAsync(CurrentChat, cts.Token);

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            await _activeSession!.SendAsync(new MessageOptions { Prompt = prompt }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Loc.Status_Error, ex.Message);
            IsBusy = false;
            IsStreaming = false;
            if (CurrentChat is not null)
            {
                var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
                runtime.IsBusy = false;
                runtime.IsStreaming = false;
                runtime.StatusText = StatusText;
            }
        }
    }

    private string GetWorkingDirectory()
    {
        // Default to user's home/Documents folder
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var lumiDir = Path.Combine(docs, "Lumi");
        Directory.CreateDirectory(lumiDir);
        return lumiDir;
    }

    private static string FormatToolDisplayName(string toolName, string? argsJson = null)
    {
        var fileName = ExtractShortFileName(argsJson);
        return toolName switch
        {
            "web_fetch" or "fetch" or "lumi_fetch" => Loc.Tool_ReadingWebsite,
            "web_search" or "search" or "lumi_search" => Loc.Tool_SearchingWeb,
            "view" or "read_file" or "read" => fileName is not null ? string.Format(Loc.Tool_ReadingNamed, fileName) : Loc.Tool_ReadingFile,
            "create" or "write_file" or "create_file" or "write" or "save_file" => fileName is not null ? string.Format(Loc.Tool_CreatingNamed, fileName) : Loc.Tool_CreatingFile,
            "edit" or "edit_file" or "str_replace_editor" or "str_replace" or "replace_string_in_file" or "insert" => fileName is not null ? string.Format(Loc.Tool_EditingNamed, fileName) : Loc.Tool_EditingFile,
            "multi_replace_string_in_file" => fileName is not null ? string.Format(Loc.Tool_EditingNamed, fileName) : Loc.Tool_EditingFiles,
            "list_dir" or "list_directory" or "ls" => Loc.Tool_ListingDirectory,
            "bash" or "shell" or "powershell" or "run_command" or "execute_command" or "run_terminal" or "run_in_terminal" => Loc.Tool_RunningCommand,
            "read_powershell" => Loc.Tool_ReadingTerminal,
            "write_powershell" => Loc.Tool_WritingTerminal,
            "stop_powershell" => Loc.Tool_StoppingTerminal,
            "report_intent" => Loc.Tool_Planning,
            "grep" or "grep_search" or "search_files" or "glob" => Loc.Tool_SearchingFiles,
            "file_search" or "find" => Loc.Tool_FindingFiles,
            "semantic_search" => Loc.Tool_SearchingCodebase,
            "delete_file" or "delete" or "rm" => fileName is not null ? string.Format(Loc.Tool_DeletingNamed, fileName) : Loc.Tool_DeletingFile,
            "move_file" or "rename_file" or "mv" or "rename" => fileName is not null ? string.Format(Loc.Tool_MovingNamed, fileName) : Loc.Tool_MovingFile,
            "get_errors" or "diagnostics" => fileName is not null ? string.Format(Loc.Tool_CheckingNamed, fileName) : Loc.Tool_CheckingErrors,
            "browser" => Loc.Tool_OpeningPage,
            "browser_look" => Loc.Tool_BrowserSnapshot,
            "browser_do" => Loc.Tool_Action,
            "browser_js" => Loc.Tool_BrowserEvaluate,
            "save_memory" => Loc.Tool_Remembering,
            "update_memory" => Loc.Tool_UpdatingMemory,
            "delete_memory" => Loc.Tool_Forgetting,
            "recall_memory" => Loc.Tool_Recalling,
            "announce_file" => Loc.Tool_SharingFile,
            "fetch_skill" => Loc.Tool_FetchingSkill,
            "ask_question" => Loc.Tool_AskingQuestion,
            "code_review" => Loc.Tool_ReviewingCode,
            "generate_tests" => Loc.Tool_GeneratingTests,
            "explain_code" => Loc.Tool_ExplainingCode,
            "analyze_project" => Loc.Tool_AnalyzingProject,
            "ui_list_windows" => Loc.Tool_ListingWindows,
            "ui_press_keys" => Loc.Tool_PressingKeys,
            "ui_inspect" => Loc.Tool_InspectingWindow,
            "ui_find" => Loc.Tool_FindingElement,
            "ui_click" => Loc.Tool_ClickingControl,
            "ui_type" => Loc.Tool_TypingInControl,
            "ui_read" => Loc.Tool_ReadingControl,
            _ => FormatSnakeCaseToTitle(toolName)
        };
    }

    private static string? ExtractShortFileName(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            string? fullPath = null;
            if (root.TryGetProperty("filePath", out var fp)) fullPath = fp.GetString();
            else if (root.TryGetProperty("path", out var p)) fullPath = p.GetString();
            else if (root.TryGetProperty("file", out var f)) fullPath = f.GetString();
            else if (root.TryGetProperty("filename", out var fn)) fullPath = fn.GetString();
            else if (root.TryGetProperty("file_path", out var fp2)) fullPath = fp2.GetString();
            // For multi_replace, check first replacement
            else if (root.TryGetProperty("replacements", out var repl)
                     && repl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in repl.EnumerateArray())
                {
                    if (item.TryGetProperty("filePath", out var rfp)) { fullPath = rfp.GetString(); break; }
                    if (item.TryGetProperty("path", out var rp)) { fullPath = rp.GetString(); break; }
                }
            }
            return fullPath is not null ? Path.GetFileName(fullPath) : null;
        }
        catch { return null; }
    }

    private static string FormatSnakeCaseToTitle(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return name;
        words[0] = char.ToUpper(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }
}

public partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessage Message { get; }

    [ObservableProperty] private string _content;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _toolStatus;

    public string Role => Message.Role;
    public string? Author => Message.Author;
    public string TimestampText => Message.Timestamp.ToString("HH:mm");
    public string? ToolName => Message.ToolName;

    public ChatMessageViewModel(ChatMessage message)
    {
        Message = message;
        _content = message.Content;
        _isStreaming = message.IsStreaming;
        _toolStatus = message.ToolStatus;
    }

    public void NotifyContentChanged()
    {
        Content = Message.Content;
    }

    public void NotifyStreamingEnded()
    {
        Content = Message.Content;
        IsStreaming = false;
    }

    public void NotifyToolStatusChanged()
    {
        ToolStatus = Message.ToolStatus;
    }
}
