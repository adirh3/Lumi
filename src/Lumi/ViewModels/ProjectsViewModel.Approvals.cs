using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>A project's approval mode choice: follow the default, or one of the three modes.</summary>
public sealed partial class ProjectApprovalChoice : ObservableObject
{
    public ProjectApprovalChoice(ApprovalMode? mode, string label, Action<ApprovalMode?> select)
    {
        Mode = mode;
        Label = label;
        SelectCommand = new RelayCommand(() => select(mode));
    }

    public ApprovalMode? Mode { get; }
    public string Label { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Customize → Projects → Approvals: the project's mode and its rules.</summary>
public partial class ProjectsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectApprovalModeHint))]
    private ApprovalMode? _editApprovalMode;

    private List<ProjectApprovalChoice>? _projectApprovalChoices;

    public IReadOnlyList<ProjectApprovalChoice> ProjectApprovalChoices => _projectApprovalChoices ??=
    [
        new(null, Loc.Get("Projects_ApprovalFollowDefault"), mode => EditApprovalMode = mode) { IsSelected = EditApprovalMode is null },
        .. ApprovalModeOption.CreateAll().Select(option =>
            new ProjectApprovalChoice(option.Mode, option.Title, mode => EditApprovalMode = mode) { IsSelected = EditApprovalMode == option.Mode }),
    ];

    public ObservableCollection<ApprovalRuleRowViewModel> ProjectApprovalRules { get; } = [];
    public bool HasProjectApprovalRules => ProjectApprovalRules.Count > 0;

    public string ProjectApprovalModeHint => EditApprovalMode is { } mode
        ? ApprovalModeOption.CreateAll().First(option => option.Mode == mode).Detail
        : Loc.Get("Projects_ApprovalFollowDefaultHint");

    /// <summary>Raised when the project's approvals changed, so live chats take them on.</summary>
    public event Action? ApprovalPolicyChanged;

    /// <summary>Raised to add a rule for a project in Settings → Permissions.</summary>
    public event Action<Guid>? AddApprovalRuleRequested;

    partial void OnEditApprovalModeChanged(ApprovalMode? value)
    {
        foreach (var choice in ProjectApprovalChoices)
            choice.IsSelected = choice.Mode == value;
        UpdateDirtyState();
    }

    private void RefreshProjectApprovalRules(Project? project)
    {
        ProjectApprovalRules.Clear();
        if (project is not null)
            foreach (var rule in project.ApprovalRules)
                ProjectApprovalRules.Add(new ApprovalRuleRowViewModel(rule, ApprovalRuleScope.Project, project.Id, project.Name));
        OnPropertyChanged(nameof(HasProjectApprovalRules));
    }

    /// <summary>Rules can also change in Settings → Permissions or from an approval card.</summary>
    public void RefreshApprovals() => RefreshProjectApprovalRules(SelectedProject);

    [RelayCommand]
    private void RemoveProjectApprovalRule(ApprovalRuleRowViewModel? row)
    {
        if (row is null || SelectedProject is not { } project)
            return;
        project.ApprovalRules.Remove(row.Rule);
        _ = _dataStore.SaveAsync();
        RefreshProjectApprovalRules(project);
        ApprovalPolicyChanged?.Invoke();
    }

    [RelayCommand]
    private void AddProjectApprovalRule()
    {
        if (SelectedProject is { } project)
            AddApprovalRuleRequested?.Invoke(project.Id);
    }
}
