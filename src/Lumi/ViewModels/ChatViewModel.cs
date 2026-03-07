using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using StrataTheme.Controls;

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
    private readonly List<SearchSource> _pendingSearchSources = [];

    private readonly TranscriptBuilder _transcriptBuilder;

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
    /// <summary>Skills activated mid-chat (after session exists). Consumed on next SendMessage to inject into prompt.</summary>
    private readonly List<Guid> _pendingSkillInjections = new();
    /// <summary>Per-chat guard so suggestion generation is queued at most once concurrently.</summary>
    private readonly HashSet<Guid> _suggestionGenerationInFlightChats = new();
    /// <summary>Tracks the last assistant message ID that already produced suggestions per chat.</summary>
    private readonly Dictionary<Guid, Guid> _lastSuggestedAssistantMessageByChat = new();

    private sealed class ChatRuntimeState
    {
        private bool _isBusy;
        public Chat? Chat { get; init; }
        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy == value) return; _isBusy = value; if (Chat is not null) Chat.IsRunning = value; }
        }
        public bool IsStreaming { get; set; }
        public string StatusText { get; set; } = "";
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
    }

    /// <summary>True while LoadChat is bulk-adding messages. The View skips CollectionChanged.Add during this.</summary>
    [ObservableProperty] private bool _isLoadingChat;

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
    [ObservableProperty] private long _totalInputTokens;
    [ObservableProperty] private long _totalOutputTokens;

    public bool HasTokenUsage => TotalInputTokens > 0 || TotalOutputTokens > 0;
    public string TokenUsageSummary => FormatTokenCount(TotalInputTokens + TotalOutputTokens);
    public string TokenInputDisplay => $"{TotalInputTokens:N0}";
    public string TokenOutputDisplay => $"{TotalOutputTokens:N0}";
    public string TokenTotalDisplay => $"{TotalInputTokens + TotalOutputTokens:N0}";

    partial void OnTotalInputTokensChanged(long value) { NotifyTokenPropertiesChanged(); }
    partial void OnTotalOutputTokensChanged(long value) { NotifyTokenPropertiesChanged(); }

    private void NotifyTokenPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasTokenUsage));
        OnPropertyChanged(nameof(TokenUsageSummary));
        OnPropertyChanged(nameof(TokenInputDisplay));
        OnPropertyChanged(nameof(TokenOutputDisplay));
        OnPropertyChanged(nameof(TokenTotalDisplay));
    }

    private static string FormatTokenCount(long tokens) => tokens switch
    {
        < 1_000 => $"{tokens}",
        < 1_000_000 => $"{tokens / 1_000.0:0.#}K",
        _ => $"{tokens / 1_000_000.0:0.##}M"
    };

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    /// <summary>Virtualized turn list for the chat transcript. Bound to StrataChatTranscript.ItemsSource.</summary>
    [ObservableProperty] private ObservableCollection<TranscriptTurnControl> _transcriptTurns = [];

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

    [ObservableProperty] private string _suggestionA = Loc.Chat_SuggestionA;
    [ObservableProperty] private string _suggestionB = Loc.Chat_SuggestionB;
    [ObservableProperty] private string _suggestionC = Loc.Chat_SuggestionC;
    [ObservableProperty] private bool _isSuggestionsGenerating;

    // Events for the view to react to
    public event Action? ScrollToEndRequested;
    public event Action? UserMessageSent;
    public event Action? ChatUpdated;
    public event Action<Guid, string>? ChatTitleChanged;
    public event Action? BrowserHideRequested;
    /// <summary>Raised when a file-edit tool wants to show a diff in the preview island.</summary>
    public event Action<FileChangeItem>? DiffShowRequested;
    /// <summary>Raised to hide the diff preview island.</summary>
    public event Action? DiffHideRequested;
    /// <summary>Raised when the user clicks the plan card to open it in the right panel.</summary>
    public event Action? PlanShowRequested;
    /// <summary>Raised to hide the plan preview island.</summary>
    public event Action? PlanHideRequested;


    /// <summary>Raised when the LLM calls ask_question. Args: questionId, question, options (comma-separated), allowFreeText.</summary>
    public event Action<string, string, string, bool>? QuestionAsked;

    /// <summary>Pending question completions keyed by question ID.</summary>
    private readonly Dictionary<string, TaskCompletionSource<string>> _pendingQuestions = new();

    /// <summary>Raised when the view should rebuild DataTemplates (e.g. settings changed).</summary>
    public event Action? TranscriptRebuilt;

    public ChatViewModel(DataStore dataStore, CopilotService copilotService, BrowserService browserService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _browserService = browserService;
        _memoryAgentService = new MemoryAgentService(dataStore, copilotService);
        _codingToolService = new CodingToolService(copilotService, GetCurrentCancellationToken);
        _selectedModel = dataStore.Data.Settings.PreferredModel;

        _transcriptBuilder = new TranscriptBuilder(
            dataStore,
            showDiffAction: item => DiffShowRequested?.Invoke(item),
            submitQuestionAnswerAction: SubmitQuestionAnswer,
            resendFromMessageAction: ResendFromMessageAsync);
        _transcriptBuilder.SetLiveTarget(_transcriptTurns);

        // Seed with preferred modelso the ComboBox has an initial selection
        if (!string.IsNullOrWhiteSpace(_selectedModel))
            AvailableModels.Add(_selectedModel);

        // Default all enabled MCPs to active so the MCP picker shows them checked
        PopulateDefaultMcps();

        // Wire messages → transcript items
        Messages.CollectionChanged += (_, args) =>
        {
            if (IsLoadingChat || _transcriptBuilder.IsRebuildingTranscript) return;

            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (ChatMessageViewModel msgVm in args.NewItems)
                    _transcriptBuilder.ProcessMessageToTranscript(msgVm);
            }
            else if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                TranscriptTurns.Clear();
                _transcriptBuilder.ResetState();
            }
        };

        // When the CopilotService reconnects (new CLI process), all cached sessions
        // are invalid — they reference the old, dead client.
        _copilotService.Reconnected += OnCopilotReconnected;

        InitializeMvvmUiState();
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
            _transcriptBuilder.ShowTypingIndicator(StatusText);
        else
        {
            _transcriptBuilder.HideTypingIndicator();
            // Refresh git status after turn completes
            if (IsCodingProject)
                RefreshCodingProjectState();
        }
    }

    partial void OnStatusTextChanged(string value)
    {
        if (IsBusy)
            _transcriptBuilder.UpdateTypingIndicatorLabel(value);
    }

    internal void RebuildTranscript()
    {
        TranscriptTurns = _transcriptBuilder.Rebuild(Messages);
        TranscriptRebuilt?.Invoke();
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
        var accContent = new System.Text.StringBuilder(4096);
        var accReasoning = new System.Text.StringBuilder(1024);
        ChatMessage? streamingMsg = null;
        ChatMessage? reasoningMsg = null;
        ChatMessageViewModel? streamingVm = null;
        ChatMessageViewModel? reasoningVm = null;
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
                        accContent.Append(delta.Data.DeltaContent);
                        runtime.StatusText = Loc.Status_Generating;
                        if (streamingMsg is null)
                        {
                            var currentContent = accContent.ToString();
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
                        }
                        else
                        {
                            streamingMsg.Content = accContent.ToString();
                            if (_activeSession == session)
                            {
                                streamingVm?.NotifyContentChanged();
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
                                _inProgressMessages.Remove(chat.Id);
                                if (_activeSession == session)
                                {
                                    if (streamingVm is not null) Messages.Remove(streamingVm);
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
                                _inProgressMessages.Remove(chat.Id);
                                if (_activeSession == session)
                                {
                                    streamingVm?.NotifyStreamingEnded();
                                }
                            }
                        }
                        streamingMsg = null;
                        streamingVm = null;
                        accContent.Clear();
                    });
                    break;

                case AssistantReasoningDeltaEvent rd:
                    Dispatcher.UIThread.Post(() =>
                    {
                        accReasoning.Append(rd.Data.DeltaContent);
                        runtime.StatusText = Loc.Status_Reasoning;
                        if (reasoningMsg is null)
                        {
                            var currentReasoning = accReasoning.ToString();
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
                        }
                        else
                        {
                            reasoningMsg.Content = accReasoning.ToString();
                            if (_activeSession == session)
                            {
                                reasoningVm?.NotifyContentChanged();
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
                                reasoningVm?.NotifyStreamingEnded();
                            }
                        }
                        reasoningMsg = null;
                        reasoningVm = null;
                        accReasoning.Clear();
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

                case AssistantTurnEndEvent:
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
                        accContent.Clear();
                    }
                    if (reasoningMsg is not null)
                    {
                        reasoningMsg.IsStreaming = false;
                        if (_activeSession == session)
                        {
                            reasoningVm?.NotifyStreamingEnded();
                        }
                        reasoningMsg = null;
                        reasoningVm = null;
                        accReasoning.Clear();
                    }

                    runtime.IsBusy = false;
                    runtime.IsStreaming = false;
                    runtime.StatusText = string.Format(Loc.Status_Error, err.Data.Message);
                    if (_activeSession == session)
                    {
                        // Clean up typing indicator and tool groups
                        _transcriptBuilder.HideTypingIndicator();
                        _transcriptBuilder.CloseCurrentToolGroup();

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
                    Dispatcher.UIThread.Post(() =>
                    {
                    // SDK-initiated abort — clean up streaming state
                    string? partialContent = null;
                    if (streamingMsg is not null)
                    {
                        streamingMsg.IsStreaming = false;
                        if (!string.IsNullOrWhiteSpace(streamingMsg.Content))
                        {
                            partialContent = streamingMsg.Content;
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
                        accContent.Clear();
                    }
                    if (reasoningMsg is not null)
                    {
                        reasoningMsg.IsStreaming = false;
                        if (_activeSession == session)
                        {
                            reasoningVm?.NotifyStreamingEnded();
                        }
                        reasoningMsg = null;
                        reasoningVm = null;
                        accReasoning.Clear();
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
                    // SDK session already records the aborted turn in its event log,
                    // so the LLM will see the partial content on the next turn automatically.
                    QueueSaveChat(chat, saveIndex: false);
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

                    // The SDK fires ToolExecutionStartEvent before SubagentStartedEvent
                    // with the same ToolCallId — reuse that message instead of duplicating.
                    var existing = chat.Messages.LastOrDefault(m => m.ToolCallId == subStart.Data.ToolCallId);
                    if (existing is not null)
                    {
                        existing.ToolName = $"agent:{subStart.Data.AgentName}";
                        existing.Content = subStart.Data.AgentDescription ?? "";
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
                            Content = subStart.Data.AgentDescription ?? "",
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
        _suggestionGenerationInFlightChats.Remove(chatId);
        _lastSuggestedAssistantMessageByChat.Remove(chatId);
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
    private async Task<bool> EnsureSessionAsync(Chat chat, CancellationToken ct, bool allowCreateFallback = true)
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

        // If a workspace agent (from .github/agents/) is selected, load its content and append to system prompt
        var workspaceAgentName = chat.SdkAgentName ?? SelectedSdkAgentName;
        if (!string.IsNullOrWhiteSpace(workspaceAgentName) && ActiveAgent is null)
        {
            var agentDir = GetEffectiveWorkingDirectory();
            var agentFile = Path.Combine(agentDir, ".github", "agents", workspaceAgentName + ".md");
            if (File.Exists(agentFile))
            {
                try
                {
                    var agentContent = await File.ReadAllTextAsync(agentFile, ct);
                    systemPrompt = (systemPrompt ?? "") + "\n\n--- Active Agent: " + workspaceAgentName + " ---\n" + agentContent;
                }
                catch { /* best effort */ }
            }
        }

        var skillDirs = new List<string>();
        if (ActiveSkillIds.Count > 0)
        {
            var dir = await _dataStore.SyncSkillFilesForIdsAsync(ActiveSkillIds);
            skillDirs.Add(dir);
        }

        var customAgents = BuildCustomAgents();
        var customTools = BuildCustomTools();
        var workDir = GetEffectiveWorkingDirectory();
        var mcpServers = BuildMcpServers(workDir);
        var reasoningEffort = _dataStore.Data.Settings.ReasoningEffort;
        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;

        // Native user input handler — wired to the existing question card UI
        UserInputHandler userInputHandler = async (request, invocation) =>
        {
            var questionId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingQuestions[questionId] = tcs;

            var optionsStr = request.Choices is { Count: > 0 } ? string.Join(",", request.Choices) : "";
            var freeText = request.AllowFreeform ?? true;

            Dispatcher.UIThread.Post(() =>
            {
                _transcriptBuilder.AddQuestionToTranscript(questionId, request.Question, optionsStr, freeText);
                QuestionAsked?.Invoke(questionId, request.Question, optionsStr, freeText);
                ScrollToEndRequested?.Invoke();
            });

            var answer = await tcs.Task;
            _pendingQuestions.Remove(questionId);
            return new GitHub.Copilot.SDK.UserInputResponse { Answer = answer, WasFreeform = true };
        };

        // Session hooks for lifecycle events
        var hooks = new GitHub.Copilot.SDK.SessionHooks
        {
            OnPreToolUse = async (input, invocation) =>
            {
                // Auto-allow all tools (permission UI can be added later)
                return new GitHub.Copilot.SDK.PreToolUseHookOutput { PermissionDecision = "allow" };
            },
            OnErrorOccurred = async (input, invocation) =>
            {
                // Retry transient errors, abort on persistent ones
                if (input.Recoverable)
                    return new GitHub.Copilot.SDK.ErrorOccurredHookOutput { ErrorHandling = "retry", RetryCount = 2 };
                return new GitHub.Copilot.SDK.ErrorOccurredHookOutput { ErrorHandling = "abort" };
            }
        };

        if (mcpServers is { Count: > 0 })
            StatusText = Loc.Status_ConnectingMcp;

        // When MCP servers are configured, apply a timeout so a broken server
        // doesn't block the UI indefinitely.
        using var sessionCts = mcpServers is { Count: > 0 }
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        sessionCts?.CancelAfter(TimeSpan.FromSeconds(30));
        var sessionCt = sessionCts?.Token ?? ct;

        if (chat.CopilotSessionId is null)
        {
            if (!allowCreateFallback)
                return false;

            try
            {
                var createConfig = SessionConfigBuilder.Build(
                    systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools,
                    mcpServers, effort, userInputHandler, onPermission: null, hooks);
                var createdSession = await _copilotService.CreateSessionAsync(createConfig, sessionCt);
                chat.CopilotSessionId = createdSession.SessionId;
                _activeSession = createdSession;
                SubscribeToSession(createdSession, chat);
                return true;
            }
            catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("MCP server connection timed out. Check that your MCP servers are installed and responding.");
            }
        }

        // Try to resume with retry for transient errors
        const int maxRetries = 2;
        Exception? lastError = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                StatusText = attempt > 0 ? Loc.Status_Reconnecting : Loc.Status_Resuming;
                var resumeConfig = SessionConfigBuilder.BuildForResume(
                    systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools,
                    mcpServers, effort, userInputHandler, onPermission: null, hooks);
                var session = await _copilotService.ResumeSessionAsync(
                    chat.CopilotSessionId, resumeConfig, sessionCt);
                _activeSession = session;
                SubscribeToSession(session, chat);
                return true; // Resume succeeded
            }
            catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("MCP server connection timed out. Check that your MCP servers are installed and responding.");
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxRetries)
                {
                    await Task.Delay(500 * (attempt + 1), ct);
                    continue;
                }
            }
        }

        // All retries failed.
        StatusText = Loc.Status_SessionExpired;
        if (!allowCreateFallback)
            return false;

        try
        {
            var createConfig = SessionConfigBuilder.Build(
                systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools,
                mcpServers, effort, userInputHandler, onPermission: null, hooks);
            var createdSession = await _copilotService.CreateSessionAsync(createConfig, sessionCt);
            chat.CopilotSessionId = createdSession.SessionId;
            _activeSession = createdSession;
            SubscribeToSession(createdSession, chat);
            await SaveCurrentChatAsync();
            return true;
        }
        catch (OperationCanceledException) when (sessionCts is not null && sessionCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("MCP server connection timed out. Check that your MCP servers are installed and responding.");
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

            // Yield so the UI thread can render the loading overlay before heavy synchronous work
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // Set the active session (don't dispose anything — background sessions keep running)
            _sessionCache.TryGetValue(chat.Id, out var cachedSession);
            _activeSession = cachedSession;

            // Clear pending state from any previous chat
            _pendingSkillInjections.Clear();
        _activeWorkspaceSkillNames.Clear();
            _pendingSearchSources.Clear();
            _transcriptBuilder.PendingFetchedSkillRefs.Clear();

            // Restore real runtime state for this session/chat
            var runtime = GetOrCreateRuntimeState(chat.Id);
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;
            TotalInputTokens = runtime.TotalInputTokens;
            TotalOutputTokens = runtime.TotalOutputTokens;

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

            // Rebuild transcript items from loaded messages
            RebuildTranscript();

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

            // Restore SDK agent selection
            SelectedSdkAgentName = chat.SdkAgentName;

            // Refresh SDK agents if we have a session
            if (_activeSession is not null)
            {
                _ = PopulateFromSessionAsync();
                _ = RefreshPlanAsync(chat);
            }
            else
            {
                HasPlan = false;
                PlanContent = null;
            }
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

    /// <summary>Refreshes plan state for a chat when a session is available.</summary>
    private async Task RefreshPlanAsync(Chat chat)
    {
        if (_activeSession is null) return;
        try
        {
            var (exists, content) = await _copilotService.ReadSessionPlanAsync(_activeSession);
            HasPlan = exists;
            PlanContent = content;
            if (exists)
                StagePlanCard("Plan");
        }
        catch { /* best effort */ }
    }

    /// <summary>Stages a plan card for insertion at end of the current turn via TranscriptBuilder.</summary>
    private void StagePlanCard(string statusText)
    {
        _transcriptBuilder.SetPendingPlanCard(statusText, () => PlanShowRequested?.Invoke());
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
        PlanHideRequested?.Invoke();
        HasUsedBrowser = false;

        // Detach from current chat without destroying its session.
        // Sessions are cleaned only when a chat is deleted via CleanupSession(chatId).
        _activeSession = null;

        Messages.Clear();
        TranscriptTurns.Clear();
        _transcriptBuilder.ResetState();
        CurrentChat = null;
        IsBusy = false;
        IsStreaming = false;
        _pendingSearchSources.Clear();
        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        ActiveMcpServerNames.Clear();
        ActiveMcpChips.Clear();
        PendingAttachments.Clear();
        PendingAttachmentItems.Clear();
        AvailableFileSuggestions = null;
        _fileSearchCts?.Cancel();
        _fileSearchCts?.Dispose();
        _fileSearchCts = null;
        PopulateDefaultMcps();
        _pendingProjectId = null;
        _pendingSkillInjections.Clear();
        _activeWorkspaceSkillNames.Clear();
        StatusText = "";
        ActiveAgent = null;

        // Reset plan/SDK agent state
        HasPlan = false;
        PlanContent = null;
        IsPlanOpen = false;
        SelectedSdkAgentName = null;
        SdkAgentChips.Clear();

        SyncComposerProjectSelectionFromState();
        RefreshProjectBadge();
    }

    /// <summary>
    /// Called when MCP server config changes so the next message creates a fresh session with updated MCP servers.
    /// </summary>
    public void InvalidateMcpSession()
    {
        if (CurrentChat is not null)
        {
            InvalidateCurrentSession();
            _pendingSkillInjections.Clear();
        _activeWorkspaceSkillNames.Clear();
        }
    }

    /// <summary>Discards the current chat's session so a fresh one is created on the next message.</summary>
    private void InvalidateCurrentSession()
    {
        if (CurrentChat is null) return;
        var chatId = CurrentChat.Id;

        if (_sessionCache.TryGetValue(chatId, out var session))
        {
            _ = _copilotService.DeleteSessionAsync(session.SessionId);
            _sessionCache.Remove(chatId);
        }
        CurrentChat.CopilotSessionId = null;
        _activeSession = null;
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
                ProjectId = _pendingProjectId ?? ActiveProjectFilterId,
                ActiveSkillIds = new List<Guid>(ActiveSkillIds),
                ActiveMcpServerNames = new List<string>(ActiveMcpServerNames),
                SdkAgentName = SelectedSdkAgentName,
                WorktreePath = IsWorktreeMode ? WorktreePath : null
            };
            _pendingProjectId = null;
            _dataStore.Data.Chats.Add(chat);
            CurrentChat = chat;
            // SDK generates titles automatically via SessionTitleChangedEvent
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

        CancellationTokenSource? cts = null;
        MessageOptions? sendOptions = null;
        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = CurrentChat.Id;
            if (_ctsSources.TryGetValue(chatId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();

                // Abort the session so the SDK fully stops the old turn before
                // we send a new one. Without this, two concurrent SendAsync calls
                // end up on the same session, corrupting SDK state.
                if (_sessionCache.TryGetValue(chatId, out var cachedSession))
                {
                    try { await cachedSession.AbortAsync(); }
                    catch { /* best-effort */ }
                }
            }
            cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            var needsSessionSetup = _activeSession?.SessionId != CurrentChat.CopilotSessionId
                                    || CurrentChat.CopilotSessionId is null;

            if (needsSessionSetup)
            {
                await EnsureSessionAsync(CurrentChat, cts.Token);

                // If a custom agent is selected, route the session through it via the SDK Agent API
                if (ActiveAgent is not null && _activeSession is not null)
                {
                    try { await _copilotService.SelectSessionAgentAsync(_activeSession, ActiveAgent.Name, cts.Token); }
                    catch { /* Lumi agents may not be selectable via SDK Agent API — they work via system prompt */ }
                }

                // Workspace agents (.github/agents/) are handled via system prompt injection
                // in EnsureSessionAsync — no Agent.SelectAsync needed.

                // Discover SDK agents in background (non-blocking)
                _ = PopulateFromSessionAsync();
                // Refresh quota in background
                _ = RefreshQuotaAsync();
            }

            sendOptions = new MessageOptions { Prompt = prompt };

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
                    sendOptions.Prompt += skillContext;
                }
            }

            // Inject workspace skill references (from .github/skills/) — instruct LLM to use fetch_skill
            if (_activeWorkspaceSkillNames.Count > 0)
            {
                var skillNames = string.Join(", ", _activeWorkspaceSkillNames.Select(n => $"\"{n}\""));
                sendOptions.Prompt += $"\n\n[Use the following skills to help with this request: {skillNames}. " +
                                      "Retrieve each skill's content using the fetch_skill tool before proceeding.]";
            }

            if (attachments is { Count: > 0 })
                sendOptions.Attachments = attachments.Cast<UserMessageDataAttachmentsItem>().ToList();

            await _activeSession!.SendAsync(sendOptions, cts.Token);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex) && CurrentChat is not null && cts is not null && sendOptions is not null)
        {
            // Stale session cache — evict and resume
            try
            {
                StatusText = Loc.Status_Reconnecting;
                InvalidateLocalSessionCache(CurrentChat);
                await EnsureSessionAsync(CurrentChat, cts.Token);
                await _activeSession!.SendAsync(sendOptions, cts.Token);
            }
            catch (Exception retryEx)
            {
                HandleSendError(retryEx, cts);
            }
        }
        catch (OperationCanceledException) when (cts is not null && !cts.IsCancellationRequested)
        {
            // SDK cancelled internally (e.g. MCP server failure) — surface as error
            var errorText = string.Format(Loc.Status_Error, "Session cancelled unexpectedly. MCP servers may have failed to connect.");
            StatusText = errorText;
            IsBusy = false;
            IsStreaming = false;
            _transcriptBuilder.HideTypingIndicator();
            _transcriptBuilder.CloseCurrentToolGroup();
            if (CurrentChat is not null)
            {
                var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
                runtime.IsBusy = false;
                runtime.IsStreaming = false;
                runtime.StatusText = StatusText;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected, no error to surface
        }
        catch (Exception ex) when (cts is not null)
        {
            HandleSendError(ex, cts);
        }
    }

    /// <summary>Detects a stale cached session (the session ID is unknown to the current CLI process).</summary>
    private static bool IsSessionNotFoundError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("Session not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Evicts a stale session from the local cache so EnsureSessionAsync will
    /// re-establish it via ResumeSessionAsync, preserving server-side context.</summary>
    private void InvalidateLocalSessionCache(Chat chat)
    {
        _sessionCache.Remove(chat.Id);
        if (_sessionSubs.TryGetValue(chat.Id, out var sub))
        {
            sub.Dispose();
            _sessionSubs.Remove(chat.Id);
        }
        _activeSession = null;
    }

    /// <summary>Handles a send error by surfacing it as a status + error message in the transcript.</summary>
    private void HandleSendError(Exception ex, CancellationTokenSource cts)
    {
        if (ex is OperationCanceledException && cts.IsCancellationRequested)
            return; // Cancelled by StopGeneration — expected

        var message = ex.InnerException is not null
            ? $"{ex.Message} → {ex.InnerException.Message}"
            : ex.Message;
        var errorText = string.Format(Loc.Status_Error, message);
        StatusText = errorText;
        IsBusy = false;
        IsStreaming = false;

        _transcriptBuilder.HideTypingIndicator();
        _transcriptBuilder.CloseCurrentToolGroup();

        if (CurrentChat is not null)
        {
            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = false;
            runtime.IsStreaming = false;
            runtime.StatusText = StatusText;

            var errorMsg = new ChatMessage
            {
                Role = "error",
                Author = Loc.Author_Lumi,
                Content = errorText
            };
            CurrentChat.Messages.Add(errorMsg);
            var msgVm = new ChatMessageViewModel(errorMsg);
            Messages.Add(msgVm);
            _transcriptBuilder.ProcessMessageToTranscript(msgVm);
            ScrollToEndRequested?.Invoke();
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

    /// <summary>
    /// Picks the best model from a list of model IDs using name/version heuristics.
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

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// Removes the user message and its response, then resends.
    /// The message content may have been edited before calling this.
    /// </summary>
    public async Task ResendFromMessageAsync(ChatMessage userMessage, bool wasEdited)
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

        // Preserve the retained transcript (before the edited user turn) so we can
        // rebuild context safely if we need to recreate the backend session.
        var retainedContext = CurrentChat.Messages.ToList();

        // Rebuild the UI without the removed messages
        _transcriptBuilder.IsRebuildingTranscript = true;
        Messages.Clear();
        foreach (var msg in CurrentChat.Messages.Where(m =>
            m.Role != "reasoning"
            && !(m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content))))
            Messages.Add(new ChatMessageViewModel(msg));
        _transcriptBuilder.IsRebuildingTranscript = false;

        RebuildTranscript();

        _transcriptBuilder.ShownFileChips.Clear();
        _pendingSearchSources.Clear();
        _transcriptBuilder.PendingFetchedSkillRefs.Clear();

        // For edits: there is currently no public SDK API to rewind/remove prior
        // turns from the server-side history. To avoid leaking the pre-edit prompt,
        // we recreate the backend session and pass only the retained transcript.
        // For regenerates (same content): reuse the existing session as-is.

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
            catch
            {
                StatusText = Loc.Status_ConnectionFailedShort;
                var connErrorMsg = new ChatMessage
                {
                    Role = "error",
                    Author = Loc.Author_Lumi,
                    Content = Loc.Status_ConnectionFailedShort
                };
                CurrentChat.Messages.Add(connErrorMsg);
                var connVm = new ChatMessageViewModel(connErrorMsg);
                Messages.Add(connVm);
                _transcriptBuilder.ProcessMessageToTranscript(connVm);
                ScrollToEndRequested?.Invoke();
                return;
            }
        }

        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = CurrentChat.Id;
            if (_ctsSources.TryGetValue(chatId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();

                if (_sessionCache.TryGetValue(chatId, out var cachedSession))
                {
                    try { await cachedSession.AbortAsync(); }
                    catch { /* best-effort */ }
                }
            }
            var cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

            var needsSessionSetup = _activeSession?.SessionId != CurrentChat.CopilotSessionId
                                    || CurrentChat.CopilotSessionId is null;

            // Editing must not keep old server-side context. Recreate session first.
            if (wasEdited)
            {
                InvalidateCurrentSession();
                needsSessionSetup = true;
            }

            if (needsSessionSetup)
            {
                var ok = wasEdited
                    ? await EnsureSessionAsync(CurrentChat, cts.Token, allowCreateFallback: true)
                    : await EnsureSessionAsync(CurrentChat, cts.Token, allowCreateFallback: false);

                if (!ok)
                {
                    StatusText = "Session expired. Please start a new chat.";
                    var errorMsg = new ChatMessage
                    {
                        Role = "error",
                        Author = Loc.Author_Lumi,
                        Content = "Session expired. Please start a new chat to continue."
                    };
                    CurrentChat.Messages.Add(errorMsg);
                    var msgVm = new ChatMessageViewModel(errorMsg);
                    Messages.Add(msgVm);
                    _transcriptBuilder.ProcessMessageToTranscript(msgVm);
                    ScrollToEndRequested?.Invoke();
                    return;
                }
            }

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            var resendPrompt = wasEdited
                ? BuildEditedReplayPrompt(retainedContext, prompt)
                : prompt;

            await _activeSession!.SendAsync(new MessageOptions { Prompt = resendPrompt }, cts.Token);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex) && CurrentChat is not null)
        {
            // Stale session cache — evict and resume
            try
            {
                var cts = _ctsSources.GetValueOrDefault(CurrentChat.Id);
                if (cts is null) return;
                StatusText = Loc.Status_Reconnecting;
                InvalidateLocalSessionCache(CurrentChat);
                await EnsureSessionAsync(CurrentChat, cts.Token);
                var resendPrompt2 = wasEdited
                    ? BuildEditedReplayPrompt(retainedContext, prompt)
                    : prompt;
                await _activeSession!.SendAsync(new MessageOptions { Prompt = resendPrompt2 }, cts.Token);
            }
            catch (Exception retryEx)
            {
                HandleSendError(retryEx, _ctsSources.GetValueOrDefault(CurrentChat.Id)!);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected
        }
        catch (Exception ex)
        {
            HandleSendError(ex, _ctsSources.GetValueOrDefault(CurrentChat.Id)!);
        }
    }

    private static string BuildEditedReplayPrompt(List<ChatMessage> retainedContext, string editedPrompt)
    {
        if (retainedContext.Count == 0)
            return editedPrompt;

        var lines = new List<string>
        {
            "The user edited an earlier message. Use ONLY the corrected conversation context below.",
            "Ignore any previous conversation state not included here.",
            "",
            "Conversation so far:"
        };

        foreach (var msg in retainedContext)
        {
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;

            string role = msg.Role switch
            {
                "assistant" => "Assistant",
                "system" => "System",
                _ => "User"
            };

            if (msg.Role is "user" or "assistant" or "system")
                lines.Add($"{role}: {msg.Content.Trim()}");
        }

        lines.Add("");
        lines.Add("Latest user message (edited):");
        lines.Add(editedPrompt);

        return string.Join("\n", lines);
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



