using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ProjectsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private Project? _selectedProject;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editInstructions = "";
    [ObservableProperty] private string _editWorkingDirectory = "";
    [ObservableProperty] private bool _isCodingProject;
    [ObservableProperty] private string _searchQuery = "";

    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<Chat> ProjectChats { get; } = [];

    /// <summary>Fired when a chat is clicked in the project detail view. MainViewModel navigates to it.</summary>
    public event Action<Chat>? ChatOpenRequested;

    public ProjectsViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        RefreshList();
    }

    private void RefreshList()
    {
        Projects.Clear();
        var items = string.IsNullOrWhiteSpace(SearchQuery)
            ? _dataStore.Data.Projects
            : _dataStore.Data.Projects.Where(p =>
                p.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var project in items.OrderBy(p => p.Name))
            Projects.Add(project);
    }

    [RelayCommand]
    private void NewProject()
    {
        SelectedProject = null;
        EditName = "";
        EditInstructions = "";
        EditWorkingDirectory = "";
        IsCodingProject = false;
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
        EditName = value.Name;
        EditInstructions = value.Instructions;
        EditWorkingDirectory = value.WorkingDirectory ?? "";
        IsCodingProject = SystemPromptBuilder.IsCodingProject(value.WorkingDirectory);
        IsEditing = true;
        RefreshProjectChats(value.Id);
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
            .OrderByDescending(c => c.UpdatedAt))
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

        if (SelectedProject is not null)
        {
            SelectedProject.Name = EditName.Trim();
            SelectedProject.Instructions = EditInstructions.Trim();
            SelectedProject.WorkingDirectory = workDir;
        }
        else
        {
            var project = new Project
            {
                Name = EditName.Trim(),
                Instructions = EditInstructions.Trim(),
                WorkingDirectory = workDir
            };
            _dataStore.Data.Projects.Add(project);
        }

        _ = _dataStore.SaveAsync();
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
            chat.ProjectId = null;

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
        IsCodingProject = SystemPromptBuilder.IsCodingProject(value);
    }

    [RelayCommand]
    private void ClearWorkingDirectory()
    {
        EditWorkingDirectory = "";
    }
}
