using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.ViewModels;

/// <summary>The library tabs, in the order they appear in the segmented picker.</summary>
public enum LibrarySection
{
    Projects,
    Skills,
    Lumis,
    Memories,
    McpServers,
    Jobs
}

/// <summary>
/// Which of the desktop's places the Library page is serving. Customize gathers agents, skills,
/// connectors and projects; Jobs and Memories (a Settings page) show one section each. All keeps
/// every section, the shape the page had before it was split into places.
/// </summary>
public enum LibraryScope
{
    All,
    Customize,
    Jobs,
    Memories
}

/// <summary>One section chip in the current scope, with its live item count.</summary>
public sealed partial class LibrarySectionOption : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _count;

    public required LibrarySection Section { get; init; }

    public required string Name { get; init; }

    public bool IsAgents => Section == LibrarySection.Lumis;

    public bool IsSkills => Section == LibrarySection.Skills;

    public bool IsConnectors => Section == LibrarySection.McpServers;

    public bool IsProjects => Section == LibrarySection.Projects;
}

/// <summary>
/// One editable library row. A single view model covers every resource because the desktop's
/// <c>LumiFeatureManager</c> already exposes a uniform CRUD shape — the phone just fills the fields
/// the selected resource uses.
/// </summary>
public sealed partial class LibraryEntryViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionsLabel))]
    private string _name = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _glyph = "•";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(ShowBadge))]
    private string? _badge;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(HasActions))]
    private bool _isBuiltIn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledStateText))]
    private bool _isEnabled = true;

    /// <summary>Open in the editor beside the list, so its row reads as the selected one.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The row's second line: what it does, or when a job runs and how it last went.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
    private string? _subtitle;

    /// <summary>What the item is, under its name on its own page ("Agent · Built in").</summary>
    [ObservableProperty] private string? _kind;

    /// <summary>The label for the item's full content on its page ("Instructions", "Command").</summary>
    [ObservableProperty] private string _bodyLabel = "Details";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFacts))]
    private IReadOnlyList<LibraryFact> _facts = [];

    /// <summary>A job's own chat on the PC, which its page can open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenChat))]
    private Guid? _chatId;

    /// <summary>Set on the first row of each group, such as Yours, Built in, On or Paused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupLabel))]
    private string? _groupLabel;

    /// <summary>The row opens its group, so a card rounds its top and draws no divider above it.</summary>
    [ObservableProperty] private bool _isGroupStart;

    /// <summary>The row closes its group, so a card rounds its bottom.</summary>
    [ObservableProperty] private bool _isGroupEnd;

    /// <summary>The switch is waiting for the PC to confirm a change.</summary>
    [ObservableProperty] private bool _isSwitchBusy;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public bool HasGroupLabel => !string.IsNullOrEmpty(GroupLabel);

    public bool HasFacts => Facts.Count > 0;

    public bool CanOpenChat => ChatId is { } id && id != Guid.Empty;

    /// <summary>Connectors and jobs are switched on and off right on their row.</summary>
    public bool HasSwitch => Section is LibrarySection.McpServers or LibrarySection.Jobs;

    public bool ShowBadge => HasBadge && !HasSwitch;

    public string SwitchLabel => $"{Name}: {(IsEnabled ? "on" : "off")}";

    /// <summary>Agents and skills wear their own emoji; the other sections their line icon.</summary>
    public bool UsesGlyph => Section is LibrarySection.Skills or LibrarySection.Lumis;

    public bool IsProject => Section == LibrarySection.Projects;

    public bool IsMemory => Section == LibrarySection.Memories;

    public bool IsConnector => Section == LibrarySection.McpServers;

    public bool IsJob => Section == LibrarySection.Jobs;

    /// <summary>Why an item that opens read-only can't be edited here.</summary>
    public string? ReadOnlyNote => CanEdit ? null
        : IsBuiltIn ? "Built into Lumi. To change it, make a copy on your PC."
        : Section switch
        {
            LibrarySection.McpServers => "Connectors are set up on your PC. You can turn this one on or off here.",
            LibrarySection.Jobs => "Jobs are edited on your PC. You can turn this one on or off here.",
            _ => null
        };

    /// <summary>Puts the switch back where the PC says it is, after a change the PC refused.</summary>
    internal void RefreshEnabled() => OnPropertyChanged(nameof(IsEnabled));

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(SwitchLabel));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(SwitchLabel));

    public required LibrarySection Section { get; init; }

    public required string Identifier { get; init; }

    public bool CanEdit =>
        !IsBuiltIn
        && Section is LibrarySection.Projects
            or LibrarySection.Skills
            or LibrarySection.Lumis
            or LibrarySection.Memories;

    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);

    public bool HasActions => CanEdit || Section is LibrarySection.McpServers or LibrarySection.Jobs;

    public string ActionsLabel => $"Actions for {Name}";

    public bool HasEnabledState => Section is LibrarySection.McpServers or LibrarySection.Jobs;

    public string EnabledStateText => IsEnabled ? "Enabled" : "Disabled";

    internal void UpdateFrom(LibraryEntryViewModel source)
    {
        Name = source.Name;
        Description = source.Description;
        Glyph = source.Glyph;
        Badge = source.Badge;
        IsBuiltIn = source.IsBuiltIn;
        if (!IsSwitchBusy)
            IsEnabled = source.IsEnabled;
        Subtitle = source.Subtitle;
        Kind = source.Kind;
        BodyLabel = source.BodyLabel;
        Facts = source.Facts;
        ChatId = source.ChatId;
    }
}

/// <summary>One labelled fact on an item's page, such as a job's schedule.</summary>
public sealed record LibraryFact(string Label, string Value, bool IsFirst);

