using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class ProjectsViewModel : ObservableObject
{
    /// <summary>How many chats the project page lists before "Show all".</summary>
    internal const int ChatPreviewCount = 8;

    private readonly DataStore _dataStore;
    private readonly ProjectGitSyncService? _projectGitSyncService;
    private int _defaultBranchDetectionVersion;
    private string? _editorBaseline;
    private bool _restoringSelection;
    private bool _syncingEditor;
    private int _savedToastVersion;

    [ObservableProperty] private Project? _selectedProject;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editInstructions = "";
    [ObservableProperty] private string _editWorkingDirectory = "";
    [ObservableProperty] private string _editAdditionalContextDirectories = "";
    [ObservableProperty] private bool _isCodingProject;
    [ObservableProperty] private bool _isGitProject;
    [ObservableProperty] private bool _editAutoSyncMainBranchDaily;
    [ObservableProperty] private bool _editDefaultNewChatsUseWorktree;
    [ObservableProperty] private string? _detectedDefaultBranch;
    [ObservableProperty] private bool _isDetectingDefaultBranch;
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>The open project differs from what is stored.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    /// <summary>Instructions are shown rendered rather than as editable markdown.</summary>
    [ObservableProperty] private bool _isPreviewingInstructions;

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _showSavedToast;
    [ObservableProperty] private bool _isShowingAllChats;

    /// <summary>0 sorts by name, 1 by most recent chat activity.</summary>
    [ObservableProperty] private int _sortIndex;

    public string DefaultBranchSummary => IsDetectingDefaultBranch
        ? Loc.Project_DefaultBranchDetecting
        : !string.IsNullOrWhiteSpace(DetectedDefaultBranch)
            ? string.Format(Loc.Project_DefaultBranchDetected, DetectedDefaultBranch)
            : Loc.Project_DefaultBranchUnknown;

    // OS-appropriate example paths for the working-dir / context-dir placeholders. Windows keeps the
    // exact prior C:\ examples; Linux/macOS show native-style paths so no Windows-only path appears
    // in the UI on other platforms.
    public string WorkingDirectoryPlaceholder =>
        OperatingSystem.IsWindows() ? @"C:\Projects\MyApp" : "/home/you/projects/myapp";
    public string AdditionalContextDirsPlaceholder =>
        OperatingSystem.IsWindows() ? "C:\\Projects\\SharedSkills\nD:\\McpConfigs" : "/home/you/shared-skills\n/home/you/mcp-configs";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<Chat> ProjectChats { get; } = [];

    /// <summary>The chats the project page lists: the most recent ones, or all of them.</summary>
    public ObservableCollection<Chat> VisibleProjectChats { get; } = [];

    /// <summary>The overview gallery: the same filtered list as <see cref="Projects"/>, with activity.</summary>
    public ObservableCollection<ProjectCard> ProjectCards { get; } = [];

    /// <summary><see cref="EditAdditionalContextDirectories"/> as removable rows.</summary>
    public ObservableCollection<ContextFolderItem> ContextFolders { get; } = [];

    /// <summary>Fired when a chat is clicked in the project detail view. MainViewModel navigates to it.</summary>
    public event Action<Chat>? ChatOpenRequested;

    /// <summary>Fired by "New chat"; the shell starts a chat inside the project.</summary>
    public event Action<Project>? NewChatRequested;

    public ProjectsViewModel(DataStore dataStore, ProjectGitSyncService? projectGitSyncService = null)
    {
        _dataStore = dataStore;
        _projectGitSyncService = projectGitSyncService;
        RefreshList();
    }

    public bool IsNewProject => IsEditing && SelectedProject is null;
    public bool ShowSaveBar => IsEditing && (HasUnsavedChanges || IsNewProject) && !IsConfirmingDelete;
    public bool CanSave => !string.IsNullOrWhiteSpace(EditName);
    public string SaveButtonText => IsNewProject ? Loc.Projects_Create : Loc.Mg_SaveChanges;
    public string SaveBarText => IsNewProject ? Loc.Mg_NotSavedYet : Loc.Mg_Unsaved;
    public string DiscardButtonText => IsNewProject ? Loc.Common_Cancel : Loc.Mg_Discard;
    public string DetailTitle => string.IsNullOrWhiteSpace(EditName) ? Loc.Projects_NewTitle : EditName;
    public string DetailInitial => ManagementText.Initial(EditName);
    public string DeleteConfirmText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_DeleteConfirm, DetailTitle);
    public bool HasAnyProjects => _dataStore.Data.Projects.Count > 0;
    public bool HasResults => ProjectCards.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool ShowNoResults => HasAnyProjects && !HasResults;
    public bool ShowEmptyState => !HasAnyProjects;
    public string NoResultsText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_NoResults, SearchQuery.Trim());
    public string TotalCountText => _dataStore.Data.Projects.Count.ToString(CultureInfo.CurrentCulture);
    public string ChatsInProjectsText => _dataStore.Data.Chats.Count(chat => chat.ProjectId is not null).ToString(CultureInfo.CurrentCulture);
    public bool IsSortedByName => SortIndex == 0;
    public bool IsSortedByRecent => SortIndex == 1;
    public bool HasInstructions => !string.IsNullOrWhiteSpace(EditInstructions);

    /// <summary>Only feeds the markdown renderer while the preview is showing, so typing never re-renders it.</summary>
    public string PreviewInstructions => IsPreviewingInstructions ? EditInstructions : "";

    /// <summary>A long rendered preview has been unfolded.</summary>
    [ObservableProperty] private bool _isPreviewExpanded;

    public bool IsPreviewLong => ManagementText.IsLongMarkdown(EditInstructions);
    public bool IsPreviewClipped => IsPreviewLong && !IsPreviewExpanded;
    public string PreviewToggleText => IsPreviewExpanded ? Loc.Mg_ShowLess : Loc.Mg_ShowMore;

    [RelayCommand]
    private void TogglePreviewExpanded() => IsPreviewExpanded = !IsPreviewExpanded;

    partial void OnIsPreviewExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPreviewClipped));
        OnPropertyChanged(nameof(PreviewToggleText));
    }
    public bool HasWorkingDirectory => !string.IsNullOrWhiteSpace(EditWorkingDirectory);
    public string WorkingDirectoryName
    {
        get
        {
            if (!HasWorkingDirectory)
                return Loc.Projects_NoFolder;

            var trimmed = EditWorkingDirectory.Trim().TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }
    public bool HasContextFolders => ContextFolders.Count > 0;
    public bool HasProjectChats => ProjectChats.Count > 0;
    public bool HasMoreChats => ProjectChats.Count > ChatPreviewCount;
    public string ShowAllChatsText => IsShowingAllChats
        ? Loc.Mg_ShowLess
        : string.Format(CultureInfo.CurrentCulture, Loc.Mg_ShowAllCount, ProjectChats.Count);
    public string ChatCountLabel => ManagementText.Count(ProjectChats.Count, Loc.Project_ChatCount, Loc.Project_ChatCounts);
    public string LastActiveLabel => ProjectChats.Count > 0
        ? string.Format(CultureInfo.CurrentCulture, Loc.Projects_LastActive, ManagementText.Relative(ProjectChats.Max(chat => chat.UpdatedAt)))
        : Loc.Projects_NoActivity;
    public int ProjectMemoryCount => SelectedProject is { } project
        ? _dataStore.Data.Memories.Count(memory => memory.ProjectId == project.Id
            && string.Equals(memory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase))
        : 0;
    public bool HasProjectMemories => ProjectMemoryCount > 0;
    public string ProjectMemoryLabel => ManagementText.Count(ProjectMemoryCount, Loc.Projects_MemoriesOne, Loc.Projects_MemoriesMany);
    public string CreatedLabel => SelectedProject is { } project
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Created, ManagementText.Date(project.CreatedAt))
        : "";

    public void RefreshFromStore()
    {
        // Rebuilding the list clears the sidebar's selection. Put the open project back quietly so the
        // page stays open or closed as it was, and keeps edits in progress.
        var open = SelectedProject;
        var hasUnsavedEdits = IsEditing && HasUnsavedChanges;
        RefreshList();

        if (open is null)
            return;

        var selectedProject = _dataStore.Data.Projects.FirstOrDefault(project => project.Id == open.Id);
        if (selectedProject is null)
        {
            SelectedProject = null;
            IsEditing = false;
            ProjectChats.Clear();
            RefreshVisibleChats();
            return;
        }

        RestoreSelection(selectedProject);
        if (!hasUnsavedEdits)
            SyncEditorFromProject(selectedProject);
        RefreshProjectChats(selectedProject.Id);
    }

    private void RestoreSelection(Project project)
    {
        _restoringSelection = true;
        try
        {
            SelectedProject = project;
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private void RefreshList()
    {
        UpdateChatCounts();
        Projects.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        var lastActivity = LastActivityByProject();
        var items = hasQuery
            ? SearchPipeline.Rank(
                _dataStore.Data.Projects,
                SearchQuery,
                static project =>
                [
                    SearchField.Primary(project.Name, 3.5),
                    new SearchField(project.WorkingDirectory, 1.3),
                    new SearchField(ProjectContextDirectoryHelper.FormatFolderList(project.AdditionalContextDirectories), 1.1),
                    SearchField.Content(project.Instructions, 1.0)
                ],
                static project => new SearchSortMetadata(Text: project.Name))
            : SortIndex == 1
                ? _dataStore.Data.Projects
                    .OrderByDescending(project => lastActivity.TryGetValue(project.Id, out var last) ? last : project.CreatedAt)
                    .ToArray()
                : _dataStore.Data.Projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        foreach (var project in items)
            Projects.Add(project);

        ProjectCards.Clear();
        foreach (var project in Projects)
        {
            ProjectCards.Add(new ProjectCard(
                project,
                project.ChatCount,
                lastActivity.TryGetValue(project.Id, out var last) ? last : null,
                EditProject,
                StartChatIn));
        }

        // The sidebar follows the open project one way; re-announcing it re-selects the row after a rebuild.
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(HasAnyProjects));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(ChatsInProjectsText));
    }

    private Dictionary<Guid, DateTimeOffset> LastActivityByProject()
        => _dataStore.Data.Chats
            .Where(static chat => chat.ProjectId is not null)
            .GroupBy(static chat => chat.ProjectId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Max(static chat => chat.UpdatedAt));

    /// <summary>Keeps <see cref="Project.ChatCount"/> current for the sidebar.</summary>
    private void UpdateChatCounts()
    {
        var counts = _dataStore.Data.Chats
            .Where(static chat => chat.ProjectId is not null)
            .GroupBy(static chat => chat.ProjectId!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());
        foreach (var project in _dataStore.Data.Projects)
            project.ChatCount = counts.GetValueOrDefault(project.Id);
    }

    [RelayCommand]
    private void NewProject()
    {
        SelectedProject = null;
        _syncingEditor = true;
        try
        {
            EditName = "";
            EditInstructions = "";
            EditWorkingDirectory = "";
            EditAdditionalContextDirectories = "";
            NewContextFolderPath = "";
            EditAutoSyncMainBranchDaily = false;
            EditDefaultNewChatsUseWorktree = false;
        }
        finally
        {
            _syncingEditor = false;
        }

        IsCodingProject = false;
        IsGitProject = false;
        DetectedDefaultBranch = null;
        IsDetectingDefaultBranch = false;
        ProjectChats.Clear();
        RefreshVisibleChats();
        _editorBaseline = CaptureEditor();
        IsPreviewingInstructions = false;
        IsPreviewExpanded = false;
        IsConfirmingDelete = false;
        IsEditing = true;
        UpdateDirtyState();
        NotifyDetailFacts();
    }

    [RelayCommand]
    private void EditProject(Project project)
    {
        SelectedProject = project;
    }

    /// <summary>Opens a project by id, clearing a search that would hide it from the list.</summary>
    public void OpenById(Guid id)
    {
        if (_dataStore.Data.Projects.FirstOrDefault(project => project.Id == id) is not { } project)
            return;

        if (!Projects.Contains(project))
            SearchQuery = "";
        if (ReferenceEquals(SelectedProject, project))
        {
            IsEditing = true;
            return;
        }

        SelectedProject = project;
    }

    /// <summary>Back to the overview.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        IsConfirmingDelete = false;
        SelectedProject = null;
        IsEditing = false;
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        if (_restoringSelection)
            return;

        if (value is null)
        {
            ProjectChats.Clear();
            RefreshVisibleChats();
            return;
        }
        NewContextFolderPath = "";
        SyncEditorFromProject(value);
        IsPreviewingInstructions = !string.IsNullOrWhiteSpace(value.Instructions);
        IsPreviewExpanded = false;
        IsConfirmingDelete = false;
        IsShowingAllChats = false;
        IsEditing = true;
        RefreshProjectChats(value.Id);
    }

    private void SyncEditorFromProject(Project project)
    {
        _syncingEditor = true;
        try
        {
            EditName = project.Name;
            EditInstructions = project.Instructions;
            EditWorkingDirectory = project.WorkingDirectory ?? "";
            EditAdditionalContextDirectories = ProjectContextDirectoryHelper.FormatFolderList(project.AdditionalContextDirectories);
            EditAutoSyncMainBranchDaily = project.AutoSyncMainBranchDaily;
            EditDefaultNewChatsUseWorktree = project.DefaultNewChatsUseWorktree;
        }
        finally
        {
            _syncingEditor = false;
        }

        RefreshCodingProjectState(project.WorkingDirectory);
        _editorBaseline = CaptureEditor();
        UpdateDirtyState();
        NotifyDetailFacts();
    }

    private string CaptureEditor()
        => string.Join(
            '\u001F',
            EditName,
            EditInstructions,
            EditWorkingDirectory,
            ProjectContextDirectoryHelper.FormatFolderList(ProjectContextDirectoryHelper.ParseFolderList(EditAdditionalContextDirectories)),
            EditAutoSyncMainBranchDaily,
            EditDefaultNewChatsUseWorktree);

    private void UpdateDirtyState()
    {
        if (_syncingEditor)
            return;

        HasUnsavedChanges = IsEditing && CaptureEditor() != _editorBaseline;
        OnPropertyChanged(nameof(IsNewProject));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(DiscardButtonText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailInitial));
        OnPropertyChanged(nameof(HasInstructions));
        OnPropertyChanged(nameof(IsPreviewLong));
        OnPropertyChanged(nameof(IsPreviewClipped));
        if (IsPreviewingInstructions)
            OnPropertyChanged(nameof(PreviewInstructions));
    }

    private void NotifyDetailFacts()
    {
        OnPropertyChanged(nameof(CreatedLabel));
        OnPropertyChanged(nameof(ProjectMemoryCount));
        OnPropertyChanged(nameof(HasProjectMemories));
        OnPropertyChanged(nameof(ProjectMemoryLabel));
    }

    partial void OnEditNameChanged(string value) => UpdateDirtyState();
    partial void OnEditInstructionsChanged(string value) => UpdateDirtyState();
    partial void OnEditAutoSyncMainBranchDailyChanged(bool value) => UpdateDirtyState();
    partial void OnEditDefaultNewChatsUseWorktreeChanged(bool value) => UpdateDirtyState();
    partial void OnIsEditingChanged(bool value) => UpdateDirtyState();
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));
    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    partial void OnIsPreviewingInstructionsChanged(bool value) => OnPropertyChanged(nameof(PreviewInstructions));

    partial void OnEditAdditionalContextDirectoriesChanged(string value)
    {
        ContextFolders.Clear();
        foreach (var folder in ProjectContextDirectoryHelper.ParseFolderList(value))
            ContextFolders.Add(new ContextFolderItem(folder, RemoveContextFolder));
        OnPropertyChanged(nameof(HasContextFolders));
        UpdateDirtyState();
    }

    partial void OnIsShowingAllChatsChanged(bool value) => RefreshVisibleChats();

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
    private void ShowPreview() => IsPreviewingInstructions = true;

    [RelayCommand]
    private void ShowEditor() => IsPreviewingInstructions = false;

    [RelayCommand]
    private void ToggleShowAllChats() => IsShowingAllChats = !IsShowingAllChats;

    /// <summary>
    /// Refreshes chat counts and the open project's chat list. Called on tab navigation and whenever
    /// chats are added, moved or removed; cards are updated in place so the gallery never re-templates.
    /// </summary>
    public void RefreshSelectedProjectChats()
    {
        UpdateChatCounts();
        var lastActivity = LastActivityByProject();
        foreach (var card in ProjectCards)
        {
            card.ChatCount = card.Model.ChatCount;
            card.LastActivity = lastActivity.TryGetValue(card.Model.Id, out var last) ? last : null;
        }

        OnPropertyChanged(nameof(ChatsInProjectsText));
        if (SelectedProject is { } p)
            RefreshProjectChats(p.Id);
    }

    private void RefreshProjectChats(Guid projectId)
    {
        ProjectChats.Clear();
        foreach (var chat in _dataStore.Data.Chats
            .Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt))
        {
            ProjectChats.Add(chat);
        }

        RefreshVisibleChats();
    }

    private void RefreshVisibleChats()
    {
        VisibleProjectChats.Clear();
        foreach (var chat in IsShowingAllChats ? ProjectChats : ProjectChats.Take(ChatPreviewCount))
            VisibleProjectChats.Add(chat);

        OnPropertyChanged(nameof(HasProjectChats));
        OnPropertyChanged(nameof(HasMoreChats));
        OnPropertyChanged(nameof(ShowAllChatsText));
        OnPropertyChanged(nameof(ChatCountLabel));
        OnPropertyChanged(nameof(LastActiveLabel));
    }

    [RelayCommand]
    private void OpenChat(Chat chat)
    {
        ChatOpenRequested?.Invoke(chat);
    }

    [RelayCommand]
    private void NewChatInProject()
    {
        if (SelectedProject is { } project)
            NewChatRequested?.Invoke(project);
    }

    private void StartChatIn(Project project) => NewChatRequested?.Invoke(project);

    /// <summary>Returns the number of chats in a project.</summary>
    public int GetChatCount(Guid projectId)
    {
        return _dataStore.Data.Chats.Count(c => c.ProjectId == projectId);
    }

    [RelayCommand]
    private void SaveProject()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        var workDir = string.IsNullOrWhiteSpace(EditWorkingDirectory) ? null : EditWorkingDirectory.Trim();
        var additionalContextDirectories = ProjectContextDirectoryHelper.ParseFolderList(EditAdditionalContextDirectories);

        Project savedProject;
        if (SelectedProject is not null)
        {
            var workingDirectoryChanged = !string.Equals(
                SelectedProject.WorkingDirectory,
                workDir,
                StringComparison.OrdinalIgnoreCase);
            var autoSyncEnabled = !SelectedProject.AutoSyncMainBranchDaily && EditAutoSyncMainBranchDaily;

            SelectedProject.Name = EditName.Trim();
            SelectedProject.Instructions = EditInstructions.Trim();
            SelectedProject.WorkingDirectory = workDir;
            SelectedProject.AdditionalContextDirectories = additionalContextDirectories;
            SelectedProject.AutoSyncMainBranchDaily = EditAutoSyncMainBranchDaily;
            SelectedProject.DefaultNewChatsUseWorktree = EditDefaultNewChatsUseWorktree;
            if (workingDirectoryChanged)
            {
                SelectedProject.LastMainBranchSyncAttemptAt = null;
                SelectedProject.LastMainBranchSyncAt = null;
                SelectedProject.LastMainBranchSyncError = null;
            }
            else if (autoSyncEnabled)
            {
                SelectedProject.LastMainBranchSyncAttemptAt = null;
                SelectedProject.LastMainBranchSyncError = null;
            }

            savedProject = SelectedProject;
        }
        else
        {
            var project = new Project
            {
                Name = EditName.Trim(),
                Instructions = EditInstructions.Trim(),
                WorkingDirectory = workDir,
                AdditionalContextDirectories = additionalContextDirectories,
                AutoSyncMainBranchDaily = EditAutoSyncMainBranchDaily,
                DefaultNewChatsUseWorktree = EditDefaultNewChatsUseWorktree
            };
            _dataStore.Data.Projects.Add(project);
            savedProject = project;
        }

        _ = _dataStore.SaveAsync();
        if (savedProject.AutoSyncMainBranchDaily)
            _projectGitSyncService?.RequestSync();

        // Saving keeps the project open: the page now shows what was stored.
        RefreshList();
        RestoreSelection(savedProject);
        SyncEditorFromProject(savedProject);
        RefreshProjectChats(savedProject.Id);
        IsEditing = true;
        UpdateDirtyState();
        FlashSaved();
        ProjectsChanged?.Invoke();
    }

    /// <summary>Fired when the project list changes (add/edit/delete).</summary>
    public event Action? ProjectsChanged;

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    /// <summary>Throws away edits: an existing project goes back to what is stored, a new one is dropped.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        NewContextFolderPath = "";
        if (SelectedProject is null)
        {
            IsEditing = false;
            return;
        }

        SyncEditorFromProject(SelectedProject);
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
    private void RequestDelete()
    {
        if (SelectedProject is not null)
            IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (SelectedProject is { } project)
            DeleteProject(project);
    }

    [RelayCommand]
    private void DeleteProject(Project project)
    {
        // Unassign all chats from this project
        foreach (var chat in _dataStore.Data.Chats.Where(c => c.ProjectId == project.Id))
        {
            chat.ProjectId = null;
            _dataStore.MarkChatChanged(chat);
        }

        _dataStore.Data.Projects.Remove(project);
        _ = _dataStore.SaveAsync();
        if (SelectedProject == project)
        {
            SelectedProject = null;
            IsEditing = false;
        }
        RefreshList();
        ProjectsChanged?.Invoke();
    }

    partial void OnSearchQueryChanged(string value) => RefreshList();

    partial void OnEditWorkingDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(HasWorkingDirectory));
        OnPropertyChanged(nameof(WorkingDirectoryName));
        if (_syncingEditor)
            return;

        RefreshCodingProjectState(value);
        UpdateDirtyState();
    }

    partial void OnDetectedDefaultBranchChanged(string? value) => OnPropertyChanged(nameof(DefaultBranchSummary));
    partial void OnIsDetectingDefaultBranchChanged(bool value) => OnPropertyChanged(nameof(DefaultBranchSummary));

    [RelayCommand]
    private void ClearWorkingDirectory()
    {
        EditWorkingDirectory = "";
    }

    [RelayCommand]
    private void OpenWorkingDirectory()
    {
        var path = EditWorkingDirectory.Trim();
        if (path.Length == 0 || !Directory.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Could not open project folder: {ex.Message}");
        }
    }

    public void AddAdditionalContextDirectories(IEnumerable<string> directories)
    {
        var combined = ProjectContextDirectoryHelper
            .ParseFolderList(EditAdditionalContextDirectories)
            .Concat(directories);
        EditAdditionalContextDirectories = ProjectContextDirectoryHelper.FormatFolderList(combined);
    }

    /// <summary>A context folder typed or pasted by hand, e.g. one that uses %USERPROFILE%.</summary>
    [ObservableProperty] private string _newContextFolderPath = "";

    [RelayCommand]
    private void AddContextFolder()
    {
        if (string.IsNullOrWhiteSpace(NewContextFolderPath))
            return;

        AddAdditionalContextDirectories([NewContextFolderPath.Trim()]);
        NewContextFolderPath = "";
    }

    private void RemoveContextFolder(ContextFolderItem folder)
    {
        var remaining = ProjectContextDirectoryHelper
            .ParseFolderList(EditAdditionalContextDirectories)
            .Where(path => !string.Equals(path, folder.Path, StringComparison.OrdinalIgnoreCase));
        EditAdditionalContextDirectories = ProjectContextDirectoryHelper.FormatFolderList(remaining);
    }

    [RelayCommand]
    private void ClearAdditionalContextDirectories()
    {
        EditAdditionalContextDirectories = "";
    }

    private void RefreshCodingProjectState(string? workingDirectory)
    {
        var version = Interlocked.Increment(ref _defaultBranchDetectionVersion);
        IsCodingProject = SystemPromptBuilder.IsCodingProject(workingDirectory);
        IsGitProject = !string.IsNullOrWhiteSpace(workingDirectory) && GitService.IsGitRepo(workingDirectory);
        DetectedDefaultBranch = null;
        IsDetectingDefaultBranch = IsGitProject;

        if (IsGitProject)
            _ = DetectDefaultBranchAsync(workingDirectory!, version);
    }

    private async Task DetectDefaultBranchAsync(string workingDirectory, int version)
    {
        try
        {
            var defaultBranch = await GitService.GetDefaultBranchInfoAsync(workingDirectory);
            if (version != Volatile.Read(ref _defaultBranchDetectionVersion))
                return;

            DetectedDefaultBranch = defaultBranch?.BranchName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Lumi] Default branch detection failed: {ex}");
        }
        finally
        {
            if (version == Volatile.Read(ref _defaultBranchDetectionVersion))
                IsDetectingDefaultBranch = false;
        }
    }
}
