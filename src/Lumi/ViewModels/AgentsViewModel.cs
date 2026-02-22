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
            AvailableSkills.Add(new SkillToggle(skill.Id, skill.Name, skill.IconGlyph, isAssigned));
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

        if (SelectedAgent is not null)
        {
            SelectedAgent.Name = EditName.Trim();
            SelectedAgent.Description = EditDescription.Trim();
            SelectedAgent.SystemPrompt = EditSystemPrompt.Trim();
            SelectedAgent.IconGlyph = EditIconGlyph;
            SelectedAgent.SkillIds = selectedSkillIds;
            SelectedAgent.McpServerIds = selectedMcpServerIds;
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
                McpServerIds = selectedMcpServerIds
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

    partial void OnSearchQueryChanged(string value) => RefreshList();
}

/// <summary>Tracks a skill's selected state in the agent editor.</summary>
public partial class SkillToggle : ObservableObject
{
    public Guid SkillId { get; }
    public string Name { get; }
    public string IconGlyph { get; }
    [ObservableProperty] private bool _isSelected;

    public SkillToggle(Guid skillId, string name, string iconGlyph, bool isSelected)
    {
        SkillId = skillId;
        Name = name;
        IconGlyph = iconGlyph;
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
