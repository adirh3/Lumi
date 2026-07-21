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
         RefreshList();

         var selectedAgent = SelectedAgent is null
             ? null
             : _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == SelectedAgent.Id);

         if (SelectedAgent is not null && selectedAgent is null)
         {
             SelectedAgent = null;
             IsEditing = false;
             RefreshAvailableSkills(null);
             RefreshAvailableMcpServers(null);
             RefreshAvailableTools(null);
             return;
         }

         if (selectedAgent is not null)
         {
             if (!ReferenceEquals(SelectedAgent, selectedAgent))
             {
                 SelectedAgent = selectedAgent;
                 return;
             }

             SyncEditorFromAgent(selectedAgent);
         }

         RefreshAvailableSkills(selectedAgent);
         RefreshAvailableMcpServers(selectedAgent);
         RefreshAvailableTools(selectedAgent);
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
        ("announce_file", "Announce File", "Utility", "Show a file attachment chip for a produced file."),
        ("fetch_skill", "Fetch Skill", "Utility", "Retrieve the full content of a skill by name."),
        ("ask_question", "Ask Question", "Utility", "Ask the user a question with predefined options."),
        ("recall_memory", "Recall Memory", "Utility", "Search and recall stored memories about the user."),
        ("manage_projects", "Manage Projects", "Utility", "List, create, update, or delete Lumi projects on explicit request."),
        ("manage_skills", "Manage Skills", "Utility", "List, create, update, or delete Lumi skills on explicit request."),
        ("manage_lumis", "Manage Lumis", "Utility", "List, create, update, or delete Lumi agents on explicit request."),
        ("manage_mcps", "Manage MCPs", "Utility", "List, create, update, or delete Lumi MCP servers on explicit request."),
        ("manage_jobs", "Manage Jobs", "Utility", "Create, pause, resume, or delete Lumi background jobs on explicit request."),
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

    private void RefreshAvailableTools(LumiAgent? agent)
    {
        AvailableTools.Clear();
        var toolNames = agent?.ToolNames ?? [];
        var runtimeToolNames = ToolDisplayHelper.ToRuntimeToolNames(toolNames);
        var hasRestrictions = agent?.HasToolRestrictions == true;
        foreach (var (name, displayName, group, description) in KnownTools)
        {
            var isAssigned = !hasRestrictions || runtimeToolNames.Contains(name);
            AvailableTools.Add(new ToolToggle(name, displayName, group, description, isAssigned));
        }
    }

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
         if (value is null) return;
         SyncEditorFromAgent(value);
         RefreshAvailableSkills(value);
         RefreshAvailableMcpServers(value);
         RefreshAvailableTools(value);
         IsEditing = true;
     }

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

        // Keep the explicit flag so an empty selected list can mean "no Lumi tools".
        var allSelected = AvailableTools.All(t => t.IsSelected);
        var selectedToolNames = allSelected
            ? []
            : AvailableTools.Where(t => t.IsSelected).Select(t => t.ToolName).ToList();

        if (SelectedAgent is not null)
        {
            SelectedAgent.Name = EditName.Trim();
            SelectedAgent.Description = EditDescription.Trim();
            SelectedAgent.SystemPrompt = EditSystemPrompt.Trim();
            SelectedAgent.IconGlyph = EditIconGlyph;
            SelectedAgent.SkillIds = selectedSkillIds;
            SelectedAgent.McpServerIds = selectedMcpServerIds;
            SelectedAgent.ToolNames = selectedToolNames;
            SelectedAgent.HasExplicitToolSelection = !allSelected;
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
                HasExplicitToolSelection = !allSelected
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
