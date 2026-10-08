using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
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

    /// <summary>Raised by "Start chat"; the shell opens a new chat with this Lumi.</summary>
    public event Action<LumiAgent>? ChatRequested;

    /// <summary>Raised when a skill of the open Lumi is opened; the shell switches to the Skills page.</summary>
    public event Action<Guid>? OpenSkillRequested;

    /// <summary>Raised when an MCP server of the open Lumi is opened; the shell switches to the MCP page.</summary>
    public event Action<Guid>? OpenMcpServerRequested;

    private string? _editorBaseline;
    private bool _restoringSelection;
    private bool _syncingEditor;
    private int _savedToastVersion;

    /// <summary>Raised to open the import sheet; the shell owns the sheet.</summary>
    public event Action? ImportRequested;

    [ObservableProperty] private LumiAgent? _selectedAgent;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editSystemPrompt = "";
    [ObservableProperty] private string _editIconGlyph = "✦";
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>The open Lumi differs from what is stored, including its skill, server and tool choices.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    /// <summary>The system prompt is shown rendered rather than as editable markdown.</summary>
    [ObservableProperty] private bool _isPreviewingPrompt;

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _showSavedToast;

    /// <summary>0 sorts by name, 1 puts the newest first.</summary>
    [ObservableProperty] private int _sortIndex;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<LumiAgent> Agents { get; } = [];

    /// <summary>The overview gallery: the same filtered list as <see cref="Agents"/>, with what each one carries.</summary>
    public ObservableCollection<AgentCard> AgentCards { get; } = [];

    /// <summary>All skills available for assignment to agents.</summary>
    public ObservableCollection<SkillToggle> AvailableSkills { get; } = [];

    /// <summary>All MCP servers available for assignment to agents.</summary>
    public ObservableCollection<McpServerToggle> AvailableMcpServers { get; } = [];

    /// <summary>All tools available for assignment to agents.</summary>
    public ObservableCollection<ToolToggle> AvailableTools { get; } = [];

    /// <summary><see cref="AvailableTools"/> grouped by area, sharing the same toggles.</summary>
    public ObservableCollection<ToolGroup> ToolGroups { get; } = [];

     public AgentsViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
     }

    public bool IsNewAgent => IsEditing && SelectedAgent is null;
    public bool ShowSaveBar => IsEditing && (HasUnsavedChanges || IsNewAgent) && !IsConfirmingDelete;
    public bool CanSave => !string.IsNullOrWhiteSpace(EditName);
    public string SaveButtonText => IsNewAgent ? Loc.Lumis_Create : Loc.Mg_SaveChanges;
    public string SaveBarText => IsNewAgent ? Loc.Mg_NotSavedYet : Loc.Mg_Unsaved;
    public string DiscardButtonText => IsNewAgent ? Loc.Common_Cancel : Loc.Mg_Discard;
    public string DetailTitle => string.IsNullOrWhiteSpace(EditName) ? Loc.Lumis_NewTitle : EditName;
    public string DeleteConfirmText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_DeleteConfirm, DetailTitle);
    public bool HasAnyAgents => _dataStore.Data.Agents.Count > 0;
    public bool HasResults => AgentCards.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool ShowNoResults => HasAnyAgents && !HasResults;
    public bool ShowEmptyState => !HasAnyAgents;
    public string NoResultsText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_NoResults, SearchQuery.Trim());
    public string TotalCountText => _dataStore.Data.Agents.Count.ToString(CultureInfo.CurrentCulture);
    public string ChatsWithLumisText => _dataStore.Data.Chats.Count(chat => chat.AgentId is not null).ToString(CultureInfo.CurrentCulture);
    public bool IsSortedByName => SortIndex == 0;
    public bool IsSortedByRecent => SortIndex == 1;
    public bool HasPrompt => !string.IsNullOrWhiteSpace(EditSystemPrompt);

    /// <summary>Only feeds the markdown renderer while the preview is showing, so typing never re-renders it.</summary>
    public string PreviewPrompt => IsPreviewingPrompt ? EditSystemPrompt : "";

    /// <summary>A long rendered preview has been unfolded.</summary>
    [ObservableProperty] private bool _isPreviewExpanded;

    public bool IsPreviewLong => ManagementText.IsLongMarkdown(EditSystemPrompt);
    public bool IsPreviewClipped => IsPreviewLong && !IsPreviewExpanded;
    public string PreviewToggleText => IsPreviewExpanded ? Loc.Mg_ShowLess : Loc.Mg_ShowMore;

    [RelayCommand]
    private void TogglePreviewExpanded() => IsPreviewExpanded = !IsPreviewExpanded;

    partial void OnIsPreviewExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPreviewClipped));
        OnPropertyChanged(nameof(PreviewToggleText));
    }
    public bool IsBuiltInAgent => SelectedAgent?.IsBuiltIn == true;
    public bool IsLearningAgent => SelectedAgent?.IsLearningAgent == true;
    public string CreatedLabel => SelectedAgent is { } agent
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Created, ManagementText.Date(agent.CreatedAt))
        : "";
    public int SelectedAgentChatCount => SelectedAgent is { } agent
        ? _dataStore.Data.Chats.Count(chat => chat.AgentId == agent.Id)
        : 0;
    public string ChatCountLabel => SelectedAgentChatCount > 0
        ? ManagementText.Count(SelectedAgentChatCount, Loc.Lumis_ChatCountOne, Loc.Lumis_ChatCountMany)
        : Loc.Lumis_NoChats;
    public int SelectedSkillCount => AvailableSkills.Count(static skill => skill.IsSelected);
    public int SelectedServerCount => AvailableMcpServers.Count(static server => server.IsSelected);
    public int SelectedToolCount => AvailableTools.Count(static tool => tool.IsSelected);
    public string SkillsCountText => string.Format(CultureInfo.CurrentCulture, Loc.Lumis_SelectedOf, SelectedSkillCount, AvailableSkills.Count);
    public string ServersCountText => string.Format(CultureInfo.CurrentCulture, Loc.Lumis_SelectedOf, SelectedServerCount, AvailableMcpServers.Count);
    public string ToolsCountText => string.Format(CultureInfo.CurrentCulture, Loc.Lumis_SelectedOf, SelectedToolCount, AvailableTools.Count);
    public string SkillsChipText => ManagementText.Count(SelectedSkillCount, Loc.Lumis_CapSkillsOne, Loc.Lumis_CapSkillsMany);
    public string ServersChipText => ManagementText.Count(SelectedServerCount, Loc.Lumis_CapServersOne, Loc.Lumis_CapServersMany);
    public string ToolsChipText => SelectedToolCount == AvailableTools.Count
        ? Loc.Lumis_AllTools
        : SelectedToolCount == 0
            ? Loc.Lumis_NoTools
            : ManagementText.Count(SelectedToolCount, Loc.Lumis_ToolsOne, Loc.Lumis_ToolsMany);
    public bool HasAvailableSkills => AvailableSkills.Count > 0;
    public bool HasAvailableServers => AvailableMcpServers.Count > 0;
    public bool AllToolsSelected => AvailableTools.Count > 0 && SelectedToolCount == AvailableTools.Count;
    public string ToggleAllToolsText => AllToolsSelected ? Loc.Lumis_ClearAll : Loc.Lumis_SelectAll;

    /// <summary>"All tools", "No Lumi tools" or "5 tools", for a stored Lumi.</summary>
    internal static string DescribeTools(LumiAgent agent)
    {
        if (!agent.HasToolRestrictions)
            return Loc.Lumis_AllTools;

        var visible = KnownTools.Count(tool => IsToolGroupAvailable(tool.Group)
            && ToolDisplayHelper.ToRuntimeToolNames(agent.ToolNames).Contains(tool.Name));
        return visible == 0
            ? Loc.Lumis_NoTools
            : ManagementText.Count(visible, Loc.Lumis_ToolsOne, Loc.Lumis_ToolsMany);
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
             UpdateDirtyState();
             NotifyDetailFacts();
             return;
         }

         if (stored is not null)
             SyncEditorFromAgent(stored);
         _editorBaseline = CaptureEditor();
         UpdateDirtyState();
         NotifyDetailFacts();
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
            : SortIndex == 1
                ? _dataStore.Data.Agents.OrderByDescending(agent => agent.CreatedAt).ToArray()
                : _dataStore.Data.Agents.OrderBy(agent => agent.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        foreach (var agent in items)
            Agents.Add(agent);

        var chatCounts = _dataStore.Data.Chats
            .Where(static chat => chat.AgentId is not null)
            .GroupBy(static chat => chat.AgentId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());
        AgentCards.Clear();
        foreach (var agent in Agents)
            AgentCards.Add(new AgentCard(agent, chatCounts.GetValueOrDefault(agent.Id), EditAgent, StartChatWith));

        // The sidebar follows the open Lumi one way; re-announcing it re-selects the row after a rebuild.
        OnPropertyChanged(nameof(SelectedAgent));
        OnPropertyChanged(nameof(HasAnyAgents));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(ChatsWithLumisText));
    }

    private void RefreshAvailableSkills(LumiAgent? agent)
    {
        AvailableSkills.Clear();
        foreach (var skill in _dataStore.Data.Skills.OrderBy(s => s.Name))
        {
            var isAssigned = agent?.SkillIds.Contains(skill.Id) == true;
            var toggle = new SkillToggle(skill.Id, skill.Name, skill.IconGlyph, skill.Description, isAssigned);
            toggle.PropertyChanged += OnToggleChanged;
            AvailableSkills.Add(toggle);
        }

        OnPropertyChanged(nameof(HasAvailableSkills));
    }

    private void RefreshAvailableMcpServers(LumiAgent? agent)
    {
        AvailableMcpServers.Clear();
        foreach (var server in _dataStore.Data.McpServers.OrderBy(s => s.Name))
        {
            var isAssigned = agent?.McpServerIds.Contains(server.Id) == true;
            var toggle = new McpServerToggle(server.Id, server.Name, isAssigned, server.Description, server.IsEnabled);
            toggle.PropertyChanged += OnToggleChanged;
            AvailableMcpServers.Add(toggle);
        }

        OnPropertyChanged(nameof(HasAvailableServers));
    }

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SkillToggle.IsSelected))
            return;

        UpdateDirtyState();
        NotifySelectionCounts();
    }

    private static readonly (string Name, string DisplayName, string Group, string Description)[] KnownTools =
    [
        ("lumi_fetch", "Fetch Webpage", "Web", "Fetch a webpage and return its text content."),
        (ToolDisplayHelper.BrowserOpenToolName, "Open Browser", "Browser", "Open a URL in the browser with persistent cookies/sessions."),
        (ToolDisplayHelper.BrowserLookToolName, "Browser Look", "Browser", "Get the current page state with interactive elements."),
        (ToolDisplayHelper.BrowserFindToolName, "Browser Find", "Browser", "Find and rank interactive elements by query."),
        (ToolDisplayHelper.BrowserDoToolName, "Browser Interact", "Browser", "Click, type, press keys, select, scroll in the browser."),
        (ToolDisplayHelper.BrowserJsToolName, "Browser JavaScript", "Browser", "Run JavaScript in the browser page context."),
        (ToolDisplayHelper.BrowserTabsToolName, "Browser Tabs", "Browser", "List, create, switch, and close browser tabs using stable tab IDs."),
        (ToolDisplayHelper.BrowserScreenshotToolName, "Browser Screenshot", "Browser", "Inspect a browser viewport image for visual layout, canvas content, and icons."),
        ("ui_list_windows", "List Windows", "Desktop", "List all visible windows on the desktop."),
        ("ui_inspect", "Inspect Window", "Desktop", "Inspect the UI element tree of a window."),
        ("ui_do", "Automate Window", "Desktop", "Run an ordered batch of UI actions and verify the result in one call."),
        ("ui_screenshot", "Screenshot Window", "Desktop", "Capture a window without focusing it and return the image to the model."),
        ("ui_click_at", "Click Screenshot", "Desktop", "Click image-relative coordinates from a recent window capture, with explicit foreground permission."),
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
            // Use the same browser availability policy as session registration and Settings.
            if (!IsToolGroupAvailable(group))
                continue;
            var isAssigned = !hasRestrictions || runtimeToolNames.Contains(name);
            var toggle = new ToolToggle(name, displayName, group, description, isAssigned);
            toggle.PropertyChanged += OnToggleChanged;
            AvailableTools.Add(toggle);
        }

        ToolGroups.Clear();
        foreach (var group in AvailableTools.GroupBy(static tool => tool.Group))
            ToolGroups.Add(new ToolGroup(group.Key, DescribeToolGroup(group.Key), group.ToList()));
    }

    private static string DescribeToolGroup(string group) => group switch
    {
        "Web" => Loc.Lumis_ToolGroup_Web,
        "Browser" => Loc.Lumis_ToolGroup_Browser,
        "Desktop" => Loc.Lumis_ToolGroup_Desktop,
        "Utility" => Loc.Lumis_ToolGroup_Utility,
        "Coding" => Loc.Lumis_ToolGroup_Coding,
        _ => group
    };

    /// <summary>Whether a tool group is usable on this host.</summary>
    private static bool IsToolGroupAvailable(string group)
        => group switch
        {
            "Browser" => NativeBrowserLogic.IsEmbeddedBrowserAvailable,
            "Desktop" => OperatingSystem.IsWindows(),
            _ => true,
        };

    [RelayCommand]
    private void NewAgent()
    {
        SelectedAgent = null;
        _syncingEditor = true;
        try
        {
            EditName = "";
            EditDescription = "";
            EditSystemPrompt = "";
            EditIconGlyph = "✦";
            RefreshAvailableSkills(null);
            RefreshAvailableMcpServers(null);
            RefreshAvailableTools(null);
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = CaptureEditor();
        IsPreviewingPrompt = false;
        IsPreviewExpanded = false;
        IsConfirmingDelete = false;
        IsEditing = true;
        UpdateDirtyState();
        NotifyDetailFacts();
    }

    [RelayCommand]
    private void EditAgent(LumiAgent agent)
    {
        SelectedAgent = agent;
    }

    /// <summary>Opens a Lumi by id, clearing a search that would hide it from the list.</summary>
    public void OpenById(Guid id)
    {
        if (_dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == id) is not { } agent)
            return;

        if (!Agents.Contains(agent))
            SearchQuery = "";
        if (ReferenceEquals(SelectedAgent, agent))
        {
            IsEditing = true;
            return;
        }

        SelectedAgent = agent;
    }

    /// <summary>Back to the overview.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        IsConfirmingDelete = false;
        SelectedAgent = null;
        IsEditing = false;
    }

     partial void OnSelectedAgentChanged(LumiAgent? value)
     {
         if (value is null || _restoringSelection) return;
         _syncingEditor = true;
         try
         {
             SyncEditorFromAgent(value);
             RefreshAvailableSkills(value);
             RefreshAvailableMcpServers(value);
             RefreshAvailableTools(value);
         }
         finally
         {
             _syncingEditor = false;
         }

         _editorBaseline = CaptureEditor();
         IsPreviewingPrompt = !string.IsNullOrWhiteSpace(value.SystemPrompt);
         IsPreviewExpanded = false;
         IsConfirmingDelete = false;
         IsEditing = true;
         UpdateDirtyState();
         NotifyDetailFacts();
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
         var wasSyncing = _syncingEditor;
         _syncingEditor = true;
         try
         {
             EditName = agent.Name;
             EditDescription = agent.Description;
             EditSystemPrompt = agent.SystemPrompt;
             EditIconGlyph = agent.IconGlyph;
         }
         finally
         {
             _syncingEditor = wasSyncing;
         }
     }

    private void UpdateDirtyState()
    {
        if (_syncingEditor)
            return;

        HasUnsavedChanges = IsEditing && CaptureEditor() != _editorBaseline;
        NotifyEditorState();
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(IsNewAgent));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(DiscardButtonText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(HasPrompt));
        OnPropertyChanged(nameof(IsPreviewLong));
        OnPropertyChanged(nameof(IsPreviewClipped));
        if (IsPreviewingPrompt)
            OnPropertyChanged(nameof(PreviewPrompt));
    }

    private void NotifySelectionCounts()
    {
        if (_syncingEditor)
            return;

        OnPropertyChanged(nameof(SelectedSkillCount));
        OnPropertyChanged(nameof(SelectedServerCount));
        OnPropertyChanged(nameof(SelectedToolCount));
        OnPropertyChanged(nameof(SkillsCountText));
        OnPropertyChanged(nameof(ServersCountText));
        OnPropertyChanged(nameof(ToolsCountText));
        OnPropertyChanged(nameof(SkillsChipText));
        OnPropertyChanged(nameof(ServersChipText));
        OnPropertyChanged(nameof(ToolsChipText));
        OnPropertyChanged(nameof(AllToolsSelected));
        OnPropertyChanged(nameof(ToggleAllToolsText));
    }

    /// <summary>Everything on the page that depends on which Lumi is open.</summary>
    private void NotifyDetailFacts()
    {
        NotifySelectionCounts();
        OnPropertyChanged(nameof(IsBuiltInAgent));
        OnPropertyChanged(nameof(IsLearningAgent));
        OnPropertyChanged(nameof(CreatedLabel));
        OnPropertyChanged(nameof(SelectedAgentChatCount));
        OnPropertyChanged(nameof(ChatCountLabel));
        OnPropertyChanged(nameof(HasAvailableSkills));
        OnPropertyChanged(nameof(HasAvailableServers));
    }

    partial void OnEditNameChanged(string value) => UpdateDirtyState();
    partial void OnEditDescriptionChanged(string value) => UpdateDirtyState();
    partial void OnEditSystemPromptChanged(string value) => UpdateDirtyState();
    partial void OnEditIconGlyphChanged(string value) => UpdateDirtyState();
    partial void OnIsEditingChanged(bool value) => UpdateDirtyState();
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));
    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    partial void OnIsPreviewingPromptChanged(bool value) => OnPropertyChanged(nameof(PreviewPrompt));

    partial void OnSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedByRecent));
        RefreshList();
    }

    [RelayCommand]
    private void SortByName() => SortIndex = 0;

    [RelayCommand]
    private void SortByRecent() => SortIndex = 1;

    [RelayCommand]
    private void ShowPreview() => IsPreviewingPrompt = true;

    [RelayCommand]
    private void ShowEditor() => IsPreviewingPrompt = false;

    [RelayCommand]
    private void ToggleAllTools()
    {
        var target = !AllToolsSelected;
        foreach (var tool in AvailableTools)
            tool.IsSelected = target;
    }

    [RelayCommand]
    private void StartChat()
    {
        if (SelectedAgent is { } agent)
            ChatRequested?.Invoke(agent);
    }

    private void StartChatWith(LumiAgent agent) => ChatRequested?.Invoke(agent);

    [RelayCommand]
    private void OpenSkill(SkillToggle? skill)
    {
        if (skill is not null)
            OpenSkillRequested?.Invoke(skill.SkillId);
    }

    [RelayCommand]
    private void OpenMcpServer(McpServerToggle? server)
    {
        if (server is not null)
            OpenMcpServerRequested?.Invoke(server.McpServerId);
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (SelectedAgent is not null)
            IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (SelectedAgent is { } agent)
            DeleteAgent(agent);
    }

    /// <summary>Throws away edits: an existing Lumi goes back to what is stored, a new one is dropped.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        if (SelectedAgent is not { } agent)
        {
            IsEditing = false;
            return;
        }

        _syncingEditor = true;
        try
        {
            SyncEditorFromAgent(agent);
            RefreshAvailableSkills(agent);
            RefreshAvailableMcpServers(agent);
            RefreshAvailableTools(agent);
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = CaptureEditor();
        UpdateDirtyState();
        NotifyDetailFacts();
    }

    private async void FlashSaved()
    {
        var version = ++_savedToastVersion;
        ShowSavedToast = true;
        await Task.Delay(1800);
        if (version == _savedToastVersion)
            ShowSavedToast = false;
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
        // Preserve tools unavailable on this host (Desktop off Windows; Browser on older macOS)
        // so editing an agent never erases its configuration for another supported host.
        var shownToolNames = KnownTools
            .Where(t => IsToolGroupAvailable(t.Group))
            .Select(t => t.Name)
            .ToHashSet();
        var preservedHidden = ToolDisplayHelper.ToRuntimeToolNames(SelectedAgent?.ToolNames ?? [])
            .Where(n => !shownToolNames.Contains(n))
            .ToList();

        // "[]" means "all tools, unrestricted". Collapsing to [] is only safe when no tool groups
        // are hidden on this platform, or the agent was already unrestricted. Otherwise — e.g. a
        // Windows agent that deliberately excluded the Desktop group, edited on Linux/macOS
        // where that group is hidden — collapsing would silently re-enable unavailable tools.
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

        LumiAgent saved;
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
            saved = SelectedAgent;
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
            saved = agent;
        }

        _ = _dataStore.SaveAsync();

        // Saving keeps the Lumi open: the page now shows what was stored.
        RefreshList();
        RestoreSelection(saved);
        _syncingEditor = true;
        try
        {
            SyncEditorFromAgent(saved);
            RefreshAvailableSkills(saved);
            RefreshAvailableMcpServers(saved);
            RefreshAvailableTools(saved);
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = CaptureEditor();
        IsEditing = true;
        UpdateDirtyState();
        NotifyDetailFacts();
        FlashSaved();
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
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
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
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Whether the server itself is switched on in the MCP page.</summary>
    public bool IsServerEnabled { get; }
    [ObservableProperty] private bool _isSelected;

    public McpServerToggle(Guid mcpServerId, string name, bool isSelected, string description = "", bool isServerEnabled = true)
    {
        McpServerId = mcpServerId;
        Name = name;
        Description = description;
        IsServerEnabled = isServerEnabled;
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
