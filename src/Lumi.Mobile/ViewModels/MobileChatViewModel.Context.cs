using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrataTheme.Controls;

namespace Lumi.Mobile.ViewModels;

/// <summary>What a context picker chooses for the chat: who Lumi is, where the chat lives, and what it may use.</summary>
public enum ContextPickerKind
{
    Agent,
    Project,
    Skills,
    Connectors
}

/// <summary>One row of a context picker: an agent, project, skill or connector the PC offers.</summary>
public sealed partial class ContextOption : ObservableObject
{
    internal ContextOption(
        ContextPickerKind kind,
        string name,
        string label,
        string? value,
        string? glyph,
        string? description,
        bool isNone = false,
        bool isEnabled = true)
    {
        Kind = kind;
        Name = name;
        Label = label;
        Value = string.IsNullOrWhiteSpace(value) ? null : value;
        Glyph = glyph ?? "";
        Description = description;
        IsNone = isNone;
        IsEnabled = isEnabled;
    }

    public ContextPickerKind Kind { get; }

    /// <summary>The name the desktop knows it by; empty for the row that clears an agent or project.</summary>
    public string Name { get; }

    public string Label { get; }

    public string? Value { get; }

    public string Glyph { get; }

    public string? Description { get; }

    /// <summary>The "No agent" / "No project" row.</summary>
    public bool IsNone { get; }

