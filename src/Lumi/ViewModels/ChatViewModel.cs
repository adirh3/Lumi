using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    private static readonly bool TranscriptDiagnosticsEnabled = Debugger.IsAttached
        || string.Equals(Environment.GetEnvironmentVariable("LUMI_TRANSCRIPT_DEBUG"), "1", StringComparison.Ordinal);

    private readonly DataStore _dataStore;
    private readonly CopilotService _copilotService;
    private readonly MemoryAgentService _memoryAgentService;
    private readonly CodingToolService _codingToolService;
    private readonly UIAutomationService _uiAutomation = new();
    private readonly object _chatLoadSync = new();
    private CancellationTokenSource? _chatLoadCts;
    private long _chatLoadRequestId;
    private bool _isBulkLoadingMessages;
    /// <summary>Maps chat ID → CancellationTokenSource for per-chat cancellation.</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _ctsSources = new();
    private readonly List<SearchSource> _pendingSearchSources = [];

    private readonly TranscriptBuilder _transcriptBuilder;
    private readonly TranscriptWindowController _transcriptWindow = new(new TranscriptPagingOptions
    {
        EnableDiagnostics = TranscriptDiagnosticsEnabled,
    });

    /// <summary>The CopilotSession for the currently displayed chat. Events for this session update the UI.</summary>
    private CopilotSession? _activeSession;
    /// <summary>Maps chat ID → locally attached CopilotSession objects for active or running chats.</summary>
    private readonly Dictionary<Guid, CopilotSession> _sessionCache = new();
    /// <summary>Maps chat ID → live event subscriptions for locally attached sessions.</summary>
    private readonly Dictionary<Guid, IDisposable> _sessionSubs = new();
    /// <summary>Maps chat ID → in-progress streaming message not yet committed to Chat.Messages.</summary>
    private readonly Dictionary<Guid, ChatMessage> _inProgressMessages = new();
    /// <summary>Per-chat runtime state sourced from live session events.</summary>
    private readonly Dictionary<Guid, ChatRuntimeState> _runtimeStates = new();
    /// <summary>Maps chat ID → per-chat BrowserService instance. Created lazily on first browser tool use.</summary>
    private readonly Dictionary<Guid, BrowserService> _chatBrowserServices = new();
    /// <summary>Skills activated mid-chat (after session exists). Consumed on next SendMessage to inject into prompt.</summary>
    private readonly List<Guid> _pendingSkillInjections = new();
    /// <summary>Per-chat guard so suggestion generation is queued at most once concurrently.</summary>
    private readonly HashSet<Guid> _suggestionGenerationInFlightChats = new();
    /// <summary>Tracks the last assistant message ID that already produced suggestions per chat.</summary>
    private readonly Dictionary<Guid, Guid> _lastSuggestedAssistantMessageByChat = new();

    /// <summary>Gets or lazily creates a per-chat BrowserService instance.</summary>
    private BrowserService GetOrCreateBrowserService(Guid chatId)
    {
        if (!_chatBrowserServices.TryGetValue(chatId, out var service))
        {
            service = new BrowserService();
            _chatBrowserServices[chatId] = service;
        }
        return service;
    }

    /// <summary>Gets the BrowserService for a chat if one exists, without creating.</summary>
    public BrowserService? GetBrowserServiceForChat(Guid chatId)
    {
        _chatBrowserServices.TryGetValue(chatId, out var service);
        return service;
    }

    /// <summary>Gets all per-chat BrowserService instances (for theme propagation etc.).</summary>
    public IReadOnlyDictionary<Guid, BrowserService> ChatBrowserServices => _chatBrowserServices;

    /// <summary>True while a chat is being loaded and the loading overlay is shown.</summary>
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
    public bool ShowInfoStrip => IsCodingProject || HasTokenUsage;
    public string TokenUsageSummary => FormatTokenCount(TotalInputTokens + TotalOutputTokens);
    public string TokenInputDisplay => $"{TotalInputTokens:N0}";
    public string TokenOutputDisplay => $"{TotalOutputTokens:N0}";
    public string TokenTotalDisplay => $"{TotalInputTokens + TotalOutputTokens:N0}";

    partial void OnTotalInputTokensChanged(long value) { NotifyTokenPropertiesChanged(); }
    partial void OnTotalOutputTokensChanged(long value) { NotifyTokenPropertiesChanged(); }

    private void NotifyTokenPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasTokenUsage));
        OnPropertyChanged(nameof(ShowInfoStrip));
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
    /// <summary>Full transcript turn store retained in memory for the active chat.</summary>
    [ObservableProperty] private ObservableCollection<TranscriptTurn> _transcriptTurns = [];
    public ObservableCollection<TranscriptTurn> MountedTranscriptTurns => _transcriptWindow.MountedTurns;
    public string TranscriptDiagnosticsText => ShowTranscriptDiagnostics ? _transcriptWindow.DiagnosticsText : string.Empty;
    public bool IsTranscriptPinnedToBottom => _transcriptWindow.IsPinnedToBottom;
    public bool ShowTranscriptDiagnostics { get; } = TranscriptDiagnosticsEnabled;

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

    public ChatViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _memoryAgentService = new MemoryAgentService(dataStore, copilotService);
        _codingToolService = new CodingToolService(copilotService, GetCurrentCancellationToken);
        _selectedModel = dataStore.Data.Settings.PreferredModel;

        _transcriptBuilder = new TranscriptBuilder(
            dataStore,
            showDiffAction: item => DiffShowRequested?.Invoke(item),
            submitQuestionAnswerAction: SubmitQuestionAnswer,
            resendFromMessageAction: ResendFromMessageAsync,
            getSelectedModel: () => SelectedModel);
        _transcriptBuilder.SetLiveTarget(_transcriptTurns);
        _transcriptWindow.BindTranscript(_transcriptTurns, "ctor");
        _transcriptWindow.PropertyChanged += OnTranscriptWindowPropertyChanged;

        // Seed with preferred modelso the ComboBox has an initial selection
        if (!string.IsNullOrWhiteSpace(_selectedModel))
            AvailableModels.Add(_selectedModel);

        // Default all enabled MCPs to active so the MCP picker shows them checked
        PopulateDefaultMcps();

        // Wire messages → transcript items
        Messages.CollectionChanged += (_, args) =>
        {
            if (_isBulkLoadingMessages || _transcriptBuilder.IsRebuildingTranscript) return;

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

    private void OnTranscriptWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ShowTranscriptDiagnostics && e.PropertyName == nameof(TranscriptWindowController.DiagnosticsText))
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));

        if (e.PropertyName == nameof(TranscriptWindowController.IsPinnedToBottom))
            OnPropertyChanged(nameof(IsTranscriptPinnedToBottom));
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
                _ = RefreshCodingProjectState();
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
        _transcriptWindow.BindTranscript(TranscriptTurns, "rebuild");
        _transcriptWindow.ResetToLatest(TranscriptWindowController.DefaultInitialViewportHeight, "rebuild");

        // Rebuild() calls ResetState() which clears the typing indicator.
        // Re-show it if this chat is still busy (e.g. switching to a streaming chat).
        if (IsBusy)
            _transcriptBuilder.ShowTypingIndicator(StatusText);

        TranscriptRebuilt?.Invoke();
    }

    private static string BuildSubagentPayloadJson(
        string? description,
        string? agentName,
        string? agentDisplayName,
        string? agentDescription,
        string? mode,
        string? model = null,
        string? transcript = null,
        string? reasoning = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("description", description ?? string.Empty);
            writer.WriteString("agentName", agentName ?? string.Empty);
            writer.WriteString("agentDisplayName", agentDisplayName ?? string.Empty);
            writer.WriteString("agentDescription", agentDescription ?? string.Empty);
            writer.WriteString("mode", mode ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(model))
                writer.WriteString("model", model);
            writer.WriteString("transcript", transcript ?? string.Empty);
            writer.WriteString("reasoning", reasoning ?? string.Empty);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal TranscriptWindowMutation InitializeMountedTranscript(double viewportHeight)
    {
        var mutation = _transcriptWindow.ResetToLatest(viewportHeight, "initial-open");
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal TranscriptWindowMutation EnsureMountedTranscriptCoverage(double viewportHeight)
    {
        var mutation = _transcriptWindow.EnsureViewportCoverage(viewportHeight, "viewport-fill");
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal TranscriptWindowMutation UpdateTranscriptViewport(double offsetY, double viewportHeight, double extentHeight)
    {
        var mutation = _transcriptWindow.UpdateViewport(
            new TranscriptViewportState(
                offsetY,
                viewportHeight,
                extentHeight,
                _transcriptWindow.IsPinnedToBottom,
                _transcriptWindow.DistanceFromBottom),
            "scroll");
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return mutation;
    }

    internal void UpdateTranscriptPinnedState(bool isPinnedToBottom, double distanceFromBottom)
    {
        _transcriptWindow.UpdatePinnedState(isPinnedToBottom, distanceFromBottom, "scroll-state");
    }

    internal bool EnsureLatestTranscriptMounted()
    {
        var changed = _transcriptWindow.EnsureLatestMounted("user-sent");
        if (changed && ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
        return changed;
    }

    internal void RecordTranscriptScrollCompensation(string reason, double beforeOffset, double afterOffset)
    {
        _transcriptWindow.RecordScrollCompensation(reason, beforeOffset, afterOffset);
        if (ShowTranscriptDiagnostics)
            OnPropertyChanged(nameof(TranscriptDiagnosticsText));
    }

    internal TranscriptWindowDiagnosticsSnapshot CaptureTranscriptDiagnostics() => _transcriptWindow.CaptureSnapshot();

    private List<Skill> ResolveSkillsByIds(IReadOnlyCollection<Guid> skillIds)
    {
        if (skillIds.Count == 0)
            return [];

        var skillsById = _dataStore.Data.Skills.ToDictionary(s => s.Id);
        var resolvedSkills = new List<Skill>(skillIds.Count);
        foreach (var skillId in skillIds)
        {
            if (skillsById.TryGetValue(skillId, out var skill))
                resolvedSkills.Add(skill);
        }

        return resolvedSkills;
    }

    private List<SkillReference> BuildSkillReferences(IReadOnlyCollection<Guid> skillIds)
    {
        return ResolveSkillsByIds(skillIds)
            .Select(static s => new SkillReference
            {
                Name = s.Name,
                Glyph = s.IconGlyph,
                Description = s.Description
            })
            .ToList();
    }

    private static async Task<string?> LoadWorkspaceAgentContentAsync(string workDir, string? workspaceAgentName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceAgentName))
            return null;

        var agentFile = Path.Combine(workDir, ".github", "agents", workspaceAgentName + ".md");
        if (!File.Exists(agentFile))
            return null;

        try
        {
            return await File.ReadAllTextAsync(agentFile, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> SyncActiveSkillDirectoryAsync(CancellationToken ct)
    {
        if (ActiveSkillIds.Count == 0)
            return null;

        var activeSkillIds = ActiveSkillIds.ToList();
        return await _dataStore.SyncSkillFilesForIdsAsync(activeSkillIds, ct);
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
    private async Task<bool> EnsureSessionAsync(
        Chat chat,
        CancellationToken ct,
        bool allowCreateFallback = true)
    {
        var allSkills = _dataStore.Data.Skills;
        var activeSkills = ResolveSkillsByIds(ActiveSkillIds);
        var memories = _dataStore.Data.Memories;
        var project = chat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, ActiveAgent, project, allSkills, activeSkills, memories);
        var workDir = GetEffectiveWorkingDirectory();

        // If a workspace agent (from .github/agents/) is selected, load its content and append to system prompt
        var workspaceAgentName = chat.SdkAgentName ?? SelectedSdkAgentName;
        var workspaceAgentContentTask = ActiveAgent is null
            ? LoadWorkspaceAgentContentAsync(workDir, workspaceAgentName, ct)
            : Task.FromResult<string?>(null);
        var skillDirTask = SyncActiveSkillDirectoryAsync(ct);
        var mcpServersTask = BuildMcpServersAsync(workDir, ct);

        var customAgents = BuildCustomAgents();
        var customTools = BuildCustomTools(chat.Id);
        var agentContent = await workspaceAgentContentTask;
        if (!string.IsNullOrWhiteSpace(agentContent))
            systemPrompt = (systemPrompt ?? "") + "\n\n--- Active Agent: " + workspaceAgentName + " ---\n" + agentContent;

        var skillDirs = new List<string>();
        var dir = await skillDirTask;
        if (!string.IsNullOrWhiteSpace(dir))
            skillDirs.Add(dir);

        var mcpServers = await mcpServersTask;
        var reasoningEffort = _dataStore.Data.Settings.ReasoningEffort;
        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;

        // Native user input handler — wired to the existing question card UI.
        // Capture chat.Id in the closure so questions always target the owning chat,
        // even if the user switches to a different chat while this session is active.
        var inputHandlerChatId = chat.Id;
        UserInputHandler userInputHandler = async (request, invocation) =>
        {
            var questionId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingQuestions[questionId] = tcs;

            var optionsStr = request.Choices is { Count: > 0 } ? string.Join(",", request.Choices) : "";
            var freeText = request.AllowFreeform ?? true;

            Dispatcher.UIThread.Post(() =>
            {
                if (CurrentChat?.Id != inputHandlerChatId) return;
                _transcriptBuilder.AddQuestionToTranscript(questionId, request.Question, optionsStr, freeText);
                QuestionAsked?.Invoke(questionId, request.Question, optionsStr, freeText);
                ScrollToEndRequested?.Invoke();

                // Store questionId on the tool message so it can be recovered during rebuild
                var owningChat = _dataStore.Data.Chats.Find(c => c.Id == inputHandlerChatId);
                if (owningChat is not null)
                {
                    var toolMsg = owningChat.Messages.LastOrDefault(m =>
                        m.ToolName == "ask_question" && m.ToolStatus == "InProgress" && m.QuestionId is null);
                    if (toolMsg is not null)
                        toolMsg.QuestionId = questionId;
                }
            });

            try
            {
                var answer = await tcs.Task;
                return new GitHub.Copilot.SDK.UserInputResponse { Answer = answer, WasFreeform = true };
            }
            finally
            {
                _pendingQuestions.Remove(questionId);
            }
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
                _dataStore.MarkChatChanged(chat);
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
                if (!await _copilotService.IsHealthyAsync(TimeSpan.FromSeconds(2)))
                    await TryReconnectCopilotAsync(ct);

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
            await SaveCurrentChatAsync(touchIndex: true);
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
        var previousChat = CurrentChat?.Id != chat.Id ? CurrentChat : null;

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

            // Reuse the cached session only while the current CLI connection can still talk to it.
            // Inactive chats are evicted separately, and AutoRestart can still leave stale session handles.
            _activeSession = await TryGetReusableCachedSessionAsync(chat, loadToken);

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
            HasUsedBrowser = runtime.HasUsedBrowser;

            _isBulkLoadingMessages = true;
            try
            {
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
                if (previousChat is not null)
                {
                    var previousRuntime = GetOrCreateRuntimeState(previousChat.Id);
                    if (!previousRuntime.IsBusy && !previousRuntime.IsStreaming)
                        QueueSaveChat(previousChat, saveIndex: false, releaseIfInactive: true);
                }

                // If this chat has an active browser, show its panel (after CurrentChat is set
                // so ActiveChatId is already updated when the MainWindow handler runs)
                if (runtime.HasUsedBrowser && _chatBrowserServices.ContainsKey(chat.Id))
                    BrowserShowRequested?.Invoke(chat.Id);

                // Rebuild transcript items from the fully loaded message list before
                // re-enabling live incremental transcript processing.
                RebuildTranscript();
            }
            finally
            {
                _isBulkLoadingMessages = false;
            }

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

            // Restore per-chat model selection (falls back to global preferred model)
            if (!string.IsNullOrWhiteSpace(chat.LastModelUsed))
            {
                if (!AvailableModels.Contains(chat.LastModelUsed))
                    AvailableModels.Add(chat.LastModelUsed);
                SelectedModel = chat.LastModelUsed;
            }
            else
            {
                SelectedModel = _dataStore.Data.Settings.PreferredModel;
            }

            // Git status can be slow in large repos/worktrees. Do not keep the chat
            // loading overlay up after the transcript is already interactive.
            _ = RefreshCodingProjectState();

            // Refresh SDK agents if we have a session
            if (_activeSession is not null)
            {
                _ = PopulateFromSessionAsync();
                _ = RefreshPlanAsync(chat);
            }
            else if (!string.IsNullOrWhiteSpace(chat.PlanContent))
            {
                // Restore plan from persisted data (no active session, e.g. after restart)
                HasPlan = true;
                PlanContent = chat.PlanContent;
                _transcriptBuilder.AppendPlanCardToLastTurn("Plan", () => PlanShowRequested?.Invoke());
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
                _transcriptBuilder.AppendPlanCardToLastTurn("Plan", () => PlanShowRequested?.Invoke());
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

        // Detach from the visible chat; inactive chat state is released later when it is safe.
        _activeSession = null;

        Messages.Clear();
        TranscriptTurns.Clear();
        _transcriptBuilder.ResetState();
        CurrentChat = null;
        _ = RefreshCodingProjectState();
        IsBusy = false;
        IsStreaming = false;
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
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

        CancelPendingQuestions(CurrentChat);
        ReleaseSessionResources(chatId, cancelActiveRequest: true, deleteServerSession: true);
        RemoveSuggestionTracking(chatId);
        CurrentChat.CopilotSessionId = null;
        _dataStore.MarkChatChanged(CurrentChat);
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
        var createdChat = false;

        // Create chat if needed
        if (CurrentChat is null)
        {
            // Lazily create the worktree now if worktree mode was toggled on.
            // Use transcript builder directly — avoid toggling IsBusy which triggers
            // RefreshCodingProjectState and resets worktree state before chat exists.
            if (IsWorktreeMode && WorktreePath is null)
            {
                var projectDir = GetProjectWorkingDirectory();
                if (GitService.IsGitRepo(projectDir))
                {
                    _transcriptBuilder.ShowTypingIndicator(Loc.Status_CreatingWorktree);
                    try
                    {
                        var chatId = Guid.NewGuid().ToString("N")[..8];
                        var branchName = $"lumi/{chatId}";
                        var path = await GitService.CreateWorktreeAsync(projectDir, branchName);

                        if (path is not null)
                            WorktreePath = path;
                        else
                            IsWorktreeMode = false;
                    }
                    catch
                    {
                        IsWorktreeMode = false;
                    }
                    finally
                    {
                        _transcriptBuilder.HideTypingIndicator();
                    }
                }
                else
                {
                    IsWorktreeMode = false;
                }
            }

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
            createdChat = true;
            if (_dataStore.Data.Settings.AutoGenerateTitles)
                _ = GenerateTitleForChatAsync(chat, prompt);
            _ = RefreshCodingProjectState();
            ChatUpdated?.Invoke();
        }

        // Add user message
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = _dataStore.Data.Settings.UserName ?? Loc.Author_You,
            Attachments = attachments?.Select(a => a.Path).ToList() ?? [],
            ActiveSkills = BuildSkillReferences(ActiveSkillIds)
        };
        CurrentChat.Messages.Add(userMsg);
        Messages.Add(new ChatMessageViewModel(userMsg));
        QueueSaveChat(CurrentChat, saveIndex: true, touchIndex: true);
        UserMessageSent?.Invoke();

        CancellationTokenSource? cts = null;
        MessageOptions? sendOptions = null;
        var localUserMessageCount = 0;
        var localAssistantMessageCount = 0;
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
                var ok = await EnsureSessionAsync(
                    CurrentChat,
                    cts.Token,
                    allowCreateFallback: AllowCreateSessionForSend(createdChat));
                if (!ok)
                {
                    HandleSendError(
                        new InvalidOperationException(Loc.Status_OriginalSessionUnavailable),
                        wasCancelledByUser: false,
                        overrideMessage: Loc.Status_OriginalSessionUnavailable);
                    return;
                }

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
            localUserMessageCount = CurrentChat.Messages.Count(m => m.Role == "user");
            localAssistantMessageCount = CountCompletedAssistantMessages(CurrentChat);

            // Inject newly activated skills as context in the message (explicit activation in existing chat)
            if (_pendingSkillInjections.Count > 0)
            {
                var injectedSkills = ResolveSkillsByIds(_pendingSkillInjections);
                _pendingSkillInjections.Clear();

                if (injectedSkills.Count > 0)
                {
                    var skillContext = new StringBuilder("\n\n--- Activated Skills (apply these to help with the request) ---\n");
                    foreach (var skill in injectedSkills)
                    {
                        skillContext.Append("\n### ")
                            .Append(skill.Name)
                            .Append('\n')
                            .Append(skill.Content)
                            .Append('\n');
                    }
                    sendOptions.Prompt += skillContext.ToString();
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

            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                _activeSession!,
                localUserMessageCount,
                cts.Token);
            PreparePendingTurnTracking(
                CurrentChat,
                expectedSessionUserMessageCount,
                localAssistantMessageCount);
            await _activeSession!.SendAsync(sendOptions, cts.Token);
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex) && CurrentChat is not null && cts is not null && sendOptions is not null)
        {
            // Stale session cache — evict and resume
            try
            {
                StatusText = Loc.Status_Reconnecting;
                InvalidateLocalSessionCache(CurrentChat);
                var ok = await EnsureSessionAsync(CurrentChat, cts.Token, allowCreateFallback: false);
                if (!ok)
                {
                    ClearPendingTurnTracking(CurrentChat!.Id);
                    HandleSendError(
                        new InvalidOperationException(Loc.Status_OriginalSessionUnavailable),
                        wasCancelledByUser: false,
                        overrideMessage: Loc.Status_OriginalSessionUnavailable);
                    return;
                }
                var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                    _activeSession!,
                    localUserMessageCount,
                    cts.Token);
                PreparePendingTurnTracking(
                    CurrentChat,
                    expectedSessionUserMessageCount,
                    localAssistantMessageCount);
                await _activeSession!.SendAsync(sendOptions, cts.Token);
            }
            catch (Exception retryEx)
            {
                if (CurrentChat is not null && IsCopilotTransportError(retryEx))
                {
                    var recovery = await TryRecoverTransportSendAsync(CurrentChat, sendOptions);
                    if (recovery.Recovered)
                        return;

                    ClearPendingTurnTracking(CurrentChat.Id);
                    HandleSendError(retryEx, cts.IsCancellationRequested, recovery.FailureMessage);
                    return;
                }

                ClearPendingTurnTracking(CurrentChat!.Id);
                HandleSendError(
                    retryEx,
                    cts.IsCancellationRequested,
                    IsCopilotTransportError(retryEx) ? Loc.Status_ConnectionRecoveryFailed : null);
            }
        }
        catch (Exception ex) when (CurrentChat is not null && sendOptions is not null && IsCopilotTransportError(ex))
        {
            var recovery = await TryRecoverTransportSendAsync(CurrentChat, sendOptions);
            if (recovery.Recovered)
                    return;

            ClearPendingTurnTracking(CurrentChat.Id);
            HandleSendError(ex, cts?.IsCancellationRequested == true, recovery.FailureMessage);
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
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat!.Id);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected, no error to surface
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat!.Id);
        }
        catch (Exception ex) when (cts is not null)
        {
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat!.Id);
            HandleSendError(ex, cts.IsCancellationRequested);
        }
    }

    private async Task<bool> TryReconnectCopilotAsync(CancellationToken ct)
    {
        try
        {
            await _copilotService.ForceReconnectAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AllowCreateSessionForSend(bool chatWasCreatedThisTurn)
        => chatWasCreatedThisTurn;

    private async Task<(CancellationTokenSource? TurnCts, string? FailureMessage)> TryRecoverTransportConnectionAsync(Chat chat)
    {
        try
        {
            using var reconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var runtime = GetOrCreateRuntimeState(chat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Reconnecting;
            if (CurrentChat?.Id == chat.Id)
            {
                IsBusy = true;
                IsStreaming = true;
                StatusText = runtime.StatusText;
            }

            if (!await TryReconnectCopilotAsync(reconnectCts.Token))
                return (null, Loc.Status_ConnectionRecoveryFailed);

            if (!await EnsureSessionAsync(chat, reconnectCts.Token, allowCreateFallback: false))
                return (null, Loc.Status_OriginalSessionUnavailable);

            var recoveredTurnCts = new CancellationTokenSource();
            _ctsSources[chat.Id] = recoveredTurnCts;
            return (recoveredTurnCts, null);
        }
        catch
        {
            return (null, Loc.Status_ConnectionRecoveryFailed);
        }
    }

    private async Task<(bool Recovered, string? FailureMessage)> TryRecoverTransportSendAsync(
        Chat chat,
        MessageOptions sendOptions)
    {
        var pendingRuntime = GetOrCreateRuntimeState(chat.Id);
        int pendingSessionUserMessageCount;
        int pendingAssistantCount;
        lock (pendingRuntime)
        {
            pendingSessionUserMessageCount = pendingRuntime.PendingSessionUserMessageCount;
            pendingAssistantCount = pendingRuntime.PendingAssistantMessageCount;
        }

        var (recoveredTurnCts, failureMessage) = await TryRecoverTransportConnectionAsync(chat);
        if (recoveredTurnCts is null || _activeSession is null)
            return (false, failureMessage ?? Loc.Status_ConnectionRecoveryFailed);

        var recoveredAnalysis = await AnalyzePendingTurnRecoveryAsync(
            _activeSession,
            pendingSessionUserMessageCount,
            recoveredTurnCts.Token);
        if (!recoveredAnalysis.UserMessageObserved)
        {
            pendingRuntime.IsBusy = true;
            pendingRuntime.IsStreaming = true;
            pendingRuntime.StatusText = Loc.Status_ConnectionRecoveredRetry;
            if (CurrentChat?.Id == chat.Id)
            {
                IsBusy = true;
                IsStreaming = true;
                StatusText = pendingRuntime.StatusText;
            }

            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                _activeSession,
                pendingSessionUserMessageCount,
                recoveredTurnCts.Token);
            SetPendingSessionUserMessageCount(chat.Id, expectedSessionUserMessageCount);
            await _activeSession.SendAsync(sendOptions.Clone(), recoveredTurnCts.Token);
            return (true, null);
        }

        if (await ApplyRecoveredTurnStateAsync(chat, recoveredAnalysis))
        {
            return (true, null);
        }

        if (CountCompletedAssistantMessages(chat) > pendingAssistantCount)
            return (true, null);

        var recoveredByWaiting = await WaitForRecoveredTurnAsync(
            chat,
            pendingSessionUserMessageCount,
            pendingAssistantCount,
            recoveredTurnCts.Token);
        return (recoveredByWaiting, recoveredByWaiting ? null : Loc.Status_ConnectionRecoveryFailed);
    }

    private static int CountCompletedAssistantMessages(Chat chat)
        => chat.Messages.Count(static m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));

    private async Task<IReadOnlyList<SessionEvent>?> TryGetSessionEventsAsync(CopilotSession session, CancellationToken ct)
    {
        try
        {
            return await _copilotService.GetSessionEventsAsync(session, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<PendingTurnRecoveryAnalysis> AnalyzePendingTurnRecoveryAsync(
        CopilotSession session,
        int expectedSessionUserMessageCount,
        CancellationToken ct)
    {
        PendingTurnRecoveryAnalysis? liveAnalysis = null;
        var liveEvents = await TryGetSessionEventsAsync(session, ct);
        if (liveEvents is not null)
            liveAnalysis = PendingTurnRecoveryAnalyzer.Analyze(liveEvents, expectedSessionUserMessageCount);

        var persistedAnalysis = await PendingTurnRecoveryAnalyzer.TryAnalyzeSessionLogAsync(
            session.SessionId,
            expectedSessionUserMessageCount,
            ct);

        return PendingTurnRecoveryAnalyzer.Merge(liveAnalysis, persistedAnalysis);
    }

    private async Task<int> CaptureExpectedSessionUserMessageCountAsync(
        CopilotSession session,
        int fallbackExpectedSessionUserMessageCount,
        CancellationToken ct)
    {
        var observedSessionUserMessageCount = 0;
        var foundObservedCount = false;

        var persistedUserMessageCount = await PendingTurnRecoveryAnalyzer.TryCountSessionUserMessagesAsync(
            session.SessionId,
            ct);
        if (persistedUserMessageCount.HasValue)
        {
            observedSessionUserMessageCount = persistedUserMessageCount.Value;
            foundObservedCount = true;
        }

        var liveEvents = await TryGetSessionEventsAsync(session, ct);
        if (liveEvents is not null)
        {
            var liveUserMessageCount = PendingTurnRecoveryAnalyzer.CountUserMessages(liveEvents);
            if (!foundObservedCount || liveUserMessageCount > observedSessionUserMessageCount)
            {
                observedSessionUserMessageCount = liveUserMessageCount;
                foundObservedCount = true;
            }
        }

        return foundObservedCount
            ? observedSessionUserMessageCount + 1
            : Math.Max(1, fallbackExpectedSessionUserMessageCount);
    }

    private bool SyncRecoveredAssistantMessages(Chat chat, IReadOnlyList<RecoveredAssistantMessage> recoveredAssistantMessages)
    {
        if (recoveredAssistantMessages.Count == 0)
            return false;

        var author = ActiveAgent?.Name ?? Loc.Author_Lumi;
        foreach (var assistantMessage in recoveredAssistantMessages)
        {
            var recoveredMessage = new ChatMessage
            {
                Role = "assistant",
                Author = author,
                Content = assistantMessage.Content,
                IsStreaming = false,
                Model = SelectedModel
            };
            chat.Messages.Add(recoveredMessage);

            if (CurrentChat?.Id == chat.Id)
                Messages.Add(new ChatMessageViewModel(recoveredMessage));
        }

        var runtime = GetOrCreateRuntimeState(chat.Id);
        runtime.IsBusy = false;
        runtime.IsStreaming = false;
        runtime.StatusText = "";
        if (CurrentChat?.Id == chat.Id)
        {
            IsBusy = false;
            IsStreaming = false;
            StatusText = runtime.StatusText;
            ScrollToEndRequested?.Invoke();
        }

        QueueSaveChat(chat, saveIndex: true, touchIndex: true);
        return true;
    }

    private async Task<bool> WaitForRecoveredTurnAsync(
        Chat chat,
        int expectedSessionUserMessageCount,
        int assistantCountBeforeRecovery,
        CancellationToken ct)
    {
        if (_activeSession is null)
            return false;

        var sawRecoveredTurnActivity = false;
        var turnActivity = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = _activeSession.On(evt =>
        {
            switch (evt)
            {
                case AssistantTurnStartEvent:
                case AssistantReasoningEvent:
                case AssistantReasoningDeltaEvent:
                case AssistantMessageDeltaEvent:
                case AssistantMessageEvent:
                case ToolExecutionStartEvent:
                case ToolExecutionPartialResultEvent:
                case ToolExecutionProgressEvent:
                case ToolExecutionCompleteEvent:
                case AssistantTurnEndEvent:
                    sawRecoveredTurnActivity = true;
                    turnActivity.TrySetResult(true);
                    break;
                case SessionIdleEvent:
                    turnActivity.TrySetResult(true);
                    break;
                case SessionErrorEvent err:
                    turnActivity.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(8));
            await turnActivity.Task.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        var recoveredAnalysis = await AnalyzePendingTurnRecoveryAsync(
            _activeSession,
            expectedSessionUserMessageCount,
            ct);
        if (await ApplyRecoveredTurnStateAsync(chat, recoveredAnalysis))
            return true;

        return sawRecoveredTurnActivity || CountCompletedAssistantMessages(chat) > assistantCountBeforeRecovery;
    }

    private bool WasCancelledByUser(Guid? chatId)
        => chatId.HasValue && _ctsSources.GetValueOrDefault(chatId.Value)?.IsCancellationRequested == true;

    /// <summary>Returns a cached session only when it is still usable on the current CLI connection.</summary>
    private async Task<CopilotSession?> TryGetReusableCachedSessionAsync(Chat chat, CancellationToken ct)
    {
        if (!_sessionCache.TryGetValue(chat.Id, out var cachedSession))
            return null;

        if (!await _copilotService.IsHealthyAsync(TimeSpan.FromSeconds(2)))
        {
            InvalidateLocalSessionCache(chat);
            return null;
        }

        try
        {
            await _copilotService.ReadSessionPlanAsync(cachedSession, ct);
            return cachedSession;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsSessionNotFoundError(ex))
        {
            InvalidateLocalSessionCache(chat);
            return null;
        }
        catch (Exception ex) when (IsCopilotTransportError(ex))
        {
            InvalidateLocalSessionCache(chat);
            return null;
        }
        catch
        {
            // Non-session-specific plan RPC failures should not discard a healthy cached session.
            return cachedSession;
        }
    }

    /// <summary>Detects a stale cached session (the session ID is unknown to the current CLI process).</summary>
    private static bool IsSessionNotFoundError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("Session not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCopilotTransportError(Exception ex)
    {
        var message = FlattenExceptionMessages(ex);
        return message.Contains("JSON-RPC", StringComparison.OrdinalIgnoreCase)
               || message.Contains("remote party was lost", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection lost", StringComparison.OrdinalIgnoreCase)
               || message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
               || message.Contains("pipe is being closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("stream closed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("transport connection", StringComparison.OrdinalIgnoreCase);
    }

    private static string FlattenExceptionMessages(Exception ex)
    {
        var builder = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (builder.Length > 0)
                builder.Append(" → ");

            builder.Append(current.Message);
        }

        return builder.ToString();
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
    private void HandleSendError(Exception ex, bool wasCancelledByUser, string? overrideMessage = null)
    {
        if (ex is OperationCanceledException && wasCancelledByUser)
            return; // Cancelled by StopGeneration — expected

        if (CurrentChat is not null)
            ClearPendingTurnTracking(CurrentChat.Id);

        var message = overrideMessage ?? FlattenExceptionMessages(ex);
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
        ClearPendingTurnTracking(chatId);

        // Only update UI properties if this is still the displayed chat
        if (CurrentChat?.Id == chatId)
        {
            IsBusy = false;
            IsStreaming = false;
            StatusText = Loc.Status_Stopped;
        }
    }

    private async Task SaveCurrentChatAsync(bool saveIndex = true, bool touchIndex = false)
    {
        if (CurrentChat is null) return;
        if (touchIndex)
            _dataStore.MarkChatChanged(CurrentChat);
        await SaveChatAsync(CurrentChat, saveIndex);
    }

    private void QueueSaveChat(Chat chat, bool saveIndex, bool releaseIfInactive = false, bool touchIndex = false)
    {
        if (touchIndex)
            _dataStore.MarkChatChanged(chat);
        _ = SaveChatAsync(chat, saveIndex, releaseIfInactive);
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

    private async Task GenerateTitleForChatAsync(Chat chat, string userMessage)
    {
        try
        {
            var title = await _copilotService.GenerateTitleAsync(userMessage);
            if (string.IsNullOrWhiteSpace(title)) return;

            Dispatcher.UIThread.Post(() =>
            {
                chat.Title = title;
                _dataStore.MarkChatChanged(chat);
                if (CurrentChat?.Id == chat.Id)
                    OnPropertyChanged(nameof(CurrentChatTitle));
                if (_dataStore.Data.Settings.AutoSaveChats)
                    _ = SaveIndexAsync();
                ChatTitleChanged?.Invoke(chat.Id, chat.Title);
            });
        }
        catch { /* best effort — title stays as truncated prompt */ }
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

    private async Task SaveChatAsync(Chat chat, bool saveIndex, bool releaseIfInactive = false, CancellationToken cancellationToken = default)
    {
        var canEvictMessages = false;
        try
        {
            if (_dataStore.Data.Settings.AutoSaveChats)
            {
                await _dataStore.SaveChatAsync(chat, cancellationToken);
                if (saveIndex)
                    await _dataStore.SaveAsync(cancellationToken);
                canEvictMessages = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Avoid surfacing persistence races/IO failures as hard UI errors.
        }

        if (releaseIfInactive)
        {
            if (Dispatcher.UIThread.CheckAccess())
                ReleaseInactiveChatState(chat, canEvictMessages);
            else
                Dispatcher.UIThread.Post(() => ReleaseInactiveChatState(chat, canEvictMessages));
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

    /// <summary>Formats a model ID into a short display name (e.g. "claude-sonnet-4.5" → "Sonnet 4.5").</summary>
    internal static string? FormatModelDisplay(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var m = modelId.ToLowerInvariant();

        // Known model families — extract tier + version
        string? tier = null;
        if (m.Contains("opus"))        tier = "Opus";
        else if (m.Contains("sonnet")) tier = "Sonnet";
        else if (m.Contains("haiku"))  tier = "Haiku";
        else if (m.Contains("gpt"))    tier = "GPT";
        else if (m.Contains("gemini")) tier = "Gemini";

        if (tier is null) return modelId; // Unknown family, show raw ID

        // Reuse the same generated regex as ScoreModel
        var versionMatch = VersionRegex().Match(m);
        var version = versionMatch.Success ? versionMatch.Value : "";

        var suffix = "";
        if (m.Contains("codex")) suffix = " Codex";
        else if (m.Contains("mini")) suffix = " Mini";
        else if (m.Contains("pro"))  suffix = " Pro";
        if (m.Contains("preview")) suffix += " Preview";

        return $"{tier} {version}{suffix}".Trim();
    }

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
        QueueSaveChat(CurrentChat, saveIndex: true, touchIndex: true);
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

        MessageOptions? resendOptions = null;
        var localUserMessageCount = 0;
        var localAssistantMessageCount = 0;
        try
        {
            // Cancel any previous in-flight request for this chat
            var chatId = CurrentChat.Id;
            if (_ctsSources.TryGetValue(chatId, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
                _ctsSources.Remove(chatId);

                if (_sessionCache.TryGetValue(chatId, out var cachedSession))
                {
                    try { await cachedSession.AbortAsync(); }
                    catch { /* best-effort */ }
                }
            }

            var needsSessionSetup = _activeSession?.SessionId != CurrentChat.CopilotSessionId
                                    || CurrentChat.CopilotSessionId is null;

            // Editing must not keep old server-side context. Recreate session first.
            // Must happen BEFORE creating the new CTS, because InvalidateCurrentSession
            // calls ReleaseSessionResources which disposes any CTS still in _ctsSources.
            if (wasEdited)
            {
                InvalidateCurrentSession();
                needsSessionSetup = true;
            }

            var cts = new CancellationTokenSource();
            _ctsSources[chatId] = cts;

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

            resendOptions = new MessageOptions { Prompt = resendPrompt };
            localUserMessageCount = CurrentChat.Messages.Count(m => m.Role == "user");
            localAssistantMessageCount = CountCompletedAssistantMessages(CurrentChat);
            var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                _activeSession!,
                localUserMessageCount,
                cts.Token);
            PreparePendingTurnTracking(
                CurrentChat,
                expectedSessionUserMessageCount,
                localAssistantMessageCount);
            await _activeSession!.SendAsync(resendOptions, cts.Token);
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
                var ok = await EnsureSessionAsync(CurrentChat, cts.Token, allowCreateFallback: wasEdited);
                if (!ok)
                {
                    ClearPendingTurnTracking(CurrentChat.Id);
                    HandleSendError(
                        new InvalidOperationException(Loc.Status_OriginalSessionUnavailable),
                        wasCancelledByUser: false,
                        overrideMessage: Loc.Status_OriginalSessionUnavailable);
                    return;
                }
                var resendPrompt2 = wasEdited
                    ? BuildEditedReplayPrompt(retainedContext, prompt)
                    : prompt;
                resendOptions = new MessageOptions { Prompt = resendPrompt2 };
                var expectedSessionUserMessageCount = await CaptureExpectedSessionUserMessageCountAsync(
                    _activeSession!,
                    localUserMessageCount,
                    cts.Token);
                PreparePendingTurnTracking(
                    CurrentChat,
                    expectedSessionUserMessageCount,
                    localAssistantMessageCount);
                await _activeSession!.SendAsync(resendOptions, cts.Token);
            }
            catch (Exception retryEx)
            {
                if (CurrentChat is not null && resendOptions is not null && IsCopilotTransportError(retryEx))
                {
                    var recovery = await TryRecoverTransportSendAsync(CurrentChat, resendOptions);
                    if (recovery.Recovered)
                        return;

                    ClearPendingTurnTracking(CurrentChat.Id);
                    HandleSendError(retryEx, WasCancelledByUser(CurrentChat?.Id), recovery.FailureMessage);
                    return;
                }

                ClearPendingTurnTracking(CurrentChat!.Id);
                HandleSendError(retryEx, WasCancelledByUser(CurrentChat?.Id));
            }
        }
        catch (Exception ex) when (CurrentChat is not null && resendOptions is not null && IsCopilotTransportError(ex))
        {
            var recovery = await TryRecoverTransportSendAsync(CurrentChat, resendOptions);
            if (recovery.Recovered)
                return;

            ClearPendingTurnTracking(CurrentChat.Id);
            HandleSendError(ex, WasCancelledByUser(CurrentChat?.Id), recovery.FailureMessage);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by StopGeneration — expected
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat.Id);
        }
        catch (Exception ex)
        {
            if (CurrentChat is not null)
                ClearPendingTurnTracking(CurrentChat.Id);
            HandleSendError(ex, WasCancelledByUser(CurrentChat?.Id));
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
    public string? ModelName => ChatViewModel.FormatModelDisplay(Message.Model);
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


