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

    [RelayCommand]
    private void NewAgent()
    {
        SelectedAgent = null;
        EditName = "";
        EditDescription = "";
        EditSystemPrompt = "";
        EditIconGlyph = "✦";
        IsEditing = true;
    }

    [RelayCommand]
    private void EditAgent(LumiAgent agent)
    {
        SelectedAgent = agent;
        EditName = agent.Name;
        EditDescription = agent.Description;
        EditSystemPrompt = agent.SystemPrompt;
        EditIconGlyph = agent.IconGlyph;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveAgent()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        if (SelectedAgent is not null)
        {
            SelectedAgent.Name = EditName.Trim();
            SelectedAgent.Description = EditDescription.Trim();
            SelectedAgent.SystemPrompt = EditSystemPrompt.Trim();
            SelectedAgent.IconGlyph = EditIconGlyph;
        }
        else
        {
            var agent = new LumiAgent
            {
                Name = EditName.Trim(),
                Description = EditDescription.Trim(),
                SystemPrompt = EditSystemPrompt.Trim(),
                IconGlyph = EditIconGlyph
            };
            _dataStore.Data.Agents.Add(agent);
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
    private void DeleteAgent(LumiAgent agent)
    {
        _dataStore.Data.Agents.Remove(agent);
        _dataStore.Save();
        if (SelectedAgent == agent)
        {
            SelectedAgent = null;
            IsEditing = false;
        }
        RefreshList();
    }

    partial void OnSearchQueryChanged(string value) => RefreshList();
}
