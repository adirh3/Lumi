using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class AgentsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private LumiAgent? _selectedAgent;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editSystemPrompt = "";
    [ObservableProperty] private string _editIconGlyph = "✦";
    [ObservableProperty] private string _searchQuery = "";

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

    private void RefreshList()
    {
        Agents.Clear();
        var items = string.IsNullOrWhiteSpace(SearchQuery)
            ? _dataStore.Data.Agents
            : _dataStore.Data.Agents.Where(a =>
                a.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var agent in items.OrderBy(a => a.Name))
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
        ("lumi_search", "Web Search", "Web", "Search the web for information and return results."),
        ("lumi_fetch", "Fetch Webpage", "Web", "Fetch a webpage and return its text content."),
        ("browser", "Open Browser", "Browser", "Open a URL in the browser with persistent cookies/sessions."),
        ("browser_look", "Browser Look", "Browser", "Get the current page state with interactive elements."),
        ("browser_find", "Browser Find", "Browser", "Find and rank interactive elements by query."),
        ("browser_do", "Browser Interact", "Browser", "Click, type, press keys, select, scroll in the browser."),
        ("browser_js", "Browser JavaScript", "Browser", "Run JavaScript in the browser page context."),
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
    ];

    private void RefreshAvailableTools(LumiAgent? agent)
    {
        AvailableTools.Clear();
        // Empty ToolNames means "all tools" — show all as selected
        var hasRestrictions = agent?.ToolNames.Count > 0;
        foreach (var (name, displayName, group, description) in KnownTools)
        {
            var isAssigned = !hasRestrictions || agent!.ToolNames.Contains(name);
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
        EditName = value.Name;
        EditDescription = value.Description;
        EditSystemPrompt = value.SystemPrompt;
        EditIconGlyph = value.IconGlyph;
        RefreshAvailableSkills(value);
        RefreshAvailableMcpServers(value);
        RefreshAvailableTools(value);
        IsEditing = true;
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

        // Empty list = all tools available; only store names when some are deselected
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
                ToolNames = selectedToolNames
            };
            _dataStore.Data.Agents.Add(agent);
        }

        _ = _dataStore.SaveAsync();
        IsEditing = false;
        RefreshList();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void DeleteAgent(LumiAgent agent)
    {
        _dataStore.Data.Agents.Remove(agent);
        _ = _dataStore.SaveAsync();
        if (SelectedAgent == agent)
        {
            SelectedAgent = null;
            IsEditing = false;
        }
        RefreshList();
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
