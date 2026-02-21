using System;
using System.Collections.ObjectModel;
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
        IsEditing = true;
        RefreshProjectChats(value.Id);
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

        if (SelectedProject is not null)
        {
            SelectedProject.Name = EditName.Trim();
            SelectedProject.Instructions = EditInstructions.Trim();
        }
        else
        {
            var project = new Project
            {
                Name = EditName.Trim(),
                Instructions = EditInstructions.Trim()
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
}
