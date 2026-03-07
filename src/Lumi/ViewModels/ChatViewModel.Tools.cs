using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHub.Copilot.SDK;
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

    private Dictionary<string, object>? BuildMcpServers(string workDir)
    {
        var allServers = _dataStore.Data.McpServers.Where(s => s.IsEnabled).ToList();

        // Always respect explicit composer selection first — if the user deselected
        // all MCPs, ActiveMcpServerNames is empty and we should send none.
        allServers = allServers.Where(s => ActiveMcpServerNames.Contains(s.Name)).ToList();

        // If an active agent restricts MCP servers, apply that as an intersection
        // with the user's current selection rather than overriding it.
        if (ActiveAgent is { McpServerIds.Count: > 0 })
        {
            var agentServerIds = ActiveAgent.McpServerIds;
            allServers = allServers.Where(s => agentServerIds.Contains(s.Id)).ToList();
        }

        var dict = new Dictionary<string, object>();

        // Add Lumi-configured MCP servers
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
                    Cwd = workDir,
                    Tools = server.Tools.Count > 0 ? server.Tools.ToList() : ["*"]
                };
                if (server.Env.Count > 0)
                    local.Env = new Dictionary<string, string>(server.Env);
                if (server.Timeout.HasValue)
                    local.Timeout = server.Timeout.Value;
                dict[server.Name] = local;
            }
        }

        // Add workspace MCP servers from .vscode/mcp.json (VS Code convention)
        MergeWorkspaceMcpServers(workDir, dict);

        return dict.Count > 0 ? dict : null;
    }

    /// <summary>
    /// Reads .vscode/mcp.json from the working directory and merges any servers
    /// not already present into the MCP server dictionary. This allows workspace
    /// MCP configs (VS Code convention) to be available in Copilot sessions.
    /// </summary>
    private static void MergeWorkspaceMcpServers(string workDir, Dictionary<string, object> dict)
    {
        var mcpJsonPath = Path.Combine(workDir, ".vscode", "mcp.json");
        if (!File.Exists(mcpJsonPath)) return;

        try
        {
            var json = File.ReadAllText(mcpJsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("servers", out var servers)) return;

            foreach (var server in servers.EnumerateObject())
            {
                if (dict.ContainsKey(server.Name)) continue; // Lumi config takes precedence

                if (server.Value.TryGetProperty("type", out var typeProp))
                {
                    var type = typeProp.GetString() ?? "stdio";
                    if (type == "stdio")
                    {
                        var command = server.Value.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
                        var args = new List<string>();
                        if (server.Value.TryGetProperty("args", out var argsProp))
                        {
                            foreach (var arg in argsProp.EnumerateArray())
                                args.Add(arg.GetString() ?? "");
                        }

                        dict[server.Name] = new McpLocalServerConfig
                        {
                            Command = command,
                            Args = args,
                            Type = "stdio",
                            Cwd = workDir,
                            Tools = ["*"]
                        };
                    }
                }
            }
        }
        catch { /* best effort — malformed JSON or missing fields */ }
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

                        });
                    }
                    return text;
                },
                "lumi_search",
                "Search the web for information. Returns titles, snippets, and URLs from search results. Use this when you only need to discover URLs or see search snippets — for full research, prefer lumi_research instead."),

            AIFunctionFactory.Create(
                ([Description("The full URL to fetch (must start with http:// or https://)")] string url) =>
                {
                    return WebFetchService.FetchAsync(url);
                },
                "lumi_fetch",
                "Fetch a webpage and return its text content. For long pages, returns a preview and saves the full content to a temp file you can read with Get-Content. If this fails, do NOT retry the same URL — try a different source instead."),

            AIFunctionFactory.Create(
                async ([Description("The search query to research")] string query,
                 [Description("Number of top results to automatically fetch (default 3, max 5)")] int maxPages = 3) =>
                {
                    maxPages = Math.Clamp(maxPages, 1, 5);

                    // Search for more results than needed so we have fallbacks for failed fetches
                    var (searchText, results) = await WebSearchService.SearchWithResultsAsync(query, maxPages + 4);
                    if (results.Count == 0)
                        return searchText;

                    // Post search sources to UI
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var r in results)
                            _pendingSearchSources.Add(new SearchSource { Title = r.Title, Snippet = r.Snippet, Url = r.Url });
                    });

                    // Fetch pages, skipping failures and trying next results until we have enough
                    var fetchedPages = new List<(WebSearchService.SearchResult Result, string Content)>();
                    var fetchTasks = results.Select(async r =>
                    {
                        var content = await WebFetchService.FetchAsync(r.Url);
                        return (Result: r, Content: content);
                    }).ToList();

                    foreach (var task in fetchTasks)
                    {
                        if (fetchedPages.Count >= maxPages) break;
                        var (result, content) = await task;
                        // Skip failed fetches (errors start with "Failed" or "Timed out" or "Error")
                        if (content.StartsWith("Failed") || content.StartsWith("Timed out") || content.StartsWith("Error") || content.StartsWith("Could not"))
                            continue;
                        fetchedPages.Add((result, content));
                    }

                    // Combine search results + fetched content
                    var output = searchText + "\n\n" + new string('—', 40) + "\n";
                    for (int i = 0; i < fetchedPages.Count; i++)
                    {
                        var (r, content) = fetchedPages[i];
                        output += $"\n## Content from: {r.Title} ({new Uri(r.Url).Host})\n";
                        output += $"Source: {r.Url}\n\n";
                        output += content + "\n\n" + new string('—', 40) + "\n";
                    }

                    if (fetchedPages.Count == 0)
                        output += "\n[All page fetches failed. Use the URLs above with lumi_fetch or browser to read them manually.]\n";

                    return output.TrimEnd();
                },
                "lumi_research",
                "Search the web AND automatically fetch the top results — all in one call. Use this as your primary research tool. Returns search results plus the full content of the top pages. For long pages, content is saved to temp files you can read with Get-Content."),
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
    public void ShowDiff(FileChangeItem item)
        => DiffShowRequested?.Invoke(item);

    /// <summary>Hides the diff preview island.</summary>
    public void HideDiff() => DiffHideRequested?.Invoke();

    partial void OnSelectedModelChanged(string? value)
    {
        UpdateQualityLevels(value);

        if (string.IsNullOrWhiteSpace(value)) return;
        _dataStore.Data.Settings.PreferredModel = value;
        _ = SaveIndexAsync();

        // Mid-session model switch via SDK API (avoids session invalidation)
        if (_activeSession is not null)
            _ = SwitchModelMidSessionAsync(value);
    }

    private async Task SwitchModelMidSessionAsync(string modelId)
    {
        if (_activeSession is null) return;
        try
        {
            await _copilotService.SwitchSessionModelAsync(_activeSession, modelId);
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

    private AIFunction BuildAnnounceFileTool()
    {
        return AIFunctionFactory.Create(
            ([Description("Absolute path of the file that was created, converted, or produced for the user")] string filePath) =>
            {
                if (File.Exists(filePath) && ToolDisplayHelper.IsUserFacingFile(filePath))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _transcriptBuilder.ShownFileChips.Add(filePath);
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
                    _transcriptBuilder.AddQuestionToTranscript(questionId, question, options, freeText);
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
}
