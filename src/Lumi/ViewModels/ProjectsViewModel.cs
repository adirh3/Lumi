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
        if (value is null) return;
        EditName = value.Name;
        EditInstructions = value.Instructions;
        IsEditing = true;
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

        _dataStore.Save();
        IsEditing = false;
        RefreshList();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void DeleteProject(Project project)
    {
        _dataStore.Data.Projects.Remove(project);
        _dataStore.Save();
        if (SelectedProject == project)
        {
            SelectedProject = null;
            IsEditing = false;
        }
        RefreshList();
    }

    partial void OnSearchQueryChanged(string value) => RefreshList();
}
