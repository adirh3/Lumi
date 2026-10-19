using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Permissions;

namespace Lumi.ViewModels;

/// <summary>Where a rule lives, so removing it touches the right list.</summary>
public enum ApprovalRuleScope
{
    Everywhere,
    Project,
    Chat
}

/// <summary>One rule, as the Permissions page and a project's approvals list show it.</summary>
public sealed class ApprovalRuleRowViewModel
{
    public ApprovalRuleRowViewModel(ApprovalRule rule, ApprovalRuleScope scope, Guid? ownerId, string? ownerName)
    {
        Rule = rule;
        Scope = scope;
        OwnerId = ownerId;
        ActionLabel = ApprovalRuleText.Action(rule.Action);
        Description = ApprovalRuleText.Describe(rule);
        IconKey = ApprovalRuleText.IconKey(rule);
        ScopeLabel = scope switch
        {
            ApprovalRuleScope.Project => Loc.Get("Permissions_ScopeProject", ownerName ?? ""),
            ApprovalRuleScope.Chat => Loc.Get("Permissions_ScopeChat", ownerName ?? ""),
            _ => Loc.Get("Permissions_ScopeEverywhere")
        };
    }

    public ApprovalRule Rule { get; }
    public ApprovalRuleScope Scope { get; }
    public Guid? OwnerId { get; }
    public string ActionLabel { get; }
    public string Description { get; }
    public string IconKey { get; }
    public string ScopeLabel { get; }
    public bool IsAllow => Rule.Action == ApprovalRuleAction.Allow;
    public bool IsAsk => Rule.Action == ApprovalRuleAction.Ask;
    public bool IsNever => Rule.Action == ApprovalRuleAction.Never;
}

/// <summary>A choice in the rule builder, such as a kind of action or a Lumi ability.</summary>
public sealed record ApprovalChoiceOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Plain-language wording for approval rules, shared by Settings, projects and the chat.</summary>
public static class ApprovalRuleText
{
    public static string Action(ApprovalRuleAction action) => Loc.Get(action switch
    {
        ApprovalRuleAction.Ask => "Permissions_ActionAsk",
        ApprovalRuleAction.Never => "Permissions_ActionNever",
        _ => "Permissions_ActionAllow"
    });

    public static string Describe(ApprovalRule rule)
    {
        var target = ApprovalPolicy.NormalizeTarget(rule);
        return rule.Kind switch
        {
            ApprovalRuleKind.Command => target is null ? Loc.Get("Permissions_RuleAnyCommand") : Loc.Get("Permissions_RuleCommand", target),
            ApprovalRuleKind.EditFiles => target is null ? Loc.Get("Permissions_RuleAnyEdit") : Loc.Get("Permissions_RuleEdit", target),
            ApprovalRuleKind.ReadFiles => target is null ? Loc.Get("Permissions_RuleAnyRead") : Loc.Get("Permissions_RuleRead", target),
            ApprovalRuleKind.Connector => DescribeConnector(target),
            ApprovalRuleKind.LumiTool => target is null ? Loc.Get("Permissions_RuleAnyLumiTool")
                : ApprovalPolicy.LumiToolGroups.Contains(target) ? Loc.Get("Permissions_RuleGroup", Loc.Get("Approval_Group_" + target))
                : Loc.Get("Permissions_RuleLumiTool", ToolDisplayHelper.GetFriendlyToolDisplay(target, null, null).Name),
            _ => target ?? ""
        };
    }

    private static string DescribeConnector(string? target)
    {
        if (target is null)
            return Loc.Get("Permissions_RuleAnyConnector");
        var slash = target.IndexOf('/');
        return slash < 0
            ? Loc.Get("Permissions_RuleConnector", target)
            : Loc.Get("Permissions_RuleConnectorTool", target[(slash + 1)..], target[..slash]);
    }

    public static string IconKey(ApprovalRule rule) => rule.Kind switch
    {
        ApprovalRuleKind.Command => TranscriptIcons.Terminal,
        ApprovalRuleKind.EditFiles => TranscriptIcons.Edit,
        ApprovalRuleKind.ReadFiles => TranscriptIcons.Read,
        ApprovalRuleKind.Connector => TranscriptIcons.Connector,
        _ => rule.Target is { } target && ApprovalPolicy.ToolsInGroup(target) is { Count: > 0 } tools
            ? ToolDisplayHelper.GetToolIconKey(tools[0])
            : TranscriptIcons.Tool
    };

    /// <summary>What a rule will do, as one sentence, before it is added.</summary>
    public static string Preview(ApprovalRule rule) => Loc.Get(rule.Action switch
    {
        ApprovalRuleAction.Ask => "Permissions_PreviewAsk",
        ApprovalRuleAction.Never => "Permissions_PreviewNever",
        _ => "Permissions_PreviewAllow"
    }, Describe(rule));
}

