using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Microsoft.Extensions.AI;

namespace Lumi.ViewModels;

/// <summary>
/// Tool building, browser/diff panel management, and MCP server configuration.
/// </summary>
public partial class ChatViewModel
{
    private List<CustomAgentConfig> BuildCustomAgents()
    {
        var agents = new List<CustomAgentConfig>();
        foreach (var agent in _dataStore.Data.Agents)
        {
            var agentConfig = new CustomAgentConfig
            {
                Name = agent.Name,
                DisplayName = agent.Name,
                Description = agent.Description,
                Prompt = agent.SystemPrompt,
            };

            if (agent.ToolNames.Count > 0)
                agentConfig.Tools = [.. ToolDisplayHelper.ToRuntimeToolNames(agent.ToolNames)];

            agents.Add(agentConfig);
        }
        return agents;
    }

    private static readonly HashSet<string> CodingToolNames = ["code_review", "generate_tests", "explain_code", "analyze_project"];
    private static readonly HashSet<string> BrowserToolNames =
    [
        ToolDisplayHelper.BrowserOpenToolName,
        ToolDisplayHelper.BrowserLookToolName,
        ToolDisplayHelper.BrowserFindToolName,
        ToolDisplayHelper.BrowserDoToolName,
        ToolDisplayHelper.BrowserJsToolName
    ];
    private static readonly HashSet<string> UIToolNames = ["ui_list_windows", "ui_inspect", "ui_find", "ui_click", "ui_type", "ui_press_keys", "ui_read"];
    private LumiFeatureManager? _lumiFeatureManager;
    private readonly HashSet<Guid> _pendingSessionInvalidations = [];
    private LumiFeatureManager FeatureManager => _lumiFeatureManager ??= new LumiFeatureManager(_dataStore);

    private ChatHistoryService? _chatHistoryService;
    private ChatHistoryService ChatHistory => _chatHistoryService ??= new ChatHistoryService(_dataStore, _globalSearchService);

    private CancellationToken GetCurrentCancellationToken()
    {
        if (CurrentChat is { } chat && _ctsSources.TryGetValue(chat.Id, out var cts))
        {
            try { return cts.Token; }
            catch (ObjectDisposedException)
            {
                _ctsSources.Remove(chat.Id);
            }
        }

        return CancellationToken.None;
    }

    private bool ActiveAgentAllows(HashSet<string> toolGroup)
        => AgentAllows(ActiveAgent, toolGroup);

    private static bool AgentAllows(LumiAgent? agent, HashSet<string> toolGroup)
    {
        // No active agent or no restrictions → allow everything
        if (agent is not { ToolNames.Count: > 0 }) return true;
        return ToolDisplayHelper.ToRuntimeToolNames(agent.ToolNames).Any(toolGroup.Contains);
    }

    private List<AIFunction> BuildCustomTools(
        Guid chatId,
        LumiAgent? activeAgent,
        ProjectContextCatalogSnapshot projectContextCatalog)
    {
        var tools = new List<AIFunction>();
        tools.AddRange(BuildMemoryTools());
        tools.Add(BuildAnnounceFileTool(chatId));
        tools.Add(BuildFetchSkillTool(projectContextCatalog));
        tools.Add(BuildAskQuestionTool(chatId));
        tools.AddRange(BuildLumiManagementTools(chatId));
        tools.AddRange(BuildWebTools());
        if (AgentAllows(activeAgent, BrowserToolNames))
            tools.AddRange(BuildBrowserTools(chatId));
        if (AgentAllows(activeAgent, CodingToolNames))
            tools.AddRange(_codingToolService.BuildCodingTools());
        if (OperatingSystem.IsWindows() && AgentAllows(activeAgent, UIToolNames))
            tools.AddRange(BuildUIAutomationTools());
        return tools;
    }

    private Dictionary<string, McpServerConfig> BuildMcpServers(
        string workDir,
        ProjectContextCatalogSnapshot projectContextCatalog,
        Chat chat,
        LumiAgent? activeAgent)
    {
        IReadOnlyCollection<string>? selectedServerNames = null;
        if (CurrentChat?.Id == chat.Id)
            selectedServerNames = ActiveMcpServerNames.ToList();

        var proxyRuntime = McpSessionPlanner.SelectProxyRuntime(_dataStore.Data.Settings, McpProxyRuntime.Shared);

        return McpSessionPlanner.Build(
            _dataStore.Data,
            workDir,
            projectContextCatalog,
            chat,
            selectedServerNames,
            activeAgent,
            proxyRuntime);
    }

