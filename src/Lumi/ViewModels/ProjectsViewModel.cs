using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly DataStore _dataStore;
    private readonly ProjectGitSyncService? _projectGitSyncService;
    private int _defaultBranchDetectionVersion;

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
        OperatingSystem.IsWindows() ? "C:\\Projects\\SharedSkills\nD:\\McpConfigs" : "/home/you/shared-skills\n/home/you/mcp-configs";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<Chat> ProjectChats { get; } = [];

    /// <summary>Fired when a chat is clicked in the project detail view. MainViewModel navigates to it.</summary>
    public event Action<Chat>? ChatOpenRequested;

    public ProjectsViewModel(DataStore dataStore, ProjectGitSyncService? projectGitSyncService = null)
    {
        _dataStore = dataStore;
        _projectGitSyncService = projectGitSyncService;
        RefreshList();
    }

    public void RefreshFromStore()
    {
        RefreshList();

        if (SelectedProject is null)
            return;

        var selectedProject = _dataStore.Data.Projects.FirstOrDefault(project => project.Id == SelectedProject.Id);
        if (selectedProject is null)
        {
            SelectedProject = null;
            IsEditing = false;
            ProjectChats.Clear();
            return;
        }

        if (!ReferenceEquals(SelectedProject, selectedProject))
        {
            SelectedProject = selectedProject;
            return;
        }

        SyncEditorFromProject(selectedProject);
        RefreshProjectChats(selectedProject.Id);
    }

    private void RefreshList()
    {
        Projects.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
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
            : _dataStore.Data.Projects.OrderBy(project => project.Name).ToArray();

        foreach (var project in items)
            Projects.Add(project);
    }

    [RelayCommand]
    private void NewProject()
    {
        SelectedProject = null;
        EditName = "";
        EditInstructions = "";
        EditWorkingDirectory = "";
        EditAdditionalContextDirectories = "";
        EditAutoSyncMainBranchDaily = false;
        EditDefaultNewChatsUseWorktree = false;
        IsCodingProject = false;
        IsGitProject = false;
        DetectedDefaultBranch = null;
        IsDetectingDefaultBranch = false;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditProject(Project project)
    {
        SelectedProject = project;
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        if (value is null)
        {
            ProjectChats.Clear();
            return;
        }
        SyncEditorFromProject(value);
        IsEditing = true;
        RefreshProjectChats(value.Id);
    }

    private void SyncEditorFromProject(Project project)
    {
        EditName = project.Name;
        EditInstructions = project.Instructions;
        EditWorkingDirectory = project.WorkingDirectory ?? "";
        EditAdditionalContextDirectories = ProjectContextDirectoryHelper.FormatFolderList(project.AdditionalContextDirectories);
        EditAutoSyncMainBranchDaily = project.AutoSyncMainBranchDaily;
        EditDefaultNewChatsUseWorktree = project.DefaultNewChatsUseWorktree;
        RefreshCodingProjectState(project.WorkingDirectory);
    }

    /// <summary>Refreshes the chat list for the currently selected project. Called on tab navigation.</summary>
    public void RefreshSelectedProjectChats()
    {
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
    }

    [RelayCommand]
    private void OpenChat(Chat chat)
    {
        ChatOpenRequested?.Invoke(chat);
    }

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
        IsEditing = false;
        RefreshList();
        ProjectsChanged?.Invoke();
    }

    /// <summary>Fired when the project list changes (add/edit/delete).</summary>
    public event Action? ProjectsChanged;

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
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
        RefreshCodingProjectState(value);
    }

    partial void OnDetectedDefaultBranchChanged(string? value) => OnPropertyChanged(nameof(DefaultBranchSummary));
    partial void OnIsDetectingDefaultBranchChanged(bool value) => OnPropertyChanged(nameof(DefaultBranchSummary));

    [RelayCommand]
    private void ClearWorkingDirectory()
    {
        EditWorkingDirectory = "";
    }

    public void AddAdditionalContextDirectories(IEnumerable<string> directories)
    {
        var combined = ProjectContextDirectoryHelper
            .ParseFolderList(EditAdditionalContextDirectories)
            .Concat(directories);
        EditAdditionalContextDirectories = ProjectContextDirectoryHelper.FormatFolderList(combined);
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
