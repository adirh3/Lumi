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
        ("web_search", "Web Search", "Web", "Search the web for information (SDK built-in, Bing-powered)."),
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
        ("manage_projects", "Manage Projects", "Utility", "List, create, update, or delete Lumi projects on explicit request."),
        ("manage_skills", "Manage Skills", "Utility", "List, create, update, or delete Lumi skills on explicit request."),
        ("manage_lumis", "Manage Lumis", "Utility", "List, create, update, or delete Lumi agents on explicit request."),
        ("manage_mcps", "Manage MCPs", "Utility", "List, create, update, or delete Lumi MCP servers on explicit request."),
        ("manage_jobs", "Manage Jobs", "Utility", "Create, pause, resume, or delete Lumi background jobs on explicit request."),
        ("manage_memories", "Manage Memories", "Utility", "List, create, update, or delete Lumi memories on explicit request."),
        ("code_review", "Code Review", "Coding", "Expert code review for bugs, security, performance, and best practices."),
        ("generate_tests", "Generate Tests", "Coding", "Generate comprehensive unit tests for source code."),
        ("explain_code", "Explain Code", "Coding", "Deep code explanation with call flow and pattern identification."),
        ("analyze_project", "Analyze Project", "Coding", "Analyze project architecture, tech stack, and structure."),
        ("release_discover_capabilities", "Release Capability Discovery", "Release", "Discover repo release agents, skills, prompts, capability JSON, and MCP config."),
        ("r2d_discover_capabilities", "Discover R2D Capabilities", "Release", "Discover R2D/SafeFly AutoCreate capabilities from repo and MCP config."),
        ("release_discover_evidence_adapters", "Discover Evidence Adapters", "Release", "Discover ADO, EV2, Kusto/ADX, release-analysis, health, and validation adapters."),
        ("release_build_evidence_packet", "Build Release Evidence", "Release", "Persist and review a definitive release proof chain."),
        ("release_review_evidence_packet", "Review Release Evidence", "Release", "Review a persisted release evidence packet for missing proof."),
        ("release_list_evidence_packets", "List Release Evidence", "Release", "List recent persisted release evidence packets."),
        ("release_draft_evidence_collection_plan", "Draft Evidence Plan", "Release", "Draft the exact release proof-chain collection plan."),
        ("r2d_create_draft", "Create R2D Draft", "Release", "Create a local SafeFly V2/R2D draft without submitting it."),
        ("r2d_validate_draft", "Validate R2D Draft", "Release", "Validate a local SafeFly V2/R2D draft for required metadata and proof."),
        ("r2d_get_draft", "Get R2D Draft", "Release", "Fetch a local SafeFly V2/R2D draft without modifying it."),
        ("r2d_list_drafts", "List R2D Drafts", "Release", "List recent local SafeFly V2/R2D drafts."),
        ("r2d_prepare_approved_request", "Prepare R2D Handoff", "Release", "Validate exact approval before external R2D/SafeFly handoff."),
        ("r2d_update_request_status", "Update R2D Status", "Release", "Record external R2D/SafeFly request status evidence."),
        ("r2d_get_request_status", "Get R2D Status", "Release", "Read local R2D/SafeFly request status evidence."),
        ("release_draft_monitor_prompt", "Draft Release Monitor", "Release", "Draft a Lumi background-job prompt for release monitoring."),
        ("release_create_monitor_job", "Create Release Monitor", "Release", "Create a Lumi background job for release monitoring."),
        ("release_discover_workiq_capabilities", "Discover Work IQ", "Release", "Discover Work IQ context and communications MCP capabilities."),
        ("release_draft_comms", "Draft Release Comms", "Release", "Draft approval-gated release communications."),
        ("release_draft_next_action_queue", "Draft Release Actions", "Release", "Order release next actions by urgency and safety."),
    ];

    private void RefreshAvailableTools(LumiAgent? agent)
    {
        AvailableTools.Clear();
        // Empty ToolNames means "all tools" — show all as selected
        var toolNames = agent?.ToolNames ?? [];
        var hasRestrictions = toolNames.Count > 0;
        foreach (var (name, displayName, group, description) in KnownTools)
        {
            var isAssigned = !hasRestrictions || toolNames.Contains(name);
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