    private List<AIFunction> BuildWebTools()
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("The full URL to fetch (must start with http:// or https://)")] string url) =>
                {
                    return WebFetchService.FetchAsync(url);
                },
                "lumi_fetch",
                "Fetch a webpage and return its text content. For long pages, returns a preview and saves the full content to a temp file you can read with Get-Content. If this fails, do NOT retry the same URL — try a different source instead."),
        ];
    }

    private List<AIFunction> BuildBrowserTools(Guid chatId)
    {
        return
        [
            AIFunctionFactory.Create(
                ([Description("The full URL to navigate to (e.g. https://mail.google.com)")] string url) =>
                {
                    var svc = GetOrCreateBrowserService(chatId);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (CurrentChat?.Id == chatId) HasUsedBrowser = true;
                        BrowserShowRequested?.Invoke(chatId);
                    });
                    return svc.OpenAndSnapshotAsync(url);
                },
                ToolDisplayHelper.BrowserOpenToolName,
                "Open a URL in the browser and return the page with numbered interactive elements and a text preview. The browser has persistent cookies/sessions — the user may already be logged in. Returns element numbers you can use with lumi_browser_do. If the URL triggers a file download (e.g. an export URL), the download is detected automatically and reported instead of a page snapshot."),

            AIFunctionFactory.Create(
                ([Description("Optional text filter to narrow elements (e.g. 'button', 'download', 'search', 'Export'). Omit to see all.")] string? filter = null) =>
                {
                    var svc = GetOrCreateBrowserService(chatId);
                    return svc.LookAsync(filter);
                },
                ToolDisplayHelper.BrowserLookToolName,
                "Returns the current page state: numbered interactive elements and text preview. Use filter to narrow results."),

            AIFunctionFactory.Create(
                ([Description("What to find on the page (e.g. 'download', 'export csv', 'save', 'submit').")]
                    string query,
                 [Description("Maximum matches to return (1-50).")]
                    int limit = 12) =>
                {
                    var svc = GetOrCreateBrowserService(chatId);
                    return svc.FindElementsAsync(query, limit, preferDialog: true);
                },
                ToolDisplayHelper.BrowserFindToolName,
                "Find and rank interactive elements by query. Matches against text, aria-label, tooltip, title, and href. Returns stable element indices usable with lumi_browser_do."),

            AIFunctionFactory.Create(
                ([Description("Action to perform: click, type, press, select, scroll, back, wait, download, clear, fill, read_form, upload, steps")] string action,
                 [Description("Target: element number from lumi_browser_open/lumi_browser_look (e.g. '3'), button text (e.g. 'Export'), CSS selector (e.g. '.btn'), key name (for press), direction (for scroll), or file pattern (for download). For upload: optional locator for the <input type=file> (CSS selector or the upload button/label text) — omit to use the page's only file input. Append ' quiet' to suppress auto-snapshot (e.g. '3 quiet').")] string? target = null,
                 [Description("Value: text to type (for type action), option text (for select), pixels (for scroll), JSON object for fill, absolute file path(s) for upload (a JSON array for multiple files, or a single path; multiple paths may also be newline-separated — commas are NOT separators), JSON array for steps (e.g. [{\"action\":\"click\",\"target\":\"Next\"},{\"action\":\"click\",\"target\":\"25\"}]), or 'quiet' to suppress snapshot")] string? value = null) =>
                {
                    var svc = GetOrCreateBrowserService(chatId);
                    var act = (action ?? "").Trim().ToLowerInvariant();
                    if (act is "click" or "type" or "press" or "select" or "download" or "back" or "clear" or "fill" or "upload" or "steps")
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (CurrentChat?.Id == chatId) HasUsedBrowser = true;
                            BrowserShowRequested?.Invoke(chatId);
                        });
                    }
                    return svc.DoAsync(action ?? "", target, value);
                },
                ToolDisplayHelper.BrowserDoToolName,
                "Interact with the page. Actions: click, type, press, select, scroll, back, wait, download, clear, fill, read_form, upload, steps. Use 'upload' to attach local file(s) to a file input WITHOUT the native OS file picker (value = absolute file path(s); target = optional file-input locator) — this is the only way to upload, never try to drive the native dialog. Use 'steps' to batch multiple actions in ONE call (value: JSON array like [{\"action\":\"click\",\"target\":\"Next month\"},{\"action\":\"click\",\"target\":\"25\"}]) — only snapshots once at end, drastically reducing tokens. Append ' quiet' to target or set value='quiet' on click/press/scroll to skip the auto-snapshot entirely."),

            AIFunctionFactory.Create(
                ([Description("JavaScript code to execute in the page context")] string script) =>
                {
                    var svc = GetOrCreateBrowserService(chatId);
                    return svc.EvaluateAsync(script);
                },
                ToolDisplayHelper.BrowserJsToolName,
                "Run JavaScript in the browser page context."),
        ];
    }

    /// <summary>Raised when a browser tool requests the browser panel to be visible. Carries the chat ID.</summary>
    public event Action<Guid>? BrowserShowRequested;

    /// <summary>True if browser tools have been used in the current session.</summary>
    [ObservableProperty] bool _hasUsedBrowser;

    /// <summary>True when the browser panel is currently visible.</summary>
    [ObservableProperty] bool _isBrowserOpen;

    /// <summary>Allows the view to request the browser panel to be shown for the current chat.</summary>
    public void RequestShowBrowser()
    {
        if (CurrentChat is not null)
            BrowserShowRequested?.Invoke(CurrentChat.Id);
    }

    /// <summary>Toggles the browser panel visibility for the current chat.</summary>
    public void ToggleBrowser()
    {
        if (IsBrowserOpen)
            BrowserHideRequested?.Invoke();
        else if (CurrentChat is not null)
            BrowserShowRequested?.Invoke(CurrentChat.Id);
    }

    /// <summary>True when the diff preview panel is currently visible.</summary>
    [ObservableProperty] bool _isDiffOpen;

    /// <summary>Shows a file diff in the preview island.</summary>
    public void ShowDiff(FileChangeItem item)
        => DiffShowRequested?.Invoke(item);

    /// <summary>Hides the diff preview island.</summary>
    public void HideDiff() => DiffHideRequested?.Invoke();

    private CancellationTokenSource? _modelSelectionSaveCts;
    private CancellationTokenSource? _modelSelectionSyncCts;

    partial void OnSelectedModelChanged(string? value)
    {
        UpdateQualityLevels(value);
        UpdateContextWindowTiers(value);
        if (CurrentChat is { } activeChat)
        {
            var runtime = GetOrCreateRuntimeState(activeChat.Id);
            ApplyKnownContextTokenLimit(activeChat, runtime, value, updateDisplayed: true);
        }

        if (_suppressModelSelectionSideEffects || string.IsNullOrWhiteSpace(value))
            return;

        var reasoningEffort = GetPersistedReasoningEffortPreference();
        var contextTier = GetSelectedContextWindowTier();

        // New chats (no messages yet) update the global default model.
        // Existing chats only update their per-chat model.
        if (CurrentChat is null || CurrentChat.Messages.Count == 0)
        {
            _dataStore.Data.Settings.PreferredModel = value;
            _dataStore.Data.Settings.ReasoningEffort = reasoningEffort ?? string.Empty;
            if (contextTier is not null)
                _dataStore.Data.Settings.ContextWindowTier = contextTier;
            _dataStore.Save();
            DefaultModelSelectionChanged?.Invoke(value, reasoningEffort, contextTier);
        }

        if (CurrentChat is { } chat)
        {
            chat.LastModelUsed = value;
            chat.LastReasoningEffortUsed = reasoningEffort;
            if (contextTier is not null)
                chat.LastContextWindowTierUsed = contextTier;
        }

        QueueModelSelectionSave();
        QueueMidSessionModelSelectionSync();
    }

    private void QueueModelSelectionSave(Chat? chat = null)
    {
        _modelSelectionSaveCts?.Cancel();
        _modelSelectionSaveCts?.Dispose();
        _modelSelectionSaveCts = null;

        var targetChat = chat ?? CurrentChat;
        if (targetChat is not { Messages.Count: > 0 })
            return;

        _dataStore.MarkChatChanged(targetChat);
        _modelSelectionSaveCts = new CancellationTokenSource();
        var cts = _modelSelectionSaveCts;
        _ = Task.Delay(500, cts.Token).ContinueWith(_ => SaveIndexAsync(),
            cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void QueueMidSessionModelSelectionSync()
    {
        _modelSelectionSyncCts?.Cancel();
        _modelSelectionSyncCts?.Dispose();
        _modelSelectionSyncCts = null;

        if (_activeSession is null || string.IsNullOrWhiteSpace(SelectedModel))
            return;

        var modelId = SelectedModel;
        var reasoningEffort = GetSelectedReasoningEffort();
        var contextTier = GetSelectedContextWindowTier();
        _modelSelectionSyncCts = new CancellationTokenSource();
        var cts = _modelSelectionSyncCts;
        _ = Task.Delay(75, cts.Token).ContinueWith(
            _ => SwitchModelMidSessionAsync(modelId, reasoningEffort, contextTier),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default).Unwrap();
    }

    private async Task SwitchModelMidSessionAsync(string modelId, string? reasoningEffort, string? contextTier)
    {
        if (_activeSession is null)
            return;

        try
        {
            await _activeSession.SetModelAsync(
                modelId,
                new SetModelOptions
                {
                    ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort,
                    ReasoningSummary = SessionConfigBuilder.DefaultReasoningSummary,
                    ContextTier = SessionConfigBuilder.CreateContextTier(contextTier)
                });
        }
        catch
        {
            // Fallback: SDK may not support mid-session switch for all models.
            // The next SendMessage will create/resume with the new model.
        }
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

    private AIFunction BuildAnnounceFileTool(Guid chatId)
    {
        return AIFunctionFactory.Create(
            ([Description("Absolute path of the file that was created, converted, or produced for the user")] string filePath) =>
            {
                if (File.Exists(filePath) && ToolDisplayHelper.IsUserFacingFile(filePath))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (CurrentChat?.Id != chatId) return;
                        _transcriptBuilder.ShownFileChips.Add(filePath);
                    });
                }
                return $"File announced: {filePath}";
            },
            "announce_file",
            "Show a file attachment chip to the user for a file you created or produced. Call this ONCE for each final deliverable file (e.g. the PDF, DOCX, PPTX, image, etc.). Do NOT call for intermediate/temporary files like scripts.");
    }

    private AIFunction BuildFetchSkillTool(ProjectContextCatalogSnapshot projectContextCatalog)
    {
        return AIFunctionFactory.Create(
            ([Description("The exact name of the skill to retrieve (as listed in Available Skills)")] string name) =>
            {
                var skill = _dataStore.Data.Skills
                    .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (skill is not null)
                    return $"# {skill.Name}\n\n{skill.Content}";

                var externalSkill = projectContextCatalog.FindSkill(name);
                if (externalSkill is not null)
                    return externalSkill.Content;

                return $"Skill not found: {name}. Check the Available Skills list for exact names.";
            },
            "fetch_skill",
            "Retrieve the full content of a skill by name. Use this when the user asks to use a skill, or when their request closely matches a skill's description. The skill content contains detailed instructions on how to perform the task.");
    }

    private AIFunction BuildAskQuestionTool(Guid chatId)
    {
        return AIFunctionFactory.Create(
            async ([Description("The question to ask the user")] string question,
             [Description("List of option labels for the user to choose from")] string[] options,
             [Description("Whether to allow the user to type a free-text answer in addition to the options. Default: true")] bool? allowFreeText,
             [Description("Whether the user can select multiple options (and optionally type free text) before confirming. When true and allowFreeText is also true, the user can combine option selections with custom typed entries. Default: false")] bool? allowMultiSelect) =>
            {
                var freeText = allowFreeText ?? true;
                var multiSelect = allowMultiSelect ?? false;
                var questionId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingQuestions[questionId] = tcs;
                IList<string> optionsList = options ?? Array.Empty<string>();
                var optionsJson = System.Text.Json.JsonSerializer.Serialize(optionsList.ToList(), Lumi.Models.AppDataJsonContext.Default.ListString);

                Dispatcher.UIThread.Post(() =>
                {
                    NotifyQuestionAsked(chatId, question);

                    if (CurrentChat?.Id == chatId)
                    {
                        _transcriptBuilder.AddQuestionToTranscript(questionId, question, optionsList, freeText, multiSelect);
                        QuestionAsked?.Invoke(questionId, question, optionsJson, freeText);
                        ScrollToEndRequested?.Invoke();
                    }
                });

                // Store questionId and question data on the tool message so it can be recovered during rebuild.
                // If no matching tool message exists, create one to guarantee rebuild works.
                Dispatcher.UIThread.Post(() =>
                {
                    var chat = _dataStore.Data.Chats.Find(c => c.Id == chatId);
                    if (chat is not null)
                    {
                        var toolMsg = chat.Messages.LastOrDefault(m =>
                            m.ToolName == "ask_question" && m.ToolStatus == "InProgress" && m.QuestionId is null);
                        if (toolMsg is null)
                        {
                            toolMsg = new Models.ChatMessage
                            {
                                Role = "tool",
                                ToolName = "ask_question",
                                ToolStatus = "InProgress",
                                Content = "",
                            };
                            chat.Messages.Add(toolMsg);
                        }
                        toolMsg.QuestionId = questionId;
                        toolMsg.QuestionText = question;
                        toolMsg.QuestionOptions = optionsJson;
                        toolMsg.QuestionAllowFreeText = freeText;
                        toolMsg.QuestionAllowMultiSelect = multiSelect;
                    }
                });

                try
                {
                    var answer = await tcs.Task;

                    // Persist the answer on the tool message so it survives reload
                    var resultText = $"User answered: {answer}";
                    Dispatcher.UIThread.Post(() =>
                    {
                        var chat = _dataStore.Data.Chats.Find(c => c.Id == chatId);
                        if (chat is not null)
                        {
                            var toolMsg = chat.Messages.LastOrDefault(m =>
                                m.ToolName == "ask_question" && m.QuestionId == questionId);
                            if (toolMsg is not null)
                                toolMsg.ToolOutput = resultText;
                        }
                    });

                    return resultText;
                }
                finally
                {
                    _pendingQuestions.Remove(questionId);
                }
            },
            "ask_question",
            "Ask the user a question with predefined options to choose from. Use this when you need the user to pick from a set of choices (e.g. selecting a template, confirming a direction, choosing between alternatives). The answer will be returned as text. Only use this for genuinely useful choices — don't ask unnecessary questions.",
            Lumi.Models.AppDataJsonContext.Default.Options);
    }

    /// <summary>Called by the View when the user selects an answer on a question card.</summary>
    public void SubmitQuestionAnswer(string questionId, string answer)
    {
        if (_pendingQuestions.TryGetValue(questionId, out var tcs))
            tcs.TrySetResult(answer);
    }

    private void NotifyQuestionAsked(Guid chatId, string question)
    {
        if (!_dataStore.Data.Settings.NotificationsEnabled)
            return;

        var chatTitle = _dataStore.Data.Chats.FirstOrDefault(c => c.Id == chatId)?.Title;
        NotificationService.ShowQuestion(question, chatTitle, chatId);
    }

    private List<AIFunction> BuildLumiManagementTools(Guid chatId)
    {
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description("Action: list, create, update, or delete")] string action,
                    [Description("Project ID or exact name for update/delete. Omit for create/list.")] string? identifier = null,
                    [Description("Project name for create, or the new name for update.")] string? name = null,
                    [Description("Project instructions or custom prompt text.")] string? instructions = null,
                    [Description("Working directory path for the project.")] string? workingDirectory = null,
                    [Description("Set to true to clear the project's working directory during update.")] bool? clearWorkingDirectory = null,
                    [Description("Optional folders to scan for project-scoped .github skills/agents and .vscode/mcp.json. Pass an empty array to clear on update.")] string[]? additionalContextDirectories = null,
                    [Description("Set to true to clear the project's additional context folders during update.")] bool? clearAdditionalContextDirectories = null,
                    [Description("Optional text query for list filtering.")] string? query = null) =>
                {
                    var result = FeatureManager.ManageProjects(
                        action,
                        identifier,
                        name,
                        instructions,
                        workingDirectory,
                        clearWorkingDirectory,
                        additionalContextDirectories,
                        clearAdditionalContextDirectories,
                        query);
                    return await ApplyFeatureChangeAsync(result);
                },
                "manage_projects",
                "List, create, update, or delete Lumi projects. Use this only when the user explicitly asks to manage Lumi's internal projects.",
                Lumi.Models.AppDataJsonContext.Default.Options),

            AIFunctionFactory.Create(
                async (
                    [Description("Action: list, create, update, or delete")] string action,
                    [Description("Skill ID or exact name for update/delete. Omit for create/list.")] string? identifier = null,
                    [Description("Skill name for create, or the new name for update.")] string? name = null,
                    [Description("Short skill description shown in the Available Skills list.")] string? description = null,
                    [Description("Full markdown content for the skill. Required when creating a skill.")] string? content = null,
                    [Description("Optional icon glyph, e.g. ⚡ or 📄.")] string? iconGlyph = null,
                    [Description("Optional text query for list filtering.")] string? query = null) =>
                {
                    var result = FeatureManager.ManageSkills(action, identifier, name, description, content, iconGlyph, query);
                    return await ApplyFeatureChangeAsync(result);
                },
                "manage_skills",
                "List, create, update, or delete Lumi skills. Use this only when the user explicitly asks to manage Lumi's internal skills.",
                Lumi.Models.AppDataJsonContext.Default.Options),

            AIFunctionFactory.Create(
                async (
                    [Description("Action: list, create, update, or delete")] string action,
                    [Description("Lumi ID or exact name for update/delete. Omit for create/list.")] string? identifier = null,
                    [Description("Lumi name for create, or the new name for update.")] string? name = null,
                    [Description("Short Lumi description.")] string? description = null,
                    [Description("System prompt for the Lumi agent.")] string? systemPrompt = null,
                    [Description("Optional icon glyph, e.g. ✦ or 📋.")] string? iconGlyph = null,
                    [Description("Skill names or IDs to link to the Lumi. Pass an empty array to clear linked skills on update.")] string[]? skillIdentifiers = null,
                    [Description("Tool names to restrict the Lumi to. Pass an empty array to allow all tools.")] string[]? toolNames = null,
                    [Description("MCP server names or IDs to link to the Lumi. Pass an empty array to clear linked MCP servers on update.")] string[]? mcpServerIdentifiers = null,
                    [Description("Optional text query for list filtering.")] string? query = null) =>
                {
                    var result = FeatureManager.ManageLumis(action, identifier, name, description, systemPrompt, iconGlyph, skillIdentifiers, toolNames, mcpServerIdentifiers, query);
                    return await ApplyFeatureChangeAsync(result);
                },
                "manage_lumis",
                "List, create, update, or delete Lumi agents. Use this only when the user explicitly asks to manage Lumi's internal Lumis.",
                Lumi.Models.AppDataJsonContext.Default.Options),

            AIFunctionFactory.Create(
                async (
                    [Description("Action: list, create, update, or delete")] string action,
                    [Description("MCP server ID or exact name for update/delete. Omit for create/list.")] string? identifier = null,
                    [Description("MCP server name for create, or the new name for update.")] string? name = null,
                    [Description("Short MCP server description.")] string? description = null,
                    [Description("Server type: local or remote.")] string? serverType = null,
                    [Description("Command for a local/stdIO MCP server.")] string? command = null,
                    [Description("Command arguments for a local MCP server.")] string[]? args = null,
                    [Description("URL for a remote MCP server.")] string? url = null,
                    [Description("Environment variables for local MCP servers in KEY=VALUE format.")] string[]? envEntries = null,
                    [Description("Headers for remote MCP servers in KEY=VALUE format.")] string[]? headerEntries = null,
                    [Description("Tool names exposed by this MCP server. Pass an empty array to allow all tools.")] string[]? toolNames = null,
                    [Description("Optional timeout in milliseconds.")] int? timeout = null,
                    [Description("Set to true to clear a previously configured timeout during update.")] bool? clearTimeout = null,
                    [Description("Whether the MCP server should be enabled.")] bool? isEnabled = null,
                    [Description("Optional text query for list filtering.")] string? query = null) =>
                {
                    var result = FeatureManager.ManageMcps(action, identifier, name, description, serverType, command, args, url, envEntries, headerEntries, toolNames, timeout, clearTimeout, isEnabled, query);
                    return await ApplyFeatureChangeAsync(result);
                },
                "manage_mcps",
                "List, create, update, or delete Lumi MCP servers. Use this only when the user explicitly asks to manage Lumi's internal MCP configuration.",
                Lumi.Models.AppDataJsonContext.Default.Options),

            AIFunctionFactory.Create(
                async (
                    [Description("Action: list, create, update, delete, pause, resume, or run_now")] string action,
                    [Description("Background job ID or exact name for update/delete/pause/resume/run_now. Omit for create/list.")] string? identifier = null,
                    [Description("Job name for create, or the new name for update.")] string? name = null,
                    [Description("Short human-readable purpose shown in the Jobs tab.")] string? description = null,
                    [Description("The prompt/instructions Lumi should receive whenever this job invokes the chat. Required for create.")] string? prompt = null,
                    [Description("Optional target chat ID or exact title. If omitted, uses the current chat.")] string? chatIdentifier = null,
                    [Description("Trigger type: time or script.")] string? triggerType = null,
                    [Description("For time triggers: interval, daily, weekly, monthly, once, or cron.")] string? scheduleType = null,
                    [Description("For interval time triggers: minutes between runs.")] int? intervalMinutes = null,
                    [Description("For daily time triggers: local HH:mm time, e.g. 08:00.")] string? dailyTime = null,
                    [Description("For weekly time triggers: days like Mon,Wed,Fri, weekdays, weekends, or daily.")] string? daysOfWeek = null,
                    [Description("For monthly time triggers: day of month, 1-31. Short months use the last valid day.")] int? monthlyDay = null,
                    [Description("For advanced time triggers: five-field cron expression: minute hour day-of-month month day-of-week. Example: 0 8 * * Mon-Fri.")] string? cronExpression = null,
                    [Description("For once time triggers: local date/time, e.g. 2026-04-25 08:00.")] string? runAt = null,
                    [Description("For script triggers: one-shot script content. Lumi starts it once, waits until the process exits, then wakes the linked chat with stdout, stderr, and exit code. For continued monitoring, create another script job after the wake.")] string? scriptContent = null,
                    [Description("For script triggers: powershell, python, node, or command.")] string? scriptLanguage = null,
                    [Description("True for a temporary time job that pauses after a successful invocation. Script jobs are always one-shot.")] bool? isTemporary = null,
                    [Description("Whether the job should be enabled.")] bool? isEnabled = null,
                    [Description("Set true to queue the job immediately.")] bool? runNow = null,
                    [Description("Optional text query for list filtering.")] string? query = null) =>
                {
                    var result = FeatureManager.ManageJobs(action, identifier, name, description, prompt, chatIdentifier,
                        triggerType, scheduleType, intervalMinutes, dailyTime, daysOfWeek, monthlyDay, cronExpression, runAt,
                        scriptContent, scriptLanguage, isTemporary, isEnabled, runNow, query, defaultChatId: chatId);
                    return await ApplyFeatureChangeAsync(result);
                },
                "manage_jobs",
                "List, create, update, delete, pause, resume, or run Lumi background jobs. Use when the user explicitly asks Lumi to monitor, remind, wait for a condition, follow up, or automate a recurring/temporary task in the background. Script jobs are one-shot wake scripts: the script waits/polls, exits when attention is needed, and Lumi wakes the linked chat with the script output.",
                Lumi.Models.AppDataJsonContext.Default.Options),

            AIFunctionFactory.Create(
                async (
                    [Description("Action: list, create, update, or delete")] string action,
                    [Description("Memory ID or exact key for update/delete. Omit for create/list.")] string? identifier = null,
                    [Description("Memory key for create, or the new key for update.")] string? key = null,
                    [Description("Full memory content. Required when creating a memory.")] string? content = null,
                    [Description("Memory category, e.g. Personal, Preferences, or Work.")] string? category = null,
                    [Description("Optional text query for list filtering.")] string? query = null) =>
                {
                    var result = FeatureManager.ManageMemories(action, identifier, key, content, category, query);
                    return await ApplyFeatureChangeAsync(result);
                },
                "manage_memories",
                "List, create, update, or delete Lumi memories. Use this only when the user explicitly asks to manage memories directly.",
                Lumi.Models.AppDataJsonContext.Default.Options),

            AIFunctionFactory.Create(
                async (
                    [Description("What to look for: a topic, keyword, phrase, person, or time hint (e.g. 'honeymoon hotel deal', 'the OLED tv chat', 'last week'). Leave empty to list the most recently active chats.")] string? query = null,
                    [Description("Maximum number of chats to return (1-25, default 8).")] int? limit = null) =>
                {
                    return await ChatHistory.SearchChatsAsync(query, limit, GetCurrentCancellationToken());
                },
                "search_chats",
                "Search the user's past Lumi chats by topic, keyword, phrase, name, or time hint. Returns the most relevant conversations with a stable chat id, title, project, last-active time, and a snippet of the matching text. Use this whenever the user refers to a previous conversation ('the chat where we…', 'what did we decide about…') so you can then open it with read_chat. Pass an empty query to list the most recent chats."),

            AIFunctionFactory.Create(
                async (
                    [Description("Which chat to read: a chat id from search_chats (preferred), an exact chat title, or a descriptive phrase to look up.")] string chat,
                    [Description("Maximum number of most-recent messages to include (1-400, default 60).")] int? maxMessages = null,
                    [Description("Include the assistant's internal reasoning text. Default false.")] bool includeReasoning = false,
                    [Description("Include a short summary of tool calls made in the chat. Default true.")] bool includeToolCalls = true) =>
                {
                    return await ChatHistory.ReadChatAsync(chat, maxMessages, includeReasoning, includeToolCalls, GetCurrentCancellationToken());
                },
                "read_chat",
                "Read the full transcript of one of the user's past chats so you can recall exactly what was discussed. Accepts a chat id (preferred — get it from search_chats), an exact title, or a descriptive phrase (it will search and either open the clear match or return candidates to pick from). Returns a clean, role-labelled transcript windowed to the most recent messages. The header also reports the chat's workspace (git worktree path or project folder), additional context directories, any saved plan, active skills/MCP servers, and model/token usage — use the workspace path when the user wants you to act on that chat's files or uncommitted code (e.g. 'implement it like the uncommitted code in that chat'). Use after search_chats, or directly when the user names a specific chat."),
        ];
    }

    private async Task<string> ApplyFeatureChangeAsync(FeatureChangeResult result)
    {
        if (!result.DataChanged)
            return result.Message;

        if (result.SyncSkillFiles)
            _dataStore.SyncSkillFiles();

        await SaveIndexAsync();

        Dispatcher.UIThread.Post(() =>
        {
            RefreshComposerCatalogs(syncProjectContextMcpSelections: false);
            var chatMetadataChanged = RefreshCurrentChatFeatureState(result);
            if (chatMetadataChanged)
                _ = SaveIndexAsync();

            if (CurrentChat?.CopilotSessionId is not null)
            {
                _pendingSessionInvalidations.Add(CurrentChat.Id);
                _pendingSkillInjections.Clear();
                _activeExternalSkillNames.Clear();
            }
            else
            {
                PrunePendingSkillInjections();
            }

            FeatureManagementStateChanged?.Invoke();
        });

        return result.Message;
    }

    private bool RefreshCurrentChatFeatureState(FeatureChangeResult result)
    {
        var chatMetadataChanged = false;
        if (CurrentChat is not null)
        {
            ActiveAgent = CurrentChat.AgentId.HasValue
                ? _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == CurrentChat.AgentId.Value)
                : null;

            chatMetadataChanged |= RefreshActiveSkillChipsFromState();
            chatMetadataChanged |= RefreshActiveMcpSelections(result);
            OnPropertyChanged(nameof(CurrentChat));
        }

        SyncComposerProjectSelectionFromState();
        SyncComposerAgentSelectionFromState();
        RefreshProjectBadge();
        RefreshAgentBadge();
        return chatMetadataChanged;
    }

    private bool RefreshActiveSkillChipsFromState()
    {
        var skillsById = _dataStore.Data.Skills.ToDictionary(skill => skill.Id);
        var projectContextCatalog = GetProjectContextCatalog();
        var externalSkillsByName = projectContextCatalog.Skills
            .ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        var filteredIds = new List<Guid>();
        var filteredExternalNames = new List<string>();
        var chips = new List<StrataTheme.Controls.StrataComposerChip>();

        foreach (var skillId in ActiveSkillIds.ToList())
        {
            if (!skillsById.TryGetValue(skillId, out var skill))
                continue;

            filteredIds.Add(skillId);
            chips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, skill.IconGlyph));
        }

        foreach (var skillName in _activeExternalSkillNames.ToList())
        {
            if (!externalSkillsByName.TryGetValue(skillName, out var skill))
                continue;

            filteredExternalNames.Add(skill.Name);
            chips.Add(new StrataTheme.Controls.StrataComposerChip(skill.Name, ExternalSkillGlyph));
        }

        ActiveSkillIds.Clear();
        _activeExternalSkillNames.Clear();
        ActiveSkillChips.Clear();
        foreach (var skillId in filteredIds)
            ActiveSkillIds.Add(skillId);
        foreach (var skillName in filteredExternalNames)
            _activeExternalSkillNames.Add(skillName);
        foreach (var chip in chips)
            ActiveSkillChips.Add(chip);

        var changed = false;
        if (CurrentChat is not null
            && (!CurrentChat.ActiveSkillIds.SequenceEqual(filteredIds)
                || !SkillNameListsEqual(CurrentChat.ActiveExternalSkillNames, filteredExternalNames)))
        {
            CurrentChat.ActiveSkillIds = new List<Guid>(filteredIds);
            CurrentChat.ActiveExternalSkillNames = new List<string>(filteredExternalNames);
            _dataStore.MarkChatChanged(CurrentChat);
            changed = true;
        }

        PrunePendingSkillInjections();
        return changed;
    }

    private void PrunePendingSkillInjections()
    {
        var validSkillIds = ActiveSkillIds.ToHashSet();
        _pendingSkillInjections.RemoveAll(skillId => !validSkillIds.Contains(skillId));
    }

    private bool RefreshActiveMcpSelections(FeatureChangeResult result)
    {
        var activeNames = ActiveMcpServerNames.ToList();

        if (result.RenamedMcpOldName is { } oldName && result.RenamedMcpNewName is { } newName)
        {
            for (var i = 0; i < activeNames.Count; i++)
            {
                if (string.Equals(activeNames[i], oldName, StringComparison.Ordinal))
                    activeNames[i] = newName;
            }
        }

        if (result.DeletedMcpName is { } deletedName)
            activeNames.RemoveAll(name => string.Equals(name, deletedName, StringComparison.Ordinal));

        var availableGlyphs = AvailableMcpChips
            .OfType<StrataTheme.Controls.StrataComposerChip>()
            .ToDictionary(chip => chip.Name, chip => chip.Glyph, StringComparer.OrdinalIgnoreCase);

        activeNames = activeNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => availableGlyphs.ContainsKey(name))
            .ToList();

        _suppressActiveMcpCollectionSync = true;
        try
        {
            ActiveMcpServerNames.Clear();
            ActiveMcpChips.Clear();
            foreach (var name in activeNames)
            {
                ActiveMcpServerNames.Add(name);
                ActiveMcpChips.Add(new StrataTheme.Controls.StrataComposerChip(name, availableGlyphs[name]));
            }
        }
        finally
        {
            _suppressActiveMcpCollectionSync = false;
        }

        if (CurrentChat is not null
            && !CurrentChat.ActiveMcpServerNames.SequenceEqual(activeNames, StringComparer.OrdinalIgnoreCase))
        {
            CurrentChat.ActiveMcpServerNames = new List<string>(activeNames);
            CurrentChat.HasExplicitMcpServerSelection = true;
            _dataStore.MarkChatChanged(CurrentChat);
            return true;
        }

        return false;
    }

    private List<AIFunction> BuildMemoryTools()
    {
        return _memoryAgentService.BuildRecallMemoryTools();
    }
}