/// <summary>
/// Projects / Skills / Lumis / Memories / MCP servers / Jobs, backed entirely by
/// <c>configure_feature</c> so the phone reuses the desktop's real CRUD path.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IRemoteCommandSink _sink;
    private RemoteLibrary _library = new();
    private long _editorGeneration;
    private long _actionGeneration;
    private LibrarySection _section = LibrarySection.Projects;
    private LibrarySection? _pendingSection;
    private (string Name, string Description, string Body, string Glyph, string Workspace) _originalDraft =
        ("", "", "", "", "");

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private LibraryEntryViewModel? _selectedEntry;

    partial void OnSelectedEntryChanged(LibraryEntryViewModel? oldValue, LibraryEntryViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsOpen = false;
        if (newValue is not null)
            newValue.IsOpen = true;
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSurface), nameof(PageTitle), nameof(ShowCreateButton),
        nameof(CanEditFields), nameof(HasUnsavedChanges), nameof(IsDetailOpen))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BeginCreateCommand))]
    private bool _isEditing;

    // ── Read-only pages: built-in agents and skills, connectors and jobs ──────────────────────

    private long _viewGeneration;

    /// <summary>An item open on its own read-only page. Every row opens something.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewing), nameof(IsDetailOpen), nameof(HasOpenSurface))]
    private LibraryEntryViewModel? _viewedEntry;

    partial void OnViewedEntryChanged(LibraryEntryViewModel? oldValue, LibraryEntryViewModel? newValue)
    {
        if (oldValue is not null && !ReferenceEquals(oldValue, SelectedEntry))
            oldValue.IsOpen = false;
        if (newValue is not null)
            newValue.IsOpen = true;
    }

    /// <summary>The item's full content, loaded from the PC when its page opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewBody))]
    private string? _viewBody;

    [ObservableProperty] private bool _isViewLoading;
    [ObservableProperty] private string? _viewStatus;

    public bool IsViewing => ViewedEntry is not null;

    public bool HasViewBody => !string.IsNullOrWhiteSpace(ViewBody);

    /// <summary>Something is open in the detail pane: the editor or a read-only page.</summary>
    public bool IsDetailOpen => IsEditing || IsViewing;

    /// <summary>Raised when an item's page asks to open its chat, such as a job's.</summary>
    public event Action<Guid, string>? ChatRequested;
    [ObservableProperty] private string? _statusMessage;

    // Editor fields — reused across resources, only the relevant ones are shown.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges), nameof(DiscardDescription))]
    private string _editName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _editDescription = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _editBody = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _editGlyph = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _editWorkingDirectory = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(PageTitle))]
    private bool _isCreating;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFields), nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RetryEditorCommand))]
    private bool _isEditorLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFields))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RetryEditorCommand))]
    private bool _hasEditorLoadFailed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSurface))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDiscardConfirmationOpen;

    public LibraryViewModel(IRemoteCommandSink sink)
    {
        _sink = sink;
        SaveCommand.PropertyChanged += OnOperationStateChanged;
        ToggleEnabledCommand.PropertyChanged += OnOperationStateChanged;
        ConfirmDeleteActionEntryCommand.PropertyChanged += OnOperationStateChanged;
        RebuildScopeSections();
    }

    // ── Scope: the place this page is serving ────────────────────────────────────────────────

    private LibraryScope _scope = LibraryScope.All;
    private LibraryScope? _pendingScope;
    private readonly Dictionary<LibraryScope, LibrarySection> _lastSectionByScope = [];

    public LibraryScope Scope => _scope;

    /// <summary>Raised after the scope changes, including one applied once unsaved edits are discarded.</summary>
    public event Action? ScopeChanged;

    /// <summary>The sections of the current scope, in the desktop's Customize order.</summary>
    public ObservableCollection<LibrarySectionOption> ScopeSections { get; } = [];

    public bool ShowSectionChips => ScopeSections.Count > 1;

    public string ScopeTitle => _scope switch
    {
        LibraryScope.Customize => "Customize",
        LibraryScope.Jobs => "Jobs",
        LibraryScope.Memories => "Memories",
        _ => "Library"
    };

    /// <summary>The line under Jobs' title, as on the desktop's Jobs page: how many run and how many wait.</summary>
    public string? ScopeSummary => _scope == LibraryScope.Jobs && _library.Jobs.Count > 0
        ? $"{_library.Jobs.Count(job => job.IsEnabled)} on · {_library.Jobs.Count(job => !job.IsEnabled)} paused"
        : null;

    /// <summary>
    /// Moves the page to another place. Unsaved edits are never dropped silently: the discard
    /// confirmation opens instead and the scope follows only if the user discards.
    /// </summary>
    public bool RequestScope(LibraryScope scope)
    {
        if (scope == _scope)
            return true;

        if (HasUnsavedChanges)
        {
            _pendingScope = scope;
            IsDiscardConfirmationOpen = true;
            return false;
        }

        if (IsActionBusy)
            return false;

        ApplyScope(scope);
        return true;
    }

    private static IReadOnlyList<LibrarySection> SectionsFor(LibraryScope scope) => scope switch
    {
        LibraryScope.Customize =>
            [LibrarySection.Lumis, LibrarySection.Skills, LibrarySection.McpServers, LibrarySection.Projects],
        LibraryScope.Jobs => [LibrarySection.Jobs],
        LibraryScope.Memories => [LibrarySection.Memories],
        _ =>
        [
            LibrarySection.Projects, LibrarySection.Skills, LibrarySection.Lumis,
            LibrarySection.Memories, LibrarySection.McpServers, LibrarySection.Jobs
        ]
    };

    private void ApplyScope(LibraryScope scope)
    {
        _lastSectionByScope[_scope] = Section;
        _scope = scope;
        _pendingScope = null;
        EndEdit();
        CloseView();
        IsRowActionsOpen = false;
        SearchText = "";
        RebuildScopeSections();

        var sections = SectionsFor(scope);
        var target = _lastSectionByScope.TryGetValue(scope, out var remembered) && sections.Contains(remembered)
            ? remembered
            : sections[0];
        if (Section != target)
            Section = target;
        else
            OnSectionChanged();

        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(ShowSectionChips));
        OnPropertyChanged(nameof(ShowBackButton));
        OnPropertyChanged(nameof(ShowPlaces));
        OnPropertyChanged(nameof(ScopeSectionIndex));
        ScopeChanged?.Invoke();
    }

    private void RebuildScopeSections()
    {
        ScopeSections.Clear();
        foreach (var section in SectionsFor(_scope))
        {
            ScopeSections.Add(new LibrarySectionOption
            {
                Section = section,
                Name = SectionNames[(int)section],
                IsSelected = section == _section,
                Count = CountOf(section)
            });
        }
    }

    private int CountOf(LibrarySection section) => section switch
    {
        LibrarySection.Projects => _library.Projects.Count,
        LibrarySection.Skills => _library.Skills.Count,
        LibrarySection.Lumis => _library.Lumis.Count,
        LibrarySection.Memories => _library.Memories.Count,
        LibrarySection.McpServers => _library.McpServers.Count,
        LibrarySection.Jobs => _library.Jobs.Count,
        _ => 0
    };

    public LibrarySection Section
    {
        get => _section;
        set
        {
            if (_section == value || !Enum.IsDefined(value) || IsActionBusy)
                return;

            if (HasUnsavedChanges)
            {
                _pendingSection = value;
                IsDiscardConfirmationOpen = true;
                OnPropertyChanged(nameof(SectionIndex));
                OnPropertyChanged(nameof(ScopeSectionIndex));
                return;
            }

            EndEdit();
            CloseView();
            IsRowActionsOpen = false;
            SetProperty(ref _section, value);
            OnSectionChanged();
        }
    }

    public string EditorTitle => $"{(IsCreating ? "New" : "Edit")} {SingularName(Section)}";
    public string PageTitle => IsEditing ? EditorTitle : ScopeTitle;
    public bool ShowCreateButton => CanCreate && !IsEditing;
    public bool IsSaving => SaveCommand.IsRunning;
    public string SaveButtonText => IsSaving ? "Saving…" : "Save";
    public bool CanEditFields => IsEditing && !IsEditorLoading && !HasEditorLoadFailed && !IsSaving;
    public bool HasUnsavedChanges => IsEditing && !IsEditorLoading && CurrentDraft != _originalDraft;

    public string DiscardDescription =>
        $"Your changes to “{(string.IsNullOrWhiteSpace(EditName) ? EditorTitle : EditName)}” will be lost. " +
        "Nothing will be deleted from your PC.";

    private (string, string, string, string, string) CurrentDraft =>
        (EditName, EditDescription, EditBody, EditGlyph, EditWorkingDirectory);

    private bool CanBeginCreate => CanCreate && !IsEditing && !IsRowActionsOpen;
    private bool CanSave => CanEditFields && !IsDiscardConfirmationOpen && CanCreate &&
                            (IsCreating || SelectedEntry?.CanEdit == true);
    private bool CanRetryEditor => IsEditing && HasEditorLoadFailed && !IsEditorLoading;

    private void OnOperationStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IAsyncRelayCommand.IsRunning))
            return;

        OnPropertyChanged(nameof(IsSaving));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(CanEditFields));
        OnPropertyChanged(nameof(IsActionBusy));
        SaveCommand.NotifyCanExecuteChanged();
        ConfirmDeleteActionEntryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised when the user dismisses the library page: Memories steps back to Settings, a place's list to the chats.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private void Close()
    {
        if (!DismissTopmostSurface())
            CloseRequested?.Invoke();
    }

    // ── Row actions and their explicit destructive confirmation ──────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSurface))]
    private bool _isRowActionsOpen;

    [ObservableProperty] private LibraryEntryViewModel? _actionEntry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionSheetTitle))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteActionEntryCommand))]
    private bool _isConfirmingDelete;

    public string ActionEntryName => ActionEntry?.Name ?? "";
    public string ActionSheetTitle => IsConfirmingDelete ? "Delete from Library?" : ActionEntryName;
    public bool CanEditActionEntry => ActionEntry?.CanEdit == true;
    public bool CanDeleteActionEntry => ActionEntry?.CanEdit == true;
    public bool IsActionBusy => ToggleEnabledCommand.IsRunning || ConfirmDeleteActionEntryCommand.IsRunning;
    public string ToggleActionText =>
        $"{(ActionEntry?.IsEnabled == true ? "Disable" : "Enable")} {SingularName(ActionEntry?.Section ?? Section)}";
    public string DeleteConfirmationDescription =>
        $"Delete “{ActionEntryName}” from your shared Library? " +
        "It will be deleted on your PC as well as this phone. This cannot be undone.";

    /// <summary>Only MCP servers and jobs have an enabled state worth toggling.</summary>
    public bool CanToggleActionEntry =>
        ActionEntry?.Section is LibrarySection.McpServers or LibrarySection.Jobs;

    partial void OnActionEntryChanged(LibraryEntryViewModel? oldValue, LibraryEntryViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnActionEntryPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnActionEntryPropertyChanged;

        _actionGeneration++;
        NotifyActionEntry();
    }

    private void OnActionEntryPropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyActionEntry();

    private void NotifyActionEntry()
    {
        OnPropertyChanged(nameof(ActionEntryName));
        OnPropertyChanged(nameof(ActionSheetTitle));
        OnPropertyChanged(nameof(CanEditActionEntry));
        OnPropertyChanged(nameof(CanDeleteActionEntry));
        OnPropertyChanged(nameof(CanToggleActionEntry));
        OnPropertyChanged(nameof(ToggleActionText));
        OnPropertyChanged(nameof(DeleteConfirmationDescription));
        ConfirmDeleteActionEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRowActionsOpenChanged(bool value)
    {
        if (!value)
        {
            IsConfirmingDelete = false;
            ActionEntry = null;
        }
        BeginCreateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenRowActions(LibraryEntryViewModel? entry)
    {
        if (entry is null || !entry.HasActions || IsEditing || IsActionBusy)
            return;

        ActionEntry = entry;
        IsConfirmingDelete = false;
        StatusMessage = null;
        IsRowActionsOpen = true;
    }

    [RelayCommand]
    private async Task EditActionEntryAsync()
    {
        if (ActionEntry is not { CanEdit: true } entry || IsActionBusy)
            return;

        IsRowActionsOpen = false;
        await BeginEditCommand.ExecuteAsync(entry);
    }

    [RelayCommand]
    private async Task ToggleActionEntryAsync()
    {
        if (!IsConfirmingDelete && ActionEntry is { } entry)
            await ToggleEnabledCommand.ExecuteAsync(entry);
    }

    [RelayCommand]
    private async Task DeleteActionEntryAsync()
    {
        if (ActionEntry is { } entry)
            await DeleteCommand.ExecuteAsync(entry);
    }

    [RelayCommand]
    private void CancelDeleteConfirmation()
    {
        if (!IsActionBusy)
            IsConfirmingDelete = false;
    }

    [RelayCommand]
    private void CloseRowActions()
    {
        if (!IsActionBusy)
            IsRowActionsOpen = false;
    }

    /// <summary>Whether Back should dismiss library-local UI before leaving the page.</summary>
    public bool HasOpenSurface => IsDiscardConfirmationOpen || IsRowActionsOpen || IsEditing || IsViewing;

    internal bool DismissTopmostSurface()
    {
        if (IsDiscardConfirmationOpen)
        {
            KeepEditing();
            return true;
        }

        if (IsRowActionsOpen)
        {
            if (IsConfirmingDelete)
                CancelDeleteConfirmation();
            else
                CloseRowActions();
            return true;
        }

        if (IsEditing)
        {
            CancelEdit();
            return true;
        }

        if (IsViewing)
        {
            CloseView();
            return true;
        }

        return false;
    }

    private ObservableCollection<LibraryEntryViewModel> _entries = [];
    public ObservableCollection<LibraryEntryViewModel> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    public ObservableCollection<string> SectionNames { get; } =
        ["Projects", "Skills", "Agents", "Memories", "Connectors", "Jobs"];

    public int SectionIndex
    {
        get => (int)Section;
        set
        {
            if (value >= 0 && value < SectionNames.Count && (int)Section != value)
                Section = (LibrarySection)value;
        }
    }

    /// <summary>The selected section's position among the current scope's sections.</summary>
    public int ScopeSectionIndex
    {
        get
        {
            for (var i = 0; i < ScopeSections.Count; i++)
            {
                if (ScopeSections[i].Section == Section)
                    return i;
            }

            return -1;
        }
        set
        {
            if (value >= 0 && value < ScopeSections.Count && ScopeSections[value].Section != Section)
                Section = ScopeSections[value].Section;
        }
    }

    /// <summary>The All scope and Memories step back; Jobs and Customize are places, reached from the places.</summary>
    public bool ShowBackButton => _scope is LibraryScope.All or LibraryScope.Memories;

    /// <summary>Memories is a Settings page and keeps its back step; the other places show the places.</summary>
    public bool ShowPlaces => _scope is not (LibraryScope.All or LibraryScope.Memories);

    public bool IsEmpty => Entries.Count == 0;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);
    public bool IsNoResults => IsEmpty && HasSearchQuery;
    public string EmptyTitle => IsNoResults ? $"No {PluralName} found" : $"No {PluralName} yet";
    public string EmptyDescription => IsNoResults
        ? $"Try another name or clear the search to see all {PluralName}."
        : Section switch
        {
            LibrarySection.McpServers => "Add connectors on your PC. You can enable or disable them here.",
            LibrarySection.Jobs => "Create jobs on your PC. You can enable or disable them here.",
            _ => $"Use + to add {(Section == LibrarySection.Lumis ? "an" : "a")} {SingularName(Section)}, or add one on your PC. Your Library is shared."
        };

    /// <summary>The selected section's own name, shown over its list.</summary>
    public string SectionTitle => SectionNames[(int)Section];

    /// <summary>The filter field's prompt, in sentence case like the desktop's ("Search agents").</summary>
    public string SearchPlaceholder => $"Search {PluralName}";

    /// <summary>A short line for the detail pane while nothing is open in it.</summary>
    public string DetailPlaceholder => Section switch
    {
        LibrarySection.McpServers => "Choose a connector to see what it does. Switch it on or off right on its row.",
        LibrarySection.Jobs => "Choose a job to see when it runs and open its chat. Switch it on or off right on its row.",
        _ => $"Choose {(Section == LibrarySection.Lumis ? "an" : "a")} {SingularName(Section)} to open it, or add one with +."
    };

    private string PluralName => SectionNames[SectionIndex].ToLowerInvariant();

    private static string SingularName(LibrarySection section) => section switch
    {
        LibrarySection.Projects => "project",
        LibrarySection.Skills => "skill",
        LibrarySection.Lumis => "agent",
        LibrarySection.Memories => "memory",
        LibrarySection.McpServers => "connector",
        LibrarySection.Jobs => "job",
        _ => "item"
    };

    /// <summary>Only resources with a meaningful phone editor allow creating from mobile.</summary>
    public bool CanCreate => Section is LibrarySection.Projects or LibrarySection.Skills
        or LibrarySection.Lumis or LibrarySection.Memories;

    public string BodyLabel => Section switch
    {
        LibrarySection.Projects => "Instructions",
        LibrarySection.Skills => "Skill content",
        LibrarySection.Lumis => "System prompt",
        LibrarySection.Memories => "Content",
        _ => "Details"
    };

    public bool ShowGlyphEditor => Section is LibrarySection.Skills or LibrarySection.Lumis;

    public bool ShowDescriptionEditor => Section is LibrarySection.Skills or LibrarySection.Lumis;

    public bool ShowProjectWorkingDirectory => Section == LibrarySection.Projects;

    public void Apply(RemoteLibrary library)
    {
        _library = library;
        foreach (var option in ScopeSections)
            option.Count = CountOf(option.Section);
        OnPropertyChanged(nameof(MemoryCount));
        OnPropertyChanged(nameof(MemoryCountLabel));
        Rebuild();
    }

    /// <summary>How many memories Lumi keeps, shown with the profile in Settings.</summary>
    public int MemoryCount => _library.Memories.Count;

    public string MemoryCountLabel => MemoryCount == 1 ? "1 memory" : $"{MemoryCount} memories";

    internal void ResetHostState()
    {
        EndEdit();
        CloseView();
        _actionGeneration++;
        IsRowActionsOpen = false;
        ActionEntry = null;
        SearchText = "";
        StatusMessage = null;
        Apply(new RemoteLibrary());
    }

    private void OnSectionChanged()
    {
        StatusMessage = null;
        foreach (var option in ScopeSections)
            option.IsSelected = option.Section == _section;
        OnPropertyChanged(nameof(SectionIndex));
        OnPropertyChanged(nameof(ScopeSectionIndex));
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(DetailPlaceholder));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(ShowCreateButton));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(BodyLabel));
        OnPropertyChanged(nameof(ShowGlyphEditor));
        OnPropertyChanged(nameof(ShowDescriptionEditor));
        OnPropertyChanged(nameof(ShowProjectWorkingDirectory));
        BeginCreateCommand.NotifyCanExecuteChanged();
        Rebuild();
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private void Rebuild()
    {
        var query = SearchText.Trim();
        var entries = Project().ToList();
        if (!IsActionBusy && ActionEntry is { } action
            && entries.FirstOrDefault(entry => entry.Section == action.Section
                && entry.Identifier == action.Identifier) is { } latest)
        {
            action.IsEnabled = latest.IsEnabled;
        }
        if (ViewedEntry is { } viewed)
        {
            var current = entries.FirstOrDefault(entry => entry.Section == viewed.Section
                && entry.Identifier == viewed.Identifier);
            if (current is null)
                CloseView();
            else
                viewed.UpdateFrom(current);
        }
        var projected = entries.Where(Matches).ToList();

        var previous = Entries.ToDictionary(entry => (entry.Section, entry.Identifier));
        for (var i = 0; i < projected.Count; i++)
        {
            var incoming = projected[i];
            if (!previous.TryGetValue((incoming.Section, incoming.Identifier), out var existing))
                continue;
            if (!IsActionBusy || !ReferenceEquals(existing, ActionEntry))
                existing.UpdateFrom(incoming);
            projected[i] = existing;
        }

        for (var i = 0; i < projected.Count; i++)
        {
            var entry = projected[i];
            var group = GroupOf(entry);
            var starts = i == 0 || group != GroupOf(projected[i - 1]);
            entry.GroupLabel = starts ? group : null;
            entry.IsGroupStart = starts;
            entry.IsGroupEnd = i == projected.Count - 1 || group != GroupOf(projected[i + 1]);
        }

        // Publish a changed page once. Replaying hundreds of Add notifications builds off-screen
        // templates on the UI thread; an unchanged snapshot should not disturb the current rows.
        if (!Entries.SequenceEqual(projected))
            Entries = new ObservableCollection<LibraryEntryViewModel>(projected);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(IsNoResults));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(ScopeSummary));
        return;

        static string? GroupOf(LibraryEntryViewModel entry) => entry.Section switch
        {
            LibrarySection.Lumis or LibrarySection.Skills => entry.IsBuiltIn ? "Built in" : "Yours",
            LibrarySection.Jobs => entry.IsEnabled ? "On" : "Paused",
            LibrarySection.McpServers => entry.IsEnabled ? "On" : "Off",
            _ => null
        };

        bool Matches(LibraryEntryViewModel entry) =>
            query.Length == 0 ||
            entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (entry.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private IEnumerable<LibraryEntryViewModel> Project() => Section switch
    {
        LibrarySection.Projects => _library.Projects.Select(p => new LibraryEntryViewModel
        {
            Section = LibrarySection.Projects,
            Identifier = p.Id.ToString(),
            Name = p.Name,
            Description = p.Instructions,
            Subtitle = p.Instructions,
            Glyph = "◆",
            Kind = "Project",
            BodyLabel = "Instructions",
            Badge = p.ChatCount > 0 ? $"{p.ChatCount} {(p.ChatCount == 1 ? "chat" : "chats")}" : null
        }),

        // Yours first, then Lumi's own, each in the PC's order.
        LibrarySection.Skills => _library.Skills.OrderBy(s => s.IsBuiltIn).Select(s => new LibraryEntryViewModel
        {
            Section = LibrarySection.Skills,
            Identifier = s.Id.ToString(),
            Name = s.Name,
            Description = s.Description,
            Subtitle = s.Description,
            Glyph = s.IconGlyph,
            IsBuiltIn = s.IsBuiltIn,
            Kind = s.IsBuiltIn ? "Skill · Built in" : "Skill",
            BodyLabel = "Skill content"
        }),

        LibrarySection.Lumis => _library.Lumis.OrderBy(l => l.IsBuiltIn).Select(l => new LibraryEntryViewModel
        {
            Section = LibrarySection.Lumis,
            Identifier = l.Id.ToString(),
            Name = l.Name,
            Description = l.Description,
            Subtitle = l.Description,
            Glyph = l.IconGlyph,
            IsBuiltIn = l.IsBuiltIn,
            Kind = l.IsBuiltIn ? "Agent · Built in" : "Agent",
            BodyLabel = "Instructions",
            Badge = l.SkillCount > 0 ? $"{l.SkillCount} {(l.SkillCount == 1 ? "skill" : "skills")}" : null,
            Facts = FactList(
                ("Skills", l.SkillCount > 0 ? l.SkillCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : null),
                ("Tools", l.ToolCount > 0 ? l.ToolCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : null))
        }),

        LibrarySection.Memories => _library.Memories.Select(m => new LibraryEntryViewModel
        {
            Section = LibrarySection.Memories,
            Identifier = m.Id.ToString(),
            Name = m.Key,
            Description = m.Content,
            Subtitle = m.Content,
            Glyph = "◇",
            Kind = "Memory",
            BodyLabel = "Content",
            Badge = m.Category
        }),

        LibrarySection.McpServers => _library.McpServers.OrderBy(s => !s.IsEnabled).Select(s => new LibraryEntryViewModel
        {
            Section = LibrarySection.McpServers,
            Identifier = s.Id.ToString(),
            Name = s.Name,
            Description = s.Description,
            Subtitle = s.Description ?? (IsLocalServer(s) ? "Runs on your PC" : "Online service"),
            Glyph = "⬡",
            IsEnabled = s.IsEnabled,
            Kind = IsLocalServer(s) ? "Connector · On your PC" : "Connector · Online",
            BodyLabel = IsLocalServer(s) ? "Command" : "Address",
            Badge = s.ToolCount > 0 ? $"{s.ToolCount} tools" : null,
            Facts = FactList(("Tools", s.ToolCount > 0 ? s.ToolCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : null))
        }),

        LibrarySection.Jobs => _library.Jobs.OrderBy(j => !j.IsEnabled).Select(j => new LibraryEntryViewModel
        {
            Section = LibrarySection.Jobs,
            Identifier = j.Id.ToString(),
            Name = j.Name,
            Description = j.Description,
            Subtitle = JoinParts(j.ScheduleSummary ?? j.Description, j.LastRunStatus),
            Glyph = "◷",
            IsEnabled = j.IsEnabled,
            Kind = $"Job · {TriggerLabel(j.TriggerType)}",
            BodyLabel = "Prompt",
            Badge = j.LastRunStatus,
            ChatId = j.ChatId == Guid.Empty ? null : j.ChatId,
            Facts = FactList(
                ("Runs", j.ScheduleSummary),
                ("Last run", j.LastRunStatus),
                ("Watches", j.SourceChatTitle))
        }),

        _ => []
    };

    private static bool IsLocalServer(RemoteMcpServer server) =>
        string.Equals(server.ServerType, "local", StringComparison.OrdinalIgnoreCase);

    private static string TriggerLabel(string? trigger) => trigger?.ToLowerInvariant() switch
    {
        "time" => "Scheduled",
        "script" => "Wake script",
        "chat_event" or "chatevent" or "chat-event" => "Follows a chat",
        _ => "Automation"
    };

    private static string? JoinParts(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    private static IReadOnlyList<LibraryFact> FactList(params (string Label, string? Value)[] facts)
    {
        var present = facts.Where(fact => !string.IsNullOrWhiteSpace(fact.Value)).ToArray();
        return present.Select((fact, index) => new LibraryFact(fact.Label, fact.Value!, index == 0)).ToArray();
    }

    private static string ResourceName(LibrarySection section) => section switch
    {
        LibrarySection.Projects => RemoteProtocol.Resources.Projects,
        LibrarySection.Skills => RemoteProtocol.Resources.Skills,
        LibrarySection.Lumis => RemoteProtocol.Resources.Lumis,
        LibrarySection.Memories => RemoteProtocol.Resources.Memories,
        LibrarySection.McpServers => RemoteProtocol.Resources.Mcps,
        LibrarySection.Jobs => RemoteProtocol.Resources.Jobs,
        _ => RemoteProtocol.Resources.Projects
    };

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        if (!CanBeginCreate)
            return;

        _editorGeneration++;
        CloseView();
        IsCreating = true;
        IsEditing = true;
        IsEditorLoading = false;
        HasEditorLoadFailed = false;
        SelectedEntry = null;
        EditName = "";
        EditDescription = "";
        EditBody = "";
        EditGlyph = Section == LibrarySection.Lumis ? "✦" : "⚡";
        EditWorkingDirectory = "";
        StatusMessage = null;
        RememberDraft();
    }

    [RelayCommand]
    private async Task BeginEditAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || !entry.CanEdit || IsEditorLoading || HasUnsavedChanges || IsActionBusy || IsRowActionsOpen
            || IsDiscardConfirmationOpen)
            return;

        Section = entry.Section;
        IsRowActionsOpen = false;
        await LoadEditorAsync(entry);
    }

    /// <summary>A tap on any row: editable items open in the editor, the rest on their own page.</summary>
    [RelayCommand]
    private async Task OpenEntryAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null)
            return;

        if (entry.CanEdit)
            await BeginEditAsync(entry);
        else
            await ViewEntryAsync(entry);
    }

    private async Task ViewEntryAsync(LibraryEntryViewModel entry)
    {
        if (IsEditorLoading || HasUnsavedChanges || IsActionBusy || IsRowActionsOpen || IsDiscardConfirmationOpen)
            return;

        if (IsEditing)
            EndEdit();

        var generation = ++_viewGeneration;
        ViewedEntry = entry;
        ViewBody = null;
        ViewStatus = null;
        IsViewLoading = true;
        try
        {
            if (_sink is not IRemoteLibraryDetailSink detailSink)
                return;

            var detail = await detailSink.GetLibraryItemAsync(ResourceName(entry.Section), entry.Identifier);
            if (generation != _viewGeneration || !ReferenceEquals(ViewedEntry, entry))
                return;

            if (detail is null)
            {
                ViewStatus = "Lumi could not load the full details. Try again when your PC is connected.";
                return;
            }

            ViewBody = detail.Body;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            if (generation == _viewGeneration)
                ViewStatus = $"Could not load the full details. {ex.Message}";
        }
        finally
        {
            if (generation == _viewGeneration)
                IsViewLoading = false;
        }
    }

    [RelayCommand]
    private void CloseView()
    {
        _viewGeneration++;
        ViewedEntry = null;
        ViewBody = null;
        ViewStatus = null;
        IsViewLoading = false;
    }

    /// <summary>Opens the chat a job runs in.</summary>
    [RelayCommand]
    private void OpenViewedChat()
    {
        if (ViewedEntry is { CanOpenChat: true, ChatId: { } chatId } entry)
            ChatRequested?.Invoke(chatId, entry.Name);
    }

    /// <summary>
    /// The switch on a connector's or job's row. It turns the item on or off on the PC without
    /// opening anything; a refused change puts the switch back.
    /// </summary>
    [RelayCommand]
    private async Task ToggleEntryAsync(LibraryEntryViewModel? entry)
    {
        if (entry is not { HasSwitch: true } || entry.IsSwitchBusy)
            return;

        var enabled = !entry.IsEnabled;
        entry.IsSwitchBusy = true;
        StatusMessage = null;
        try
        {
            var result = await _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
                    .With("resource", ResourceName(entry.Section))
                    .With("featureAction", "update")
                    .With("identifier", entry.Identifier)
                    .With("isEnabled", enabled.ToString()));
            if (!result.Ok)
            {
                StatusMessage = result.Error ?? result.Message ?? "Could not change this item. Please try again.";
                entry.RefreshEnabled();
                return;
            }

            entry.IsEnabled = enabled;
            const string done = "Done.";
            var message = await RefreshAfterSuccessAsync(done);
            if (message != done)
                StatusMessage = message;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            StatusMessage = $"Could not change this item. {ex.Message}";
            entry.RefreshEnabled();
        }
        finally
        {
            entry.IsSwitchBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryEditor))]
    private async Task RetryEditorAsync()
    {
        if (CanRetryEditor && SelectedEntry is { } entry)
            await LoadEditorAsync(entry);
    }

    private async Task LoadEditorAsync(LibraryEntryViewModel entry)
    {
        CloseView();
        var generation = ++_editorGeneration;
        IsCreating = false;
        IsEditing = true;
        IsEditorLoading = true;
        HasEditorLoadFailed = false;
        SelectedEntry = entry;
        EditName = entry.Name;
        EditDescription = "";
        EditBody = "";
        EditGlyph = "";
        EditWorkingDirectory = "";
        RememberDraft();
        StatusMessage = "Loading full details…";

        try
        {
            if (_sink is not IRemoteLibraryDetailSink detailSink)
            {
                HasEditorLoadFailed = true;
                StatusMessage = "This item cannot be edited from this connection.";
                return;
            }

            var detail = await detailSink.GetLibraryItemAsync(
                ResourceName(entry.Section),
                entry.Identifier);
            if (generation != _editorGeneration
                || SelectedEntry?.Identifier != entry.Identifier
                || SelectedEntry.Section != entry.Section)
            {
                return;
            }

            if (detail is null)
            {
                HasEditorLoadFailed = true;
                StatusMessage = "Lumi could not load the full item. Try again when your PC is connected.";
                return;
            }

            EditName = detail.Name;
            EditDescription = detail.Description ?? "";
            EditBody = detail.Body ?? "";
            EditGlyph = detail.Glyph ?? entry.Glyph;
            EditWorkingDirectory = detail.WorkingDirectory ?? "";
            RememberDraft();
            StatusMessage = null;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            if (generation == _editorGeneration)
            {
                HasEditorLoadFailed = true;
                StatusMessage = $"Could not load this item. {ex.Message}";
            }
        }
        finally
        {
            if (generation == _editorGeneration)
                IsEditorLoading = false;
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        if (HasUnsavedChanges)
        {
            _pendingSection = null;
            IsDiscardConfirmationOpen = true;
            return;
        }

        EndEdit();
    }

    [RelayCommand]
    private void KeepEditing() => IsDiscardConfirmationOpen = false;

    partial void OnIsDiscardConfirmationOpenChanged(bool value)
    {
        if (!value)
        {
            _pendingSection = null;
            _pendingScope = null;
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        if (!IsDiscardConfirmationOpen || IsSaving)
            return;

        var section = _pendingSection;
        var scope = _pendingScope;
        EndEdit();
        StatusMessage = null;
        if (scope is { } requestedScope)
            ApplyScope(requestedScope);
        else if (section is { } requestedSection)
            Section = requestedSection;
    }

    private void RememberDraft()
    {
        _originalDraft = CurrentDraft;
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void EndEdit()
    {
        _editorGeneration++;
        IsDiscardConfirmationOpen = false;
        _pendingSection = null;
        IsEditing = false;
        IsCreating = false;
        IsEditorLoading = false;
        HasEditorLoadFailed = false;
        SelectedEntry = null;
        StatusMessage = null;
        EditName = "";
        EditDescription = "";
        EditBody = "";
        EditGlyph = "";
        EditWorkingDirectory = "";
        RememberDraft();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave)
            return;

        if (EditName.Trim().Length == 0)
        {
            StatusMessage = "Give it a name first.";
            return;
        }

        var generation = _editorGeneration;
        var section = Section;
        var isCreating = IsCreating;
        var selectedIdentifier = SelectedEntry?.Identifier;
        var command = new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
            .With("resource", ResourceName(section))
            .With("featureAction", isCreating ? "create" : "update");

        if (!isCreating && selectedIdentifier is { Length: > 0 })
            command.With("identifier", selectedIdentifier);

        switch (section)
        {
            case LibrarySection.Projects:
                command.With("name", EditName)
                    .With("instructions", EditBody)
                    .With("workingDirectory", EditWorkingDirectory);
                break;
            case LibrarySection.Skills:
                command.With("name", EditName)
                    .With("description", EditDescription)
                    .With("content", EditBody)
                    .With("iconGlyph", EditGlyph);
                break;
            case LibrarySection.Lumis:
                command.With("name", EditName)
                    .With("description", EditDescription)
                    .With("systemPrompt", EditBody)
                    .With("iconGlyph", EditGlyph);
                break;
            case LibrarySection.Memories:
                command.With("key", EditName).With("content", EditBody);
                break;
        }

        StatusMessage = null;
        try
        {
            var result = await _sink.SendCommandAsync(command);
            if (!IsCurrentEditor())
                return;

            if (!result.Ok)
            {
                StatusMessage = result.Error ?? result.Message ?? "Could not save. Your changes are still here.";
                return;
            }

            var message = await RefreshAfterSuccessAsync(result.Message ?? "Saved on your PC.");
            if (!IsCurrentEditor())
                return;

            EndEdit();
            StatusMessage = message;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            if (IsCurrentEditor())
                StatusMessage = $"Could not save. Your changes are still here. {ex.Message}";
        }

        bool IsCurrentEditor() => generation == _editorGeneration
            && Section == section
            && IsCreating == isCreating
            && (isCreating || SelectedEntry?.Identifier == selectedIdentifier);
    }

    [RelayCommand]
    private Task DeleteAsync(LibraryEntryViewModel? entry)
    {
        if (entry is not { CanEdit: true } || IsEditing || IsActionBusy)
            return Task.CompletedTask;

        if (!ReferenceEquals(ActionEntry, entry))
            OpenRowActions(entry);

        IsConfirmingDelete = true;
        StatusMessage = null;
        return Task.CompletedTask;
    }

    private bool CanConfirmDelete => IsRowActionsOpen && IsConfirmingDelete && CanDeleteActionEntry && !IsActionBusy;

    [RelayCommand(CanExecute = nameof(CanConfirmDelete))]
    private async Task ConfirmDeleteActionEntryAsync()
    {
        if (!CanConfirmDelete || ActionEntry is not { } entry)
            return;

        var generation = _actionGeneration;
        StatusMessage = null;
        try
        {
            var result = await _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
                    .With("resource", ResourceName(entry.Section))
                    .With("featureAction", "delete")
                    .With("identifier", entry.Identifier));
            if (generation != _actionGeneration)
                return;

            if (!result.Ok)
            {
                StatusMessage = result.Error ?? result.Message ?? "Could not delete this item. Please try again.";
                return;
            }

            var message = await RefreshAfterSuccessAsync("Deleted from your shared Library.");
            if (generation != _actionGeneration)
                return;

            IsRowActionsOpen = false;
            StatusMessage = message;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            if (generation == _actionGeneration)
                StatusMessage = $"Could not delete this item. {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || entry.Section is not (LibrarySection.McpServers or LibrarySection.Jobs)
            || IsEditing || IsActionBusy || IsConfirmingDelete)
            return;

        if (!ReferenceEquals(ActionEntry, entry))
            OpenRowActions(entry);

        var generation = _actionGeneration;
        var enabled = !entry.IsEnabled;
        StatusMessage = null;
        try
        {
            var result = await _sink.SendCommandAsync(
                new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
                    .With("resource", ResourceName(entry.Section))
                    .With("featureAction", "update")
                    .With("identifier", entry.Identifier)
                    .With("isEnabled", enabled.ToString()));
            if (generation != _actionGeneration)
                return;

            if (!result.Ok)
            {
                StatusMessage = result.Error ?? result.Message ?? "Could not change this item. Please try again.";
                return;
            }

            entry.IsEnabled = enabled;
            var message = await RefreshAfterSuccessAsync(enabled ? "Enabled on your PC." : "Disabled on your PC.");
            if (generation != _actionGeneration)
                return;

            IsRowActionsOpen = false;
            StatusMessage = message;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            if (generation == _actionGeneration)
                StatusMessage = $"Could not change this item. {ex.Message}";
        }
    }

    private async Task<string> RefreshAfterSuccessAsync(string message)
    {
        try
        {
            if (_sink is IRemoteCatalogRefreshSink refreshSink)
                await refreshSink.RefreshCatalogsAsync();
            return message;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            return $"{message} The list could not refresh. Reconnect to your PC to reload it.";
        }
    }
}
