using System.Collections.ObjectModel;
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
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private string _glyph = "•";
    [ObservableProperty] private string? _badge;
    [ObservableProperty] private bool _isBuiltIn;
    [ObservableProperty] private bool _isEnabled = true;

    public required LibrarySection Section { get; init; }

    public required string Identifier { get; init; }

    public bool CanEdit =>
        !IsBuiltIn
        && Section is LibrarySection.Projects
            or LibrarySection.Skills
            or LibrarySection.Lumis
            or LibrarySection.Memories;

    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
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

    [ObservableProperty] private LibrarySection _section = LibrarySection.Projects;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private LibraryEntryViewModel? _selectedEntry;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSurface))]
    private bool _isEditing;
    [ObservableProperty] private string? _statusMessage;

    // Editor fields — reused across resources, only the relevant ones are shown.
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editBody = "";
    [ObservableProperty] private string _editGlyph = "";
    [ObservableProperty] private string _editWorkingDirectory = "";
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private bool _isEditorLoading;

    public LibraryViewModel(IRemoteCommandSink sink) => _sink = sink;

    /// <summary>Raised when the user dismisses the library page and returns to the conversation.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private void Close()
    {
        // Cancel a half-finished edit rather than stranding it behind the chat.
        if (IsEditing)
            CancelEdit();
        else
            CloseRequested?.Invoke();
    }

    // ── Row action sheet (long-press) ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSurface))]
    private bool _isRowActionsOpen;

    [ObservableProperty] private LibraryEntryViewModel? _actionEntry;

    public string ActionEntryName => ActionEntry?.Name ?? "";

    /// <summary>Only MCP servers and jobs have an enabled state worth toggling.</summary>
    public bool CanToggleActionEntry =>
        ActionEntry?.Section is LibrarySection.McpServers or LibrarySection.Jobs;

    partial void OnActionEntryChanged(LibraryEntryViewModel? value)
    {
        OnPropertyChanged(nameof(ActionEntryName));
        OnPropertyChanged(nameof(CanToggleActionEntry));
    }

    [RelayCommand]
    private void OpenRowActions(LibraryEntryViewModel? entry)
    {
        if (entry is null)
            return;

        ActionEntry = entry;
        IsRowActionsOpen = true;
    }

    [RelayCommand]
    private async Task ToggleActionEntryAsync()
    {
        if (ActionEntry is { } entry)
            await ToggleEnabledCommand.ExecuteAsync(entry);

        IsRowActionsOpen = false;
    }

    [RelayCommand]
    private async Task DeleteActionEntryAsync()
    {
        if (ActionEntry is { } entry)
            await DeleteCommand.ExecuteAsync(entry);

        IsRowActionsOpen = false;
    }

    /// <summary>Whether Back should dismiss library-local UI before leaving the page.</summary>
    public bool HasOpenSurface => IsRowActionsOpen || IsEditing;

    internal bool DismissTopmostSurface()
    {
        if (IsRowActionsOpen)
        {
            IsRowActionsOpen = false;
            return true;
        }

        if (IsEditing)
        {
            CancelEdit();
            return true;
        }

        return false;
    }

    public ObservableCollection<LibraryEntryViewModel> Entries { get; } = [];

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

    public bool ShowDescriptionEditor => Section is LibrarySection.Skills or LibrarySection.Lumis
        or LibrarySection.McpServers or LibrarySection.Jobs;

    public bool ShowProjectWorkingDirectory => Section == LibrarySection.Projects;

    public void Apply(RemoteLibrary library)
    {
        _library = library;
        Rebuild();
    }

    internal void ResetHostState()
    {
        _editorGeneration++;
        CancelEdit();
        IsRowActionsOpen = false;
        ActionEntry = null;
        SelectedEntry = null;
        SearchText = "";
        StatusMessage = null;
        EditName = "";
        EditDescription = "";
        EditBody = "";
        EditGlyph = "";
        EditWorkingDirectory = "";
        Apply(new RemoteLibrary());
    }

    partial void OnSectionChanged(LibrarySection value)
    {
        _editorGeneration++;
        IsEditing = false;
        IsEditorLoading = false;
        SelectedEntry = null;
        OnPropertyChanged(nameof(SectionIndex));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(BodyLabel));
        OnPropertyChanged(nameof(ShowGlyphEditor));
        OnPropertyChanged(nameof(ShowDescriptionEditor));
        OnPropertyChanged(nameof(ShowProjectWorkingDirectory));
        Rebuild();
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var query = SearchText.Trim();
        var projected = Project().Where(Matches).ToList();

        Entries.Clear();
        foreach (var entry in projected)
            Entries.Add(entry);

        OnPropertyChanged(nameof(IsEmpty));
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
            Detail = p.WorkingDirectory,
            Glyph = "◆",
            Badge = p.ChatCount > 0 ? $"{p.ChatCount}" : null
        }),

        LibrarySection.Skills => _library.Skills.Select(s => new LibraryEntryViewModel
        {
            Section = LibrarySection.Skills,
            Identifier = s.Id.ToString(),
            Name = s.Name,
            Description = s.Description,
            Detail = s.Content,
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
            Detail = l.SystemPrompt,
            Glyph = l.IconGlyph,
            IsBuiltIn = l.IsBuiltIn,
            Badge = l.SkillCount > 0 ? $"{l.SkillCount} skills" : null
        }),

        LibrarySection.Memories => _library.Memories.Select(m => new LibraryEntryViewModel
        {
            Section = LibrarySection.Memories,
            Identifier = m.Id.ToString(),
            Name = m.Key,
            Description = m.Content,
            Detail = m.Content,
            Glyph = "◇",
            Badge = m.Category
        }),

        LibrarySection.McpServers => _library.McpServers.Select(s => new LibraryEntryViewModel
        {
            Section = LibrarySection.McpServers,
            Identifier = s.Id.ToString(),
            Name = s.Name,
            Description = s.Description ?? s.Command ?? s.Url,
            Detail = s.Command ?? s.Url,
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
            Detail = j.ScheduleSummary,
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

    [RelayCommand]
    private void BeginCreate()
    {
        _editorGeneration++;
        IsCreating = true;
        IsEditing = true;
        IsEditorLoading = false;
        SelectedEntry = null;
        EditName = "";
        EditDescription = "";
        EditBody = "";
        EditGlyph = Section == LibrarySection.Lumis ? "✦" : "⚡";
        EditWorkingDirectory = "";
        StatusMessage = null;
    }

    [RelayCommand]
    private async Task BeginEditAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || !entry.CanEdit)
            return;

        var generation = ++_editorGeneration;
        IsCreating = false;
        IsEditing = true;
        IsEditorLoading = true;
        SelectedEntry = entry;
        EditName = "";
        EditDescription = "";
        EditBody = "";
        EditGlyph = "";
        EditWorkingDirectory = "";
        StatusMessage = "Loading full details…";

        if (_sink is not IRemoteLibraryDetailSink detailSink)
        {
            if (generation == _editorGeneration)
            {
                IsEditorLoading = false;
                IsEditing = false;
                StatusMessage = "This item cannot be edited from this connection.";
            }
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

        IsEditorLoading = false;
        if (detail is null)
        {
            IsEditing = false;
            StatusMessage = "Lumi could not load the full item.";
            return;
        }

        EditName = detail.Name;
        EditDescription = detail.Description ?? "";
        EditBody = detail.Body ?? "";
        EditGlyph = detail.Glyph ?? entry.Glyph;
        EditWorkingDirectory = detail.WorkingDirectory ?? "";
        StatusMessage = null;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        _editorGeneration++;
        IsEditing = false;
        IsCreating = false;
        IsEditorLoading = false;
        SelectedEntry = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsEditorLoading)
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
            default:
                command.With("name", EditName).With("description", EditDescription);
                break;
        }

        var result = await _sink.SendCommandAsync(command);
        if (generation != _editorGeneration
            || Section != section
            || IsCreating != isCreating
            || (!isCreating && SelectedEntry?.Identifier != selectedIdentifier))
        {
            return;
        }

        StatusMessage = result.Ok ? result.Message : result.Error;

        if (result.Ok)
        {
            if (_sink is IRemoteCatalogRefreshSink refreshSink)
                await refreshSink.RefreshCatalogsAsync();
            _editorGeneration++;
            IsEditing = false;
            IsCreating = false;
            SelectedEntry = null;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || !entry.CanEdit)
            return;

        var result = await _sink.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
                .With("resource", ResourceName(entry.Section))
                .With("featureAction", "delete")
                .With("identifier", entry.Identifier));

        StatusMessage = result.Ok ? result.Message : result.Error;

        if (result.Ok)
        {
            if (_sink is IRemoteCatalogRefreshSink refreshSink)
                await refreshSink.RefreshCatalogsAsync();
            if (ReferenceEquals(entry, SelectedEntry))
            {
                IsEditing = false;
                SelectedEntry = null;
            }
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(LibraryEntryViewModel? entry)
    {
        if (entry is null || entry.Section is not (LibrarySection.McpServers or LibrarySection.Jobs))
            return;

        var result = await _sink.SendCommandAsync(
            new RemoteCommand(RemoteProtocol.Actions.ConfigureFeature)
                .With("resource", ResourceName(entry.Section))
                .With("featureAction", "update")
                .With("identifier", entry.Identifier)
                .With("isEnabled", (!entry.IsEnabled).ToString()));

        StatusMessage = result.Ok ? result.Message : result.Error;
        if (result.Ok && _sink is IRemoteCatalogRefreshSink refreshSink)
            await refreshSink.RefreshCatalogsAsync();
    }
}
