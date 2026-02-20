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
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _shownFileChips = new(StringComparer.OrdinalIgnoreCase);

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

    private sealed class ChatRuntimeState
    {
        public bool IsBusy { get; set; }
        public bool IsStreaming { get; set; }
        public string StatusText { get; set; } = "";
    }

    /// <summary>True while LoadChat is bulk-adding messages. The View skips CollectionChanged.Add during this.</summary>
    public bool IsLoadingChat { get; private set; }

    [ObservableProperty] private Chat? _currentChat;
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

    // Events for the view to react to
    public event Action? ScrollToEndRequested;
    public event Action? UserMessageSent;
    public event Action? ChatUpdated;
    public event Action<string>? FileCreatedByTool;
    public event Action? AgentChanged;

    public ChatViewModel(DataStore dataStore, CopilotService copilotService)
    {
        _dataStore = dataStore;
        _copilotService = copilotService;
        _selectedModel = dataStore.Data.Settings.PreferredModel;

        // Seed with preferred model so the ComboBox has an initial selection
        if (!string.IsNullOrWhiteSpace(_selectedModel))
            AvailableModels.Add(_selectedModel);
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
                        var displayName = FormatToolDisplayName(toolStart.Data.ToolName, toolStart.Data.Arguments?.ToString());
                        runtime.StatusText = string.Format(Loc.Status_Running, displayName);
                        var toolMsg = new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolStart.Data.ToolCallId,
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

                case ToolExecutionCompleteEvent toolEnd:
                    Dispatcher.UIThread.Post(() =>
                    {
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
                            if (success)
                            {
                                var toolName = toolMsg.ToolName;
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
                        chat.UpdatedAt = DateTimeOffset.Now;
                        if (_dataStore.Data.Settings.AutoSaveChats)
                            _dataStore.Save();
                        ChatUpdated?.Invoke();
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
                    });
                    break;

                case SessionTitleChangedEvent title:
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_dataStore.Data.Settings.AutoGenerateTitles) return;
                        chat.Title = title.Data.Title;
                        chat.UpdatedAt = DateTimeOffset.Now;
                        if (_dataStore.Data.Settings.AutoSaveChats)
                            _dataStore.Save();
                        ChatUpdated?.Invoke();
                    });
                    break;

                case SessionErrorEvent err:
                    Dispatcher.UIThread.Post(() =>
                    {
                        runtime.IsBusy = false;
                        runtime.IsStreaming = false;
                        runtime.StatusText = string.Format(Loc.Status_Error, err.Data.Message);
                        if (_activeSession == session)
                        {
                            StatusText = runtime.StatusText;
                            IsBusy = runtime.IsBusy;
                            IsStreaming = runtime.IsStreaming;
                        }
                    });
                    break;
            }
        });
    }

    /// <summary>Cleans up session resources for a chat (e.g., on delete).</summary>
    public void CleanupSession(Guid chatId)
    {
        if (_sessionSubs.TryGetValue(chatId, out var sub))
        {
            sub.Dispose();
            _sessionSubs.Remove(chatId);
        }
        _sessionCache.Remove(chatId);
        _inProgressMessages.Remove(chatId);
        _runtimeStates.Remove(chatId);
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

    public void LoadChat(Chat chat)
    {
        // Set the active session (don't dispose anything — background sessions keep running)
        _sessionCache.TryGetValue(chat.Id, out var cachedSession);
        _activeSession = cachedSession;

        // Restore real runtime state for this session/chat
        var runtime = GetOrCreateRuntimeState(chat.Id);
        IsBusy = runtime.IsBusy;
        IsStreaming = runtime.IsStreaming;
        StatusText = runtime.StatusText;

        IsLoadingChat = true;
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
        foreach (var skillId in chat.ActiveSkillIds)
        {
            var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Id == skillId);
            if (skill is not null)
            {
                ActiveSkillIds.Add(skillId);
                ActiveSkillChips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
            }
        }

        // Restore active agent from chat
        if (chat.AgentId.HasValue)
        {
            var agent = _dataStore.Data.Agents.FirstOrDefault(a => a.Id == chat.AgentId.Value);
            if (agent is not null)
                ActiveAgent = agent;
        }

        IsLoadingChat = false;
    }

    public void ClearChat()
    {
        // Detach from current chat without destroying its session.
        // Sessions are cleaned only when a chat is deleted via CleanupSession(chatId).
        _activeSession = null;

        Messages.Clear();
        CurrentChat = null;
        IsBusy = false;
        IsStreaming = false;
        _shownFileChips.Clear();
        ActiveSkillIds.Clear();
        ActiveSkillChips.Clear();
        _pendingProjectId = null;
        StatusText = "";
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
            SaveChat();
            ChatUpdated?.Invoke();
        }

        // Add user message
        var userMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = _dataStore.Data.Settings.UserName ?? Loc.Author_You,
            Attachments = attachments?.Select(a => a.Path).ToList() ?? []
        };
        CurrentChat.Messages.Add(userMsg);
        Messages.Add(new ChatMessageViewModel(userMsg));
        SaveChat();
        UserMessageSent?.Invoke();

        // Build system prompt with active skills
        var allSkills = _dataStore.Data.Skills;
        var activeSkills = ActiveSkillIds.Count > 0
            ? allSkills.Where(s => ActiveSkillIds.Contains(s.Id)).ToList()
            : new List<Skill>();
        var memories = _dataStore.Data.Memories;
        var project = CurrentChat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == CurrentChat.ProjectId)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, ActiveAgent, project, allSkills, activeSkills, memories);

        // Build skill directories for SDK
        var skillDirs = new List<string>();
        if (ActiveSkillIds.Count > 0)
        {
            var dir = _dataStore.SyncSkillFilesForIds(ActiveSkillIds);
            skillDirs.Add(dir);
        }

        // Build custom agents for SDK (register all agents as subagents)
        var customAgents = BuildCustomAgents();

        // Build custom tools (memory + file announcement)
        var customTools = BuildCustomTools();

        try
        {
            _cts = new CancellationTokenSource();

            // Create or resume session
            var workDir = GetWorkingDirectory();
            if (CurrentChat.CopilotSessionId is null)
            {
                var session = await _copilotService.CreateSessionAsync(
                    systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools, _cts.Token);
                CurrentChat.CopilotSessionId = session.SessionId;
                _activeSession = session;
                SubscribeToSession(session, CurrentChat);
            }
            else if (_activeSession?.SessionId != CurrentChat.CopilotSessionId)
            {
                var session = await _copilotService.ResumeSessionAsync(
                    CurrentChat.CopilotSessionId, systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools, _cts.Token);
                _activeSession = session;
                SubscribeToSession(session, CurrentChat);
            }

            var sendOptions = new MessageOptions { Prompt = prompt };
            if (attachments is { Count: > 0 })
                sendOptions.Attachments = attachments.Cast<UserMessageDataAttachmentsItem>().ToList();

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            await _activeSession!.SendAsync(sendOptions, _cts.Token);
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
        _cts?.Cancel();
        if (_activeSession is not null)
            await _activeSession.AbortAsync();
        if (CurrentChat is not null)
        {
            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = false;
            runtime.IsStreaming = false;
            runtime.StatusText = Loc.Status_Stopped;
        }
        IsBusy = false;
        IsStreaming = false;
        StatusText = Loc.Status_Stopped;
    }

    private void SaveChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.UpdatedAt = DateTimeOffset.Now;
            if (_dataStore.Data.Settings.AutoSaveChats)
                _dataStore.Save();
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
            SaveChat();
        }
        AgentChanged?.Invoke();
    }

    /// <summary>Assigns a project to the current (or next) chat. Called when a project filter is active.</summary>
    public void SetProjectId(Guid projectId)
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ProjectId = projectId;
            SaveChat();
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
        SyncActiveSkillsToChat();
    }

    /// <summary>Registers a skill ID without adding a chip (composer already added it).</summary>
    public void RegisterSkillIdByName(string name)
    {
        var skill = _dataStore.Data.Skills.FirstOrDefault(s => s.Name == name);
        if (skill is null || ActiveSkillIds.Contains(skill.Id)) return;
        ActiveSkillIds.Add(skill.Id);
        SyncActiveSkillsToChat();
    }

    private void SyncActiveSkillsToChat()
    {
        if (CurrentChat is not null)
        {
            CurrentChat.ActiveSkillIds = new List<Guid>(ActiveSkillIds);
            SaveChat();
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

    private List<AIFunction> BuildCustomTools()
    {
        var tools = new List<AIFunction>();
        if (_dataStore.Data.Settings.EnableMemoryAutoSave)
            tools.AddRange(BuildMemoryTools());
        tools.Add(BuildAnnounceFileTool());
        tools.AddRange(BuildWebTools());
        return tools;
    }

    private static List<AIFunction> BuildWebTools()
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("The search query to look up on the web")] string query,
                 [Description("Number of results to return (default 5, max 10)")] int count = 5) =>
                {
                    count = Math.Clamp(count, 1, 10);
                    return WebSearchService.SearchAsync(query, count);
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

    partial void OnSelectedModelChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _dataStore.Data.Settings.PreferredModel = value;
        _dataStore.Save();
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

    private List<AIFunction> BuildMemoryTools()
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("Brief label for the memory (e.g. Birthday, Dog's name, Prefers dark mode)")] string key,
                 [Description("Full memory text with details")] string content,
                 [Description("Category: Personal, Preferences, Work, etc. Default: General")] string? category) =>
                {
                    category ??= "General";
                    var memories = _dataStore.Data.Memories;
                    var existing = memories.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        existing.Content = content;
                        existing.Category = category;
                        existing.UpdatedAt = DateTimeOffset.Now;
                    }
                    else
                    {
                        memories.Add(new Memory
                        {
                            Key = key,
                            Content = content,
                            Category = category,
                            SourceChatId = CurrentChat?.Id.ToString()
                        });
                    }
                    _dataStore.Save();
                    return $"Memory saved: {key}";
                },
                "save_memory",
                "Save or update a persistent memory about the user"),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to update")] string key,
                 [Description("New content text (optional)")] string? content,
                 [Description("New key if renaming (optional)")] string? newKey) =>
                {
                    var memory = _dataStore.Data.Memories
                        .FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (memory is null) return $"Memory not found: {key}";
                    if (content is not null) memory.Content = content;
                    if (newKey is not null) memory.Key = newKey;
                    memory.UpdatedAt = DateTimeOffset.Now;
                    _dataStore.Save();
                    return $"Memory updated: {memory.Key}";
                },
                "update_memory",
                "Update an existing memory's content or key"),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to remove")] string key) =>
                {
                    var memory = _dataStore.Data.Memories
                        .FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (memory is null) return $"Memory not found: {key}";
                    _dataStore.Data.Memories.Remove(memory);
                    _dataStore.Save();
                    return $"Memory deleted: {key}";
                },
                "delete_memory",
                "Remove a memory that is no longer relevant"),

            AIFunctionFactory.Create(
                ([Description("Key of the memory to retrieve full content for")] string key) =>
                {
                    var memory = _dataStore.Data.Memories
                        .FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (memory is null) return $"Memory not found: {key}";
                    return memory.Content;
                },
                "recall_memory",
                "Fetch the full content of a memory by its key"),
        ];
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
        if (CurrentChat is null || IsBusy) return;

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

        // Re-add the user message as a fresh entry
        var newUserMsg = new ChatMessage
        {
            Role = "user",
            Content = prompt,
            Author = userMessage.Author
        };
        CurrentChat.Messages.Add(newUserMsg);
        Messages.Add(new ChatMessageViewModel(newUserMsg));
        SaveChat();
        ScrollToEndRequested?.Invoke();

        // Resend
        if (!_copilotService.IsConnected)
        {
            StatusText = Loc.Status_NotConnected;
            try { await _copilotService.ConnectAsync(); }
            catch { StatusText = Loc.Status_ConnectionFailedShort; return; }
        }

        var allSkills = _dataStore.Data.Skills;
        var activeSkills = ActiveSkillIds.Count > 0
            ? allSkills.Where(s => ActiveSkillIds.Contains(s.Id)).ToList()
            : new List<Skill>();
        var memories = _dataStore.Data.Memories;
        var project = CurrentChat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == CurrentChat.ProjectId)
            : null;
        var systemPrompt = SystemPromptBuilder.Build(
            _dataStore.Data.Settings, ActiveAgent, project, allSkills, activeSkills, memories);

        var skillDirs = new List<string>();
        if (ActiveSkillIds.Count > 0)
        {
            var dir = _dataStore.SyncSkillFilesForIds(ActiveSkillIds);
            skillDirs.Add(dir);
        }
        var customAgents = BuildCustomAgents();
        var customTools = BuildCustomTools();

        try
        {
            _cts = new CancellationTokenSource();
            var workDir = GetWorkingDirectory();
            if (CurrentChat.CopilotSessionId is not null)
            {
                if (_activeSession?.SessionId == CurrentChat.CopilotSessionId)
                {
                    // Session is still active — just resend
                }
                else
                {
                    // Resume the saved session to restore server-side history
                    var session = await _copilotService.ResumeSessionAsync(
                        CurrentChat.CopilotSessionId, systemPrompt, SelectedModel, workDir,
                        skillDirs, customAgents, customTools, _cts.Token);
                    _activeSession = session;
                    SubscribeToSession(session, CurrentChat);
                }
            }
            else
            {
                // No session at all — create new (first-time edge case)
                var session = await _copilotService.CreateSessionAsync(
                    systemPrompt, SelectedModel, workDir, skillDirs, customAgents, customTools, _cts.Token);
                CurrentChat.CopilotSessionId = session.SessionId;
                _activeSession = session;
                SubscribeToSession(session, CurrentChat);
            }

            var runtime = GetOrCreateRuntimeState(CurrentChat.Id);
            runtime.IsBusy = true;
            runtime.IsStreaming = true;
            runtime.StatusText = Loc.Status_Thinking;
            IsBusy = runtime.IsBusy;
            IsStreaming = runtime.IsStreaming;
            StatusText = runtime.StatusText;

            await _activeSession!.SendAsync(new MessageOptions { Prompt = userMessage.Content }, _cts.Token);
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
        // If a project is selected and has a path, use it
        if (CurrentChat?.ProjectId.HasValue == true)
        {
            var project = _dataStore.Data.Projects
                .FirstOrDefault(p => p.Id == CurrentChat.ProjectId);
            if (project is not null && !string.IsNullOrWhiteSpace(project.Instructions))
            {
                // Check if instructions contain a directory hint (first line may be a path)
            }
        }

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
            "browser_navigate" or "navigate" => Loc.Tool_OpeningPage,
            "browser_click" or "click" => Loc.Tool_ClickingElement,
            "browser_type" or "type" => Loc.Tool_TypingText,
            "browser_snapshot" or "screenshot" => Loc.Tool_TakingScreenshot,
            "save_memory" => Loc.Tool_Remembering,
            "update_memory" => Loc.Tool_UpdatingMemory,
            "delete_memory" => Loc.Tool_Forgetting,
            "recall_memory" => Loc.Tool_Recalling,
            "announce_file" => Loc.Tool_SharingFile,
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