    public bool IsEnabled { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>Agents and skills wear their own emoji, as in Customize; the rest their kind's icon.</summary>
    public bool UsesGlyph => !IsNone && Kind is ContextPickerKind.Agent or ContextPickerKind.Skills;

    public bool IsProject => !IsNone && Kind == ContextPickerKind.Project;

    public bool IsConnector => Kind == ContextPickerKind.Connectors;

    /// <summary>
    /// The chosen agent or project wears the sheet's selection band, as a single choice does; skills and
    /// connectors, of which a chat often has several on, show only their check so the list stays calm.
    /// </summary>
    public bool IsHighlighted => IsSelected && Kind is ContextPickerKind.Agent or ContextPickerKind.Project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isSelected;
}

/// <summary>
/// What the next message carries: the chat's project, agent and skills, shown as chips as the desktop's
/// composer shows them, and its connectors, kept behind + as the desktop keeps them behind its Connectors
/// button. The + reaches all four through sheets instead of the desktop's anchored menus. Every change
/// goes through the same selection properties and chip collections the composer's own triggers use, so
/// the PC hears about it the same way.
/// </summary>
public sealed partial class MobileChatViewModel
{
    /// <summary>The composer's +: files and photos, then the agent, project, skills and connectors.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isAddSheetOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSheet))]
    private bool _isContextPickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextPickerTitle))]
    [NotifyPropertyChangedFor(nameof(ContextPickerDescription))]
    [NotifyPropertyChangedFor(nameof(ContextPickerEmptyText))]
    private ContextPickerKind _contextPickerKind;

    /// <summary>Whether the project can change now; read when a sheet opens, as the rule depends on the chat's history.</summary>
    [ObservableProperty] private bool _canPickProject = true;

    public ObservableCollection<ContextOption> ContextOptions { get; } = [];

    public bool HasAgent => !string.IsNullOrWhiteSpace(AgentName);

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectName);

    /// <summary>
    /// Whether the composer shows chips: the project, agent and skills, as the desktop's composer does.
    /// Connectors are tools the chat may use rather than what a message carries; the desktop keeps them
    /// behind its Connectors button, and the phone behind +.
    /// </summary>
    public bool HasComposerContext => HasAgent || HasProject || SkillChips.Count > 0;

    public string AgentSummary => HasAgent ? AgentName! : "None";

    public string ProjectSummary => HasProject ? ProjectName! : "None";

    public string SkillsSummary => Summarize(SkillChips, "skills");

    public string ConnectorsSummary => Summarize(McpChips, "connectors");

    public string ContextPickerTitle => ContextPickerKind switch
    {
        ContextPickerKind.Agent => "Agent",
        ContextPickerKind.Project => "Project",
        ContextPickerKind.Skills => "Skills",
        _ => "Connectors"
    };

    public string ContextPickerDescription => ContextPickerKind switch
    {
        ContextPickerKind.Agent => "An agent brings its own instructions, skills and tools.",
        ContextPickerKind.Project => "Where this chat lives, and whose instructions it follows.",
        ContextPickerKind.Skills => "What Lumi can reach for in this chat.",
        _ => "Apps and services Lumi can use in this chat."
    };

    public bool IsContextPickerEmpty => ContextOptions.All(static option => option.IsNone);

    public string ContextPickerEmptyText => ContextPickerKind switch
    {
        ContextPickerKind.Agent => "Your PC has no agents yet. Create one in Customize.",
        ContextPickerKind.Project => "Your PC has no projects yet. Create one in Customize.",
        ContextPickerKind.Skills => "Your PC has no skills yet. Create one in Customize.",
        _ => "No connectors are on. Turn one on in Customize to use it here."
    };

    private void InitializeComposerContext()
    {
        SkillChips.CollectionChanged += OnContextChipsChanged;
        McpChips.CollectionChanged += OnContextChipsChanged;
        AvailableAgents.CollectionChanged += OnContextCatalogChanged;
        AvailableProjects.CollectionChanged += OnContextCatalogChanged;
        AvailableSkills.CollectionChanged += OnContextCatalogChanged;
        AvailableMcps.CollectionChanged += OnContextCatalogChanged;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(AgentName) or nameof(AgentValue):
                OnPropertyChanged(nameof(HasAgent));
                OnPropertyChanged(nameof(AgentSummary));
                OnPropertyChanged(nameof(HasComposerContext));
                SyncContextSelection();
                break;
            case nameof(ProjectName) or nameof(ProjectValue):
                OnPropertyChanged(nameof(HasProject));
                OnPropertyChanged(nameof(ProjectSummary));
                OnPropertyChanged(nameof(HasComposerContext));
                SyncContextSelection();
                break;
        }
    }

    private void OnContextChipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasComposerContext));
        OnPropertyChanged(ReferenceEquals(sender, SkillChips) ? nameof(SkillsSummary) : nameof(ConnectorsSummary));
        SyncContextSelection();
    }

    private void OnContextCatalogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsContextPickerOpen)
            RefreshContextOptions();
    }

    [RelayCommand]
    private void OpenAddSheet()
    {
        CanPickProject = CanChangeProjectSelection;
        IsAddSheetOpen = true;
    }

    [RelayCommand]
    private void AttachFromAddSheet()
    {
        IsAddSheetOpen = false;
        PickAttachment();
    }

    /// <summary>Opens the picker for one kind, from the + sheet or from the chip that names it.</summary>
    [RelayCommand]
    private void OpenContextPicker(string? kind)
    {
        if (!Enum.TryParse<ContextPickerKind>(kind, ignoreCase: true, out var parsed))
            return;

        CanPickProject = CanChangeProjectSelection;
        ContextPickerKind = parsed;
        RefreshContextOptions();
        IsAddSheetOpen = false;
        IsContextPickerOpen = true;
    }

    /// <summary>
    /// An agent or project is one choice, so picking it closes the sheet; skills and connectors switch on
    /// and off where they are, as the desktop's pickers do.
    /// </summary>
    [RelayCommand]
    private void PickContextOption(ContextOption? option)
    {
        if (option is null || !option.IsEnabled)
            return;

        switch (option.Kind)
        {
            case ContextPickerKind.Agent:
                if (option.IsNone)
                    _ = RemoveAgent();
                else if (!option.IsSelected)
                {
                    AgentGlyph = string.IsNullOrWhiteSpace(option.Glyph) ? "◉" : option.Glyph;
                    AgentValue = option.Value;
                    AgentName = option.Name;
                }
                IsContextPickerOpen = false;
                break;
            case ContextPickerKind.Project:
                if (!CanChangeProjectSelection)
                    return;
                if (option.IsNone)
                    _ = RemoveProject();
                else if (!option.IsSelected)
                {
                    ProjectValue = option.Value;
                    ProjectName = option.Name;
                }
                IsContextPickerOpen = false;
                break;
            case ContextPickerKind.Skills:
                if (option.IsSelected)
                    _ = RemoveSkill(option.Name);
                else
                    AddPendingChip(SkillChips, AvailableSkills, option.Name, "✦");
                break;
            case ContextPickerKind.Connectors:
                if (option.IsSelected)
                    _ = RemoveMcp(option.Name);
                else
                    AddPendingChip(McpChips, AvailableMcps, option.Name, "⚙");
                break;
        }

        SyncContextSelection();
    }

    private void RefreshContextOptions()
    {
        ContextOptions.Clear();
        switch (ContextPickerKind)
        {
            case ContextPickerKind.Agent:
                ContextOptions.Add(new ContextOption(
                    ContextPickerKind.Agent, "", "No agent", null, null, "Lumi on its own", isNone: true));
                AddCatalog(AvailableAgents, ContextPickerKind.Agent, enabled: true);
                if (HasAgent && !ContextOptions.Any(option => !option.IsNone && IsContextOptionSelected(option)))
                    ContextOptions.Insert(1, new ContextOption(
                        ContextPickerKind.Agent, AgentName!, AgentName!, AgentValue, AgentGlyph, null));
                break;
            case ContextPickerKind.Project:
                ContextOptions.Add(new ContextOption(
                    ContextPickerKind.Project, "", "No project", null, null, "A chat of its own",
                    isNone: true, isEnabled: CanPickProject));
                AddCatalog(AvailableProjects, ContextPickerKind.Project, enabled: CanPickProject);
                if (HasProject && !ContextOptions.Any(option => !option.IsNone && IsContextOptionSelected(option)))
                    ContextOptions.Insert(1, new ContextOption(
                        ContextPickerKind.Project, ProjectName!, ProjectName!, ProjectValue, null, null,
                        isEnabled: CanPickProject));
                break;
            case ContextPickerKind.Skills:
                AddChipsMissingFromCatalog(SkillChips, AvailableSkills, ContextPickerKind.Skills);
                AddCatalog(AvailableSkills, ContextPickerKind.Skills, enabled: true);
                break;
            case ContextPickerKind.Connectors:
                AddChipsMissingFromCatalog(McpChips, AvailableMcps, ContextPickerKind.Connectors);
                AddCatalog(AvailableMcps, ContextPickerKind.Connectors, enabled: true);
                break;
        }

        SyncContextSelection();
        OnPropertyChanged(nameof(IsContextPickerEmpty));

        void AddCatalog(IEnumerable<StrataComposerChip> catalog, ContextPickerKind kind, bool enabled)
        {
            foreach (var chip in catalog)
            {
                ContextOptions.Add(new ContextOption(
                    kind, chip.Name, chip.Name, chip.Value, chip.Glyph, chip.SecondaryText, isEnabled: enabled));
            }
        }

        // A skill or connector the chat already uses stays listed even when the catalog doesn't offer it
        // (one a plugin brought, or one switched off since), so it can still be taken off.
        void AddChipsMissingFromCatalog(
            IEnumerable<StrataComposerChip> chips,
            IReadOnlyCollection<StrataComposerChip> catalog,
            ContextPickerKind kind)
        {
            foreach (var chip in chips)
            {
                if (!catalog.Any(known => SameName(known.Name, chip.Name)))
                    ContextOptions.Add(new ContextOption(kind, chip.Name, chip.Name, chip.Value, chip.Glyph, null));
            }
        }
    }

    private void SyncContextSelection()
    {
        foreach (var option in ContextOptions)
            option.IsSelected = IsContextOptionSelected(option);
    }

    private bool IsContextOptionSelected(ContextOption option) => option.Kind switch
    {
        ContextPickerKind.Agent => option.IsNone ? !HasAgent : Matches(option, AgentValue, AgentName),
        ContextPickerKind.Project => option.IsNone ? !HasProject : Matches(option, ProjectValue, ProjectName),
        ContextPickerKind.Skills => SkillChips.Any(chip => SameName(chip.Name, option.Name)),
        _ => McpChips.Any(chip => SameName(chip.Name, option.Name))
    };

    /// <summary>A selection matches by id when both sides know one, otherwise by name.</summary>
    private static bool Matches(ContextOption option, string? selectedValue, string? selectedName) =>
        !string.IsNullOrWhiteSpace(selectedValue) && option.Value is { } value
            ? string.Equals(value, selectedValue, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrWhiteSpace(selectedName) && SameName(option.Name, selectedName);

    private static bool SameName(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Summarize(IReadOnlyList<StrataComposerChip> chips, string plural) => chips.Count switch
    {
        0 => "None",
        1 => chips[0].Name,
        _ => $"{chips.Count} {plural}"
    };
}
