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
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private string? _badge;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(HasActions))]
    private bool _isBuiltIn;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledStateText))]
    private bool _isEnabled = true;

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
        IsEnabled = source.IsEnabled;
    }
}

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSurface), nameof(PageTitle), nameof(ShowCreateButton),
        nameof(CanEditFields), nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(BeginCreateCommand))]
    private bool _isEditing;
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
    }

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
                return;
            }

            EndEdit();
            IsRowActionsOpen = false;
            SetProperty(ref _section, value);
            OnSectionChanged();
        }
    }

    public string EditorTitle => $"{(IsCreating ? "New" : "Edit")} {SingularName(Section)}";
    public string PageTitle => IsEditing ? EditorTitle : "Library";
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

    /// <summary>Raised when the user dismisses the library page and returns to the conversation.</summary>
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
    public bool HasOpenSurface => IsDiscardConfirmationOpen || IsRowActionsOpen || IsEditing;

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

        return false;
    }

    private ObservableCollection<LibraryEntryViewModel> _entries = [];
    public ObservableCollection<LibraryEntryViewModel> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }

    public ObservableCollection<string> SectionNames { get; } =
        ["Projects", "Skills", "Lumis", "Memories", "MCP", "Jobs"];

    public int SectionIndex
    {
        get => (int)Section;
        set
        {
            if (value >= 0 && value < SectionNames.Count && (int)Section != value)
                Section = (LibrarySection)value;
        }
    }

    public bool IsEmpty => Entries.Count == 0;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);
    public bool IsNoResults => IsEmpty && HasSearchQuery;
    public string EmptyTitle => IsNoResults ? $"No {PluralName} found" : $"No {PluralName} yet";
    public string EmptyDescription => IsNoResults
        ? $"Try another name or clear the search to see all {PluralName}."
        : Section switch
        {
            LibrarySection.McpServers => "Add MCP servers on your PC. You can enable or disable them here.",
            LibrarySection.Jobs => "Create jobs on your PC. You can enable or disable them here.",
            _ => $"Use New to add a {SingularName(Section)}, or add one on your PC. Your Library is shared."
        };

    public string SectionDescription => Section switch
    {
        LibrarySection.Projects => "Shared projects, instructions and PC workspaces for your chats.",
        LibrarySection.Skills => "Reusable instructions that help Lumi handle familiar tasks.",
        LibrarySection.Lumis => "Your assistants, each with their own role and instructions.",
        LibrarySection.Memories => "Facts Lumi remembers and uses across your conversations.",
        LibrarySection.McpServers => "Tool connections on your PC. Manage their availability here; configure them on your PC.",
        LibrarySection.Jobs => "Automations that run on your PC. Manage their availability here; edit their schedules on your PC.",
        _ => ""
    };

    private string PluralName => Section switch
    {
        LibrarySection.McpServers => "MCP servers",
        LibrarySection.Lumis => "Lumis",
        _ => SectionNames[SectionIndex].ToLowerInvariant()
    };

    private static string SingularName(LibrarySection section) => section switch
    {
        LibrarySection.Projects => "project",
        LibrarySection.Skills => "skill",
        LibrarySection.Lumis => "Lumi",
        LibrarySection.Memories => "memory",
        LibrarySection.McpServers => "MCP server",
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
        Rebuild();
    }

    internal void ResetHostState()
    {
        EndEdit();
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
        OnPropertyChanged(nameof(SectionIndex));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(ShowCreateButton));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(SectionDescription));
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

        // Publish a changed page once. Replaying hundreds of Add notifications builds off-screen
        // templates on the UI thread; an unchanged snapshot should not disturb the current rows.
        if (!Entries.SequenceEqual(projected))
            Entries = new ObservableCollection<LibraryEntryViewModel>(projected);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(IsNoResults));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        return;

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
            Glyph = "◆",
            Badge = p.ChatCount > 0 ? $"{p.ChatCount} {(p.ChatCount == 1 ? "chat" : "chats")}" : null
        }),

        LibrarySection.Skills => _library.Skills.Select(s => new LibraryEntryViewModel
        {
            Section = LibrarySection.Skills,
            Identifier = s.Id.ToString(),
            Name = s.Name,
            Description = s.Description,
            Glyph = s.IconGlyph,
            IsBuiltIn = s.IsBuiltIn,
            Badge = s.IsBuiltIn ? "Built-in" : null
        }),

        LibrarySection.Lumis => _library.Lumis.Select(l => new LibraryEntryViewModel
        {
            Section = LibrarySection.Lumis,
            Identifier = l.Id.ToString(),
            Name = l.Name,
            Description = l.Description,
            Glyph = l.IconGlyph,
            IsBuiltIn = l.IsBuiltIn,
            Badge = l.IsBuiltIn ? "Built-in" : l.SkillCount > 0 ? $"{l.SkillCount} skills" : null
        }),

        LibrarySection.Memories => _library.Memories.Select(m => new LibraryEntryViewModel
        {
            Section = LibrarySection.Memories,
            Identifier = m.Id.ToString(),
            Name = m.Key,
            Description = m.Content,
            Glyph = "◇",
            Badge = m.Category
        }),

        LibrarySection.McpServers => _library.McpServers.Select(s => new LibraryEntryViewModel
        {
            Section = LibrarySection.McpServers,
            Identifier = s.Id.ToString(),
            Name = s.Name,
            Description = s.Description ?? s.Command ?? s.Url,
            Glyph = "⬡",
            IsEnabled = s.IsEnabled,
            Badge = s.ToolCount > 0 ? $"{s.ToolCount} tools" : null
        }),

        LibrarySection.Jobs => _library.Jobs.Select(j => new LibraryEntryViewModel
        {
            Section = LibrarySection.Jobs,
            Identifier = j.Id.ToString(),
            Name = j.Name,
            Description = j.Description ?? j.ScheduleSummary,
            Glyph = "◷",
            IsEnabled = j.IsEnabled,
            Badge = j.LastRunStatus
        }),

        _ => []
    };

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

    [RelayCommand(CanExecute = nameof(CanRetryEditor))]
    private async Task RetryEditorAsync()
    {
        if (CanRetryEditor && SelectedEntry is { } entry)
            await LoadEditorAsync(entry);
    }

    private async Task LoadEditorAsync(LibraryEntryViewModel entry)
    {
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
            _pendingSection = null;
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        if (!IsDiscardConfirmationOpen || IsSaving)
            return;

        var section = _pendingSection;
        EndEdit();
        StatusMessage = null;
        if (section is { } requestedSection)
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