public partial class SettingsViewModel
{
    /// <summary>Permissions (nav: Settings, listed after Chat). The next free index, as Memories took 8.</summary>
    public const int PermissionsPageIndex = 9;

    /// <summary>Raised when what chats may do changed, so live sessions take it on.</summary>
    public event Action? ApprovalPolicyChanged;

    public IReadOnlyList<ApprovalModeOption> ApprovalModeOptions { get; } = ApprovalModeOption.CreateAll();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultApprovalModeModified))]
    private ApprovalMode _defaultApprovalMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwayApprovalBehaviorModified))]
    private bool _skipApprovalsWhenAway;

    private List<ApprovalModeCard>? _defaultModeCards;

    public IReadOnlyList<ApprovalModeCard> DefaultModeCards
        => _defaultModeCards ??= ApprovalModeCard.CreateAll(ApprovalModeOptions, DefaultApprovalMode, mode => DefaultApprovalMode = mode);

    public bool IsDefaultApprovalModeModified => DefaultApprovalMode != _defaults.DefaultApprovalMode;
    public bool IsAwayApprovalBehaviorModified => SkipApprovalsWhenAway != (_defaults.AwayApprovalBehavior == AwayApprovalBehavior.Skip);

    public ObservableCollection<ApprovalRuleRowViewModel> ApprovalRuleRows { get; } = [];
    public ObservableCollection<ApprovalRuleRowViewModel> RememberedApprovalRows { get; } = [];
    public bool HasApprovalRules => ApprovalRuleRows.Count > 0;
    public bool HasRememberedApprovals => RememberedApprovalRows.Count > 0;

    // ── Rule builder ──
    public IReadOnlyList<ApprovalChoiceOption<ApprovalRuleKind>> RuleKindOptions { get; } =
    [
        new(ApprovalRuleKind.Command, Loc.Get("Permissions_KindCommand")),
        new(ApprovalRuleKind.EditFiles, Loc.Get("Permissions_KindEdit")),
        new(ApprovalRuleKind.ReadFiles, Loc.Get("Permissions_KindRead")),
        new(ApprovalRuleKind.Connector, Loc.Get("Permissions_KindConnector")),
        new(ApprovalRuleKind.LumiTool, Loc.Get("Permissions_KindLumiTool")),
    ];

    public IReadOnlyList<ApprovalChoiceOption<string>> LumiToolGroupOptions { get; } =
        ApprovalPolicy.LumiToolGroups.Select(group => new ApprovalChoiceOption<string>(group, Loc.Get("Approval_GroupOption_" + group))).ToList();

    public ObservableCollection<string> RuleConnectorOptions { get; } = [];

    /// <summary>Where a new rule applies: everywhere, or one project.</summary>
    public ObservableCollection<ApprovalChoiceOption<Guid?>> RuleScopeOptions { get; } = [];

    [ObservableProperty] private ApprovalChoiceOption<Guid?>? _newRuleScope;

    [ObservableProperty] private bool _isAddingApprovalRule;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRulePreview))]
    private ApprovalRuleAction _newRuleAction = ApprovalRuleAction.Ask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRulePreview))]
    [NotifyPropertyChangedFor(nameof(NewRuleUsesText))]
    [NotifyPropertyChangedFor(nameof(NewRuleUsesGroup))]
    [NotifyPropertyChangedFor(nameof(NewRuleUsesConnector))]
    [NotifyPropertyChangedFor(nameof(NewRuleTargetLabel))]
    [NotifyPropertyChangedFor(nameof(NewRuleTargetPlaceholder))]
    private ApprovalChoiceOption<ApprovalRuleKind>? _newRuleKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRulePreview))]
    private string _newRuleTarget = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRulePreview))]
    private ApprovalChoiceOption<string>? _newRuleGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRulePreview))]
    private string? _newRuleConnector;

    public bool NewRuleUsesText => NewRuleKind?.Value is ApprovalRuleKind.Command or ApprovalRuleKind.EditFiles or ApprovalRuleKind.ReadFiles;
    public bool NewRuleUsesGroup => NewRuleKind?.Value == ApprovalRuleKind.LumiTool;
    public bool NewRuleUsesConnector => NewRuleKind?.Value == ApprovalRuleKind.Connector;

    public string NewRuleTargetLabel => Loc.Get(NewRuleKind?.Value == ApprovalRuleKind.Command
        ? "Permissions_TargetCommand" : "Permissions_TargetPath");

    public string NewRuleTargetPlaceholder => Loc.Get(NewRuleKind?.Value == ApprovalRuleKind.Command
        ? "Permissions_TargetCommandPlaceholder" : "Permissions_TargetPathPlaceholder");

    public bool IsNewRuleAllow { get => NewRuleAction == ApprovalRuleAction.Allow; set { if (value) NewRuleAction = ApprovalRuleAction.Allow; } }
    public bool IsNewRuleAsk { get => NewRuleAction == ApprovalRuleAction.Ask; set { if (value) NewRuleAction = ApprovalRuleAction.Ask; } }
    public bool IsNewRuleNever { get => NewRuleAction == ApprovalRuleAction.Never; set { if (value) NewRuleAction = ApprovalRuleAction.Never; } }

    partial void OnNewRuleActionChanged(ApprovalRuleAction value)
    {
        OnPropertyChanged(nameof(IsNewRuleAllow));
        OnPropertyChanged(nameof(IsNewRuleAsk));
        OnPropertyChanged(nameof(IsNewRuleNever));
        AddApprovalRuleCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewRuleKindChanged(ApprovalChoiceOption<ApprovalRuleKind>? value) => AddApprovalRuleCommand.NotifyCanExecuteChanged();
    partial void OnNewRuleConnectorChanged(string? value) => AddApprovalRuleCommand.NotifyCanExecuteChanged();
    partial void OnNewRuleGroupChanged(ApprovalChoiceOption<string>? value) => AddApprovalRuleCommand.NotifyCanExecuteChanged();

    public string NewRulePreview => BuildNewRule() is { } rule ? ApprovalRuleText.Preview(rule) : "";

    private ApprovalRule? BuildNewRule()
    {
        if (NewRuleKind is not { } kind)
            return null;
        var target = kind.Value switch
        {
            ApprovalRuleKind.LumiTool => NewRuleGroup?.Value,
            ApprovalRuleKind.Connector => NewRuleConnector,
            _ => NewRuleTarget
        };
        if (kind.Value == ApprovalRuleKind.Connector && string.IsNullOrWhiteSpace(target))
            return null;
        return new ApprovalRule { Action = NewRuleAction, Kind = kind.Value, Target = string.IsNullOrWhiteSpace(target) ? null : target.Trim() };
    }

    private void InitializePermissions(UserSettings settings)
    {
        // Loading, not changing: skip the change handlers that save and re-apply the policy.
#pragma warning disable MVVMTK0034
        _defaultApprovalMode = settings.DefaultApprovalMode;
        _skipApprovalsWhenAway = settings.AwayApprovalBehavior == AwayApprovalBehavior.Skip;
#pragma warning restore MVVMTK0034
        RefreshApprovalRules();
    }

    partial void OnDefaultApprovalModeChanged(ApprovalMode value)
    {
        _dataStore.Data.Settings.DefaultApprovalMode = value;
        foreach (var card in DefaultModeCards)
            card.IsSelected = card.Mode == value;
        Save();
        NotifyModified();
        ApprovalPolicyChanged?.Invoke();
    }

    public bool IsSkipWhenAway { get => SkipApprovalsWhenAway; set { if (value) SkipApprovalsWhenAway = true; } }
    public bool IsWaitWhenAway { get => !SkipApprovalsWhenAway; set { if (value) SkipApprovalsWhenAway = false; } }

    partial void OnSkipApprovalsWhenAwayChanged(bool value)
    {
        _dataStore.Data.Settings.AwayApprovalBehavior = value ? AwayApprovalBehavior.Skip : AwayApprovalBehavior.Wait;
        OnPropertyChanged(nameof(IsSkipWhenAway));
        OnPropertyChanged(nameof(IsWaitWhenAway));
        Save();
        NotifyModified();
    }

    [RelayCommand]
    private void SelectAwayBehavior(string? value) => SkipApprovalsWhenAway = value == "skip";

    [RelayCommand] private void RevertDefaultApprovalMode() => DefaultApprovalMode = _defaults.DefaultApprovalMode;
    [RelayCommand] private void RevertAwayApprovalBehavior() => SkipApprovalsWhenAway = _defaults.AwayApprovalBehavior == AwayApprovalBehavior.Skip;

    /// <summary>Re-reads every rule: yours, the projects', and the approvals chats remembered.</summary>
    public void RefreshApprovalRules()
    {
        ApprovalRuleRows.Clear();
        foreach (var rule in _dataStore.Data.Settings.ApprovalRules)
            ApprovalRuleRows.Add(new ApprovalRuleRowViewModel(rule, ApprovalRuleScope.Everywhere, null, null));
        foreach (var project in _dataStore.Data.Projects)
            foreach (var rule in project.ApprovalRules.Where(rule => !rule.Remembered))
                ApprovalRuleRows.Add(new ApprovalRuleRowViewModel(rule, ApprovalRuleScope.Project, project.Id, project.Name));

        RememberedApprovalRows.Clear();
        foreach (var project in _dataStore.Data.Projects)
            foreach (var rule in project.ApprovalRules.Where(rule => rule.Remembered))
                RememberedApprovalRows.Add(new ApprovalRuleRowViewModel(rule, ApprovalRuleScope.Project, project.Id, project.Name));
        foreach (var chat in _dataStore.Data.Chats)
            foreach (var rule in chat.ApprovalRules)
                RememberedApprovalRows.Add(new ApprovalRuleRowViewModel(rule, ApprovalRuleScope.Chat, chat.Id, chat.Title));

        OnPropertyChanged(nameof(HasApprovalRules));
        OnPropertyChanged(nameof(HasRememberedApprovals));
    }

    [RelayCommand]
    private void StartAddApprovalRule() => StartAddApprovalRuleFor(null);

    /// <summary>Opens the rule builder, for one project when <paramref name="projectId"/> is set.</summary>
    public void StartAddApprovalRuleFor(Guid? projectId)
    {
        RuleConnectorOptions.Clear();
        foreach (var name in _dataStore.Data.McpServers.Select(server => server.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            RuleConnectorOptions.Add(name);
        RuleScopeOptions.Clear();
        RuleScopeOptions.Add(new ApprovalChoiceOption<Guid?>(null, Loc.Get("Permissions_ScopeEverywhere")));
        foreach (var project in _dataStore.Data.Projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
            RuleScopeOptions.Add(new ApprovalChoiceOption<Guid?>(project.Id, Loc.Get("Permissions_ScopeProject", project.Name)));
        NewRuleScope = RuleScopeOptions.FirstOrDefault(option => option.Value == projectId) ?? RuleScopeOptions[0];
        NewRuleAction = ApprovalRuleAction.Ask;
        NewRuleKind = RuleKindOptions[0];
        NewRuleTarget = "";
        NewRuleGroup = LumiToolGroupOptions[0];
        NewRuleConnector = RuleConnectorOptions.FirstOrDefault();
        IsAddingApprovalRule = true;
    }

    [RelayCommand]
    private void CancelAddApprovalRule() => IsAddingApprovalRule = false;

    [RelayCommand]
    private void SelectNewRuleAction(string? value) => NewRuleAction = value switch
    {
        "allow" => ApprovalRuleAction.Allow,
        "never" => ApprovalRuleAction.Never,
        _ => ApprovalRuleAction.Ask
    };

    private bool CanAddApprovalRule() => BuildNewRule() is not null;

    [RelayCommand(CanExecute = nameof(CanAddApprovalRule))]
    private void AddApprovalRule()
    {
        if (BuildNewRule() is not { } rule)
            return;
        var project = NewRuleScope?.Value is { } projectId
            ? _dataStore.Data.Projects.FirstOrDefault(candidate => candidate.Id == projectId)
            : null;
        var rules = project?.ApprovalRules ?? _dataStore.Data.Settings.ApprovalRules;
        // One rule per thing: a new choice for the same command replaces the old one.
        rules.RemoveAll(existing => existing.Kind == rule.Kind
            && string.Equals(ApprovalPolicy.NormalizeTarget(existing), ApprovalPolicy.NormalizeTarget(rule), StringComparison.OrdinalIgnoreCase));
        rules.Add(rule);
        IsAddingApprovalRule = false;
        ApprovalRulesChanged();
    }

    [RelayCommand]
    private void RemoveApprovalRule(ApprovalRuleRowViewModel? row)
    {
        if (row is null)
            return;
        switch (row.Scope)
        {
            case ApprovalRuleScope.Project:
                _dataStore.Data.Projects.FirstOrDefault(project => project.Id == row.OwnerId)?.ApprovalRules.Remove(row.Rule);
                break;
            case ApprovalRuleScope.Chat:
                if (_dataStore.Data.Chats.FirstOrDefault(chat => chat.Id == row.OwnerId) is { } chat)
                {
                    chat.ApprovalRules.Remove(row.Rule);
                    _dataStore.MarkChatChanged(chat);
                }
                break;
            default:
                _dataStore.Data.Settings.ApprovalRules.Remove(row.Rule);
                break;
        }
        ApprovalRulesChanged();
    }

    private void ApprovalRulesChanged()
    {
        Save();
        RefreshApprovalRules();
        ApprovalPolicyChanged?.Invoke();
    }

    private void ResetPermissions(UserSettings defaults)
    {
        DefaultApprovalMode = defaults.DefaultApprovalMode;
        SkipApprovalsWhenAway = defaults.AwayApprovalBehavior == AwayApprovalBehavior.Skip;
    }
}
