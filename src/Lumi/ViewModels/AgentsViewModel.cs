using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class AgentsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    public event Action? AgentsChanged;

    /// <summary>
    /// Raised to open the share sheet for a Lumi; the shell owns the sheet. The flag is true when the
    /// open editor holds edits that the shared (saved) version does not include.
    /// </summary>
    public event Action<LumiAgent, bool>? ShareRequested;

    /// <summary>Raised for one-click "Copy for chat"; the flag has the same meaning as for <see cref="ShareRequested"/>.</summary>
    public event Action<LumiAgent, bool>? CopyForChatRequested;

    private string? _editorBaseline;
    private bool _restoringSelection;

    /// <summary>Raised to open the import sheet; the shell owns the sheet.</summary>
    public event Action? ImportRequested;

    [ObservableProperty] private LumiAgent? _selectedAgent;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editSystemPrompt = "";
    [ObservableProperty] private string _editIconGlyph = "✦";
    [ObservableProperty] private string _searchQuery = "";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<LumiAgent> Agents { get; } = [];

    /// <summary>All skills available for assignment to agents.</summary>
    public ObservableCollection<SkillToggle> AvailableSkills { get; } = [];

    /// <summary>All MCP servers available for assignment to agents.</summary>
    public ObservableCollection<McpServerToggle> AvailableMcpServers { get; } = [];

    /// <summary>All tools available for assignment to agents.</summary>
    public ObservableCollection<ToolToggle> AvailableTools { get; } = [];

     public AgentsViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
     }

     public void RefreshFromStore()
     {
         // Rebuilding the list clears the sidebar's selection, and through its two-way binding the
         // open Lumi. Put it back quietly afterwards: the editor stays attached, stays open or closed
         // as it was, and keeps edits in progress, including skill, server and tool choices.
         var open = SelectedAgent;
         var hasUnsavedEdits = IsEditing && CaptureEditor() != _editorBaseline;
         var chosenSkills = AvailableSkills.Where(static s => s.IsSelected).Select(static s => s.SkillId).ToHashSet();
         var chosenServers = AvailableMcpServers.Where(static s => s.IsSelected).Select(static s => s.McpServerId).ToHashSet();
         var chosenTools = AvailableTools.Where(static t => t.IsSelected).Select(static t => t.ToolName).ToHashSet(StringComparer.Ordinal);
         RefreshList();

         var stored = open is null
             ? null
             : _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == open.Id);

         if (open is not null && stored is null)
         {
             SelectedAgent = null;
             IsEditing = false;
             RefreshAvailableSkills(null);
             RefreshAvailableMcpServers(null);
             RefreshAvailableTools(null);
             return;
         }

         if (stored is not null)
             RestoreSelection(stored);

         // The lists are rebuilt either way so newly added skills and servers can be chosen.
         RefreshAvailableSkills(stored);
         RefreshAvailableMcpServers(stored);
         RefreshAvailableTools(stored);

         if (hasUnsavedEdits)
         {
             foreach (var skill in AvailableSkills)
                 skill.IsSelected = chosenSkills.Contains(skill.SkillId);
             foreach (var server in AvailableMcpServers)
                 server.IsSelected = chosenServers.Contains(server.McpServerId);
             foreach (var tool in AvailableTools)
                 tool.IsSelected = chosenTools.Contains(tool.ToolName);
             return;
         }

         if (stored is not null)
             SyncEditorFromAgent(stored);
         _editorBaseline = CaptureEditor();
     }

     private void RestoreSelection(LumiAgent agent)
     {
         _restoringSelection = true;
         try
         {
             SelectedAgent = agent;
         }
         finally
         {
             _restoringSelection = false;
         }
     }

    private void RefreshList()
    {
        Agents.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        var items = hasQuery
            ? SearchPipeline.Rank(
                _dataStore.Data.Agents,
                SearchQuery,
                static agent =>
                [
                    SearchField.Primary(agent.Name, 3.4),
                    new SearchField(agent.Description, 1.8),
                    SearchField.Content(agent.SystemPrompt, 0.95)
                ],
                static agent => new SearchSortMetadata(Text: agent.Name))
            : _dataStore.Data.Agents.OrderBy(agent => agent.Name).ToArray();

        foreach (var agent in items)
            Agents.Add(agent);
    }

    private void RefreshAvailableSkills(LumiAgent? agent)
    {
        AvailableSkills.Clear();
        foreach (var skill in _dataStore.Data.Skills.OrderBy(s => s.Name))
        {
            var isAssigned = agent?.SkillIds.Contains(skill.Id) == true;
            AvailableSkills.Add(new SkillToggle(skill.Id, skill.Name, skill.IconGlyph, skill.Description, isAssigned));
        }
    }

    private void RefreshAvailableMcpServers(LumiAgent? agent)
    {
        AvailableMcpServers.Clear();
        foreach (var server in _dataStore.Data.McpServers.OrderBy(s => s.Name))
        {
            var isAssigned = agent?.McpServerIds.Contains(server.Id) == true;
            AvailableMcpServers.Add(new McpServerToggle(server.Id, server.Name, isAssigned));
        }
    }

    private static readonly (string Name, string DisplayName, string Group, string Description)[] KnownTools =
    [
        ("lumi_fetch", "Fetch Webpage", "Web", "Fetch a webpage and return its text content."),
        (ToolDisplayHelper.BrowserOpenToolName, "Open Browser", "Browser", "Open a URL in the browser with persistent cookies/sessions."),
        (ToolDisplayHelper.BrowserLookToolName, "Browser Look", "Browser", "Get the current page state with interactive elements."),
        (ToolDisplayHelper.BrowserFindToolName, "Browser Find", "Browser", "Find and rank interactive elements by query."),
        (ToolDisplayHelper.BrowserDoToolName, "Browser Interact", "Browser", "Click, type, press keys, select, scroll in the browser."),
        (ToolDisplayHelper.BrowserJsToolName, "Browser JavaScript", "Browser", "Run JavaScript in the browser page context."),
        ("ui_list_windows", "List Windows", "Desktop", "List all visible windows on the desktop."),
        ("ui_inspect", "Inspect Window", "Desktop", "Inspect the UI element tree of a window."),
        ("ui_find", "Find UI Element", "Desktop", "Find UI elements matching a search query."),
        ("ui_click", "Click Element", "Desktop", "Click a UI element by its number."),
        ("ui_type", "Type Text", "Desktop", "Type or set text in a UI element."),
        ("ui_press_keys", "Press Keys", "Desktop", "Send keyboard shortcuts or key presses."),
        ("ui_read", "Read Element", "Desktop", "Read detailed information about a UI element."),
        ("announce_file", "Announce File", "Utility", "Announce a produced file, optionally opening its preview."),
        ("skill", "Load Lumi Skill", "Utility", "Allow the native skill tool to load Lumi-owned skills. Copilot's file-based skills are unchanged."),
        ("ask_question", "Ask Question", "Utility", "Ask the user a question with predefined options."),
        ("recall_memory", "Recall Memory", "Utility", "Search and recall stored memories about the user."),
        ("manage_projects", "Manage Projects", "Utility", "List, create, update, or delete Lumi projects on explicit request."),
        ("manage_skills", "Manage Skills", "Utility", "List, create, update, or delete Lumi skills on explicit request."),
        ("manage_lumis", "Manage Lumis", "Utility", "List, create, update, or delete Lumi agents on explicit request."),
        ("manage_mcps", "Manage MCPs", "Utility", "List, create, update, or delete Lumi MCP servers on explicit request."),
        ("manage_jobs", "Manage Jobs", "Utility", "Create, pause, resume, or delete scheduled, script, and chat-event background jobs on explicit request."),
        ("manage_memories", "Manage Memories", "Utility", "List, create, update, or delete Lumi memories on explicit request."),
        ("manage_current_chat", "Manage Current Chat", "Utility", "Inspect or update this chat's title and linked worktree workspace."),
        ("manage_chats", "Manage Chats", "Utility", "Create, list, message, and track other chats — act as a manager orchestrating chats across projects."),
        ("search_chats", "Search Chats", "Utility", "Search the user's past chats by topic, keyword, person, or time."),
        ("read_chat", "Read Chat", "Utility", "Open and read the transcript of a past chat by id, title, or phrase."),
        ("code_review", "Code Review", "Coding", "Expert code review for bugs, security, performance, and best practices."),
        ("generate_tests", "Generate Tests", "Coding", "Generate comprehensive unit tests for source code."),
        ("explain_code", "Explain Code", "Coding", "Deep code explanation with call flow and pattern identification."),
        ("analyze_project", "Analyze Project", "Coding", "Analyze project architecture, tech stack, and structure."),
    ];

    /// <summary>Friendly name for a Lumi tool id (legacy ids included); unknown ids are shown as written.</summary>
    internal static string GetToolDisplayName(string toolName)
    {
        var runtimeName = ToolDisplayHelper.ToRuntimeToolName(toolName);
        foreach (var (name, displayName, _, _) in KnownTools)
        {
            if (string.Equals(name, runtimeName, StringComparison.Ordinal))
                return displayName;
        }

        return toolName;
    }

    private void RefreshAvailableTools(LumiAgent? agent)
    {
        AvailableTools.Clear();
        var toolNames = agent?.ToolNames ?? [];
        var runtimeToolNames = ToolDisplayHelper.ToRuntimeToolNames(toolNames);
        var hasRestrictions = agent?.HasToolRestrictions == true;
        foreach (var (name, displayName, group, description) in KnownTools)
        {
            // Browser and Desktop (UI automation) tools are Windows-only and never registered off
            // Windows (see ChatViewModel.BuildCustomTools), so don't offer them as assignable here.
            if (!IsToolGroupAvailable(group))
                continue;
            var isAssigned = !hasRestrictions || runtimeToolNames.Contains(name);
            AvailableTools.Add(new ToolToggle(name, displayName, group, description, isAssigned));
        }
    }

    /// <summary>Whether a tool group is usable on this platform. Browser/Desktop groups are
    /// Windows-only; everything else is cross-platform.</summary>
    private static bool IsToolGroupAvailable(string group)
        => OperatingSystem.IsWindows() || group is not ("Browser" or "Desktop");

    [RelayCommand]
    private void NewAgent()
    {
        SelectedAgent = null;
        EditName = "";
        EditDescription = "";
        EditSystemPrompt = "";
        EditIconGlyph = "✦";
        RefreshAvailableSkills(null);
        RefreshAvailableMcpServers(null);
        RefreshAvailableTools(null);
        IsEditing = true;
    }

    [RelayCommand]
    private void EditAgent(LumiAgent agent)
    {
        SelectedAgent = agent;
    }

     partial void OnSelectedAgentChanged(LumiAgent? value)
     {
         if (value is null || _restoringSelection) return;
         SyncEditorFromAgent(value);
         RefreshAvailableSkills(value);
         RefreshAvailableMcpServers(value);
         RefreshAvailableTools(value);
         _editorBaseline = CaptureEditor();
         IsEditing = true;
     }

    private string CaptureEditor()
        => string.Join(
            '\u001F',
            EditName,
            EditDescription,
            EditSystemPrompt,
            EditIconGlyph,
            string.Join(',', AvailableSkills.Where(static s => s.IsSelected).Select(static s => s.SkillId)),
            string.Join(',', AvailableMcpServers.Where(static s => s.IsSelected).Select(static s => s.McpServerId)),
            string.Join(',', AvailableTools.Where(static t => t.IsSelected).Select(static t => t.ToolName)));

     private void SyncEditorFromAgent(LumiAgent agent)
     {
         EditName = agent.Name;
         EditDescription = agent.Description;
         EditSystemPrompt = agent.SystemPrompt;
         EditIconGlyph = agent.IconGlyph;
     }

    [RelayCommand]
    private void SaveAgent()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var selectedSkillIds = AvailableSkills
            .Where(s => s.IsSelected)
            .Select(s => s.SkillId)
            .ToList();

        var selectedMcpServerIds = AvailableMcpServers
            .Where(s => s.IsSelected)
            .Select(s => s.McpServerId)
            .ToList();

        // Empty list = all tools available; only store names when some are deselected.
        // Preserve any tools the agent already has that aren't shown on this platform (Windows-only
        // Browser/Desktop tools edited on Linux/macOS) so a Windows configuration is never erased.
        var shownToolNames = KnownTools
            .Where(t => IsToolGroupAvailable(t.Group))
            .Select(t => t.Name)
            .ToHashSet();
        var preservedHidden = ToolDisplayHelper.ToRuntimeToolNames(SelectedAgent?.ToolNames ?? [])
            .Where(n => !shownToolNames.Contains(n))
            .ToList();

        // "[]" means "all tools, unrestricted". Collapsing to [] is only safe when no tool groups
        // are hidden on this platform, or the agent was already unrestricted. Otherwise — e.g. a
        // Windows agent that deliberately excluded the Browser/Desktop groups, edited on Linux/macOS
        // where those groups are hidden — collapsing would silently re-enable those Windows-only tools.
        var hasHiddenGroups = KnownTools.Any(t => !IsToolGroupAvailable(t.Group));
        var wasRestricted = SelectedAgent?.HasToolRestrictions == true;
        var canBeUnrestricted = !(hasHiddenGroups && wasRestricted);

        var allShownSelected = AvailableTools.All(t => t.IsSelected);
        var isUnrestricted = allShownSelected && preservedHidden.Count == 0 && canBeUnrestricted;
        var selectedToolNames = isUnrestricted
            ? []
            : AvailableTools.Where(t => t.IsSelected).Select(t => t.ToolName)
                .Concat(preservedHidden)
                .Distinct()
                .ToList();

        if (SelectedAgent is not null)
        {
            SelectedAgent.Name = EditName.Trim();
            SelectedAgent.Description = EditDescription.Trim();
            SelectedAgent.SystemPrompt = EditSystemPrompt.Trim();
            SelectedAgent.IconGlyph = EditIconGlyph;
            SelectedAgent.SkillIds = selectedSkillIds;
            SelectedAgent.McpServerIds = selectedMcpServerIds;
            SelectedAgent.ToolNames = selectedToolNames;
            SelectedAgent.HasExplicitToolSelection = !isUnrestricted;
        }
        else
        {
            var agent = new LumiAgent
            {
                Name = EditName.Trim(),
                Description = EditDescription.Trim(),
                SystemPrompt = EditSystemPrompt.Trim(),
                IconGlyph = EditIconGlyph,
                SkillIds = selectedSkillIds,
                McpServerIds = selectedMcpServerIds,
                ToolNames = selectedToolNames,
                HasExplicitToolSelection = !isUnrestricted
            };
            _dataStore.Data.Agents.Add(agent);
        }

        _ = _dataStore.SaveAsync();
        IsEditing = false;
        RefreshList();
        AgentsChanged?.Invoke();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void DeleteAgent(LumiAgent agent)
    {
        var result = new LumiFeatureManager(_dataStore).ManageLumis("delete", identifier: agent.Id.ToString());
        if (!result.DataChanged)
            return;

        _ = _dataStore.SaveAsync();
        if (SelectedAgent == agent)
        {
            SelectedAgent = null;
            IsEditing = false;
        }
        RefreshList();
        AgentsChanged?.Invoke();
    }

    [RelayCommand]
    private void DeleteSelectedAgent()
    {
        if (SelectedAgent is not null)
            DeleteAgent(SelectedAgent);
    }

    [RelayCommand]
    private void ShareAgent(LumiAgent? agent)
    {
        agent ??= SelectedAgent;
        if (agent is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(agent, SelectedAgent) && CaptureEditor() != _editorBaseline;
        ShareRequested?.Invoke(agent, hasUnsavedEdits);
    }

    [RelayCommand]
    private void CopyAgentForChat(LumiAgent? agent)
    {
        agent ??= SelectedAgent;
        if (agent is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(agent, SelectedAgent) && CaptureEditor() != _editorBaseline;
        CopyForChatRequested?.Invoke(agent, hasUnsavedEdits);
    }

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke();

    partial void OnSearchQueryChanged(string value) => RefreshList();
}

/// <summary>Tracks a skill's selected state in the agent editor.</summary>
public partial class SkillToggle : ObservableObject
{
    public Guid SkillId { get; }
    public string Name { get; }
    public string IconGlyph { get; }
    public string Description { get; }
    [ObservableProperty] private bool _isSelected;

    public SkillToggle(Guid skillId, string name, string iconGlyph, string description, bool isSelected)
    {
        SkillId = skillId;
        Name = name;
        IconGlyph = iconGlyph;
        Description = description;
        _isSelected = isSelected;
    }
}

/// <summary>Tracks an MCP server's selected state in the agent editor.</summary>
public partial class McpServerToggle : ObservableObject
{
    public Guid McpServerId { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;

    public McpServerToggle(Guid mcpServerId, string name, bool isSelected)
    {
        McpServerId = mcpServerId;
        Name = name;
        _isSelected = isSelected;
    }
}

/// <summary>Tracks a tool's selected state in the agent editor.</summary>
public partial class ToolToggle : ObservableObject
{
    public string ToolName { get; }
    public string DisplayName { get; }
    public string Group { get; }
    public string Description { get; }
    [ObservableProperty] private bool _isSelected;

    public ToolToggle(string toolName, string displayName, string group, string description, bool isSelected)
    {
        ToolName = toolName;
        DisplayName = displayName;
        Group = group;
        Description = description;
        _isSelected = isSelected;
    }
}
