using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Context;
using StrataSearch;

namespace Lumi.ViewModels;

/// <summary>How the Memories page shows the briefs group.</summary>
public enum BriefsListMode
{
    /// <summary>A memory category is chosen: no briefs.</summary>
    Hidden,

    /// <summary>All: the brief across projects and the most recently updated ones, then Show all.</summary>
    Preview,

    /// <summary>The Briefs chip: every brief.</summary>
    All,
}

/// <summary>
/// The briefs on the Memories page: Lumi's own summary of where each project stands, which every new chat in the project
/// starts with. The user can read and correct a brief, clear it, or ask Lumi to review the chats now.
/// </summary>
public partial class BriefsViewModel : ObservableObject
{
    /// <summary>Project briefs the All view shows before Show all, after the one across projects.</summary>
    internal const int PreviewProjectCount = 3;

    private readonly DataStore _dataStore;
    private readonly ContextCuratorService? _curator;
    private readonly Action? _onCardsChanged;
    private string _editorBaseline = "";
    private bool _syncingEditor;
    private int _savedToastVersion;
    private string _query = "";
    private BriefsListMode _mode = BriefsListMode.Preview;
    private int _matchCount;
    private bool _canToggleShowAll;

    [ObservableProperty] private ContextBrief? _selectedBrief;
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private bool _isPreviewing = true;
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private bool _isConfirmingClear;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private bool _showSavedToast;
    [ObservableProperty] private bool _isShowingAll;

    /// <param name="onCardsChanged">Called after the user saved or cleared a brief or a review finished, so the page
    /// can update its chips and other pages that show briefs.</param>
    public BriefsViewModel(DataStore dataStore, ContextCuratorService? curator, Action? onCardsChanged = null)
    {
        _dataStore = dataStore;
        _curator = curator;
        _onCardsChanged = onCardsChanged;
        Refresh();
    }

    /// <summary>Every brief: the one across projects first, then the most recently updated.</summary>
    public ObservableCollection<BriefCard> Cards { get; } = [];

    /// <summary>The briefs the group shows for the page's chip and search.</summary>
    public ObservableCollection<BriefCard> VisibleCards { get; } = [];

    public bool IsEnabled => _dataStore.Data.Settings.EnableDailyBriefs;
    public bool HasBriefs => Cards.Count > 0;
    public bool ShowEmpty => IsEnabled && !HasBriefs && _query.Length == 0;
    public bool ShowGroup => IsEnabled && _mode != BriefsListMode.Hidden && (VisibleCards.Count > 0 || ShowEmpty);
    public bool CanToggleShowAll => _canToggleShowAll;
    public string ShowAllText => IsShowingAll
        ? Loc.Mg_ShowLess
        : string.Format(CultureInfo.CurrentCulture, Loc.Mg_ShowAllCount, _matchCount);
    public string CountText => _matchCount.ToString(CultureInfo.CurrentCulture);
    public bool IsOpen => SelectedBrief is not null;
    public bool CanUpdateNow => _curator is not null && IsEnabled && !IsUpdating;
    public bool IsGlobal => SelectedBrief is { ProjectId: null };
    public bool IsEditedByUser => SelectedBrief?.EditedByUser == true;
    public string DetailTitle => SelectedBrief is { } brief ? TitleOf(brief) : "";
    public string ScopeLabel => SelectedBrief is { } brief ? AudienceOf(brief) : "";
    public string UpdatedLabel => SelectedBrief is { } brief
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Updated, ManagementText.Relative(brief.UpdatedAt))
        : "";
    public string DetailCaption => SelectedBrief is { } brief
        ? brief.ProjectId is null
            ? Loc.Briefs_GlobalCaption
            : string.Format(CultureInfo.CurrentCulture, Loc.Briefs_ProjectCaption, TitleOf(brief))
        : "";
    public string PreviewMarkdown => IsPreviewing ? EditContent : "";
    public bool ShowSaveBar => IsOpen && HasUnsavedChanges && !IsConfirmingClear;
    public string ClearConfirmText => string.Format(CultureInfo.CurrentCulture, Loc.Briefs_ClearConfirm, DetailTitle);

    public string StatusText
    {
        get
        {
            if (IsUpdating)
                return Loc.Briefs_Updating;
            if (_curator?.LastRunFailed == true)
                return Loc.Briefs_UpdateFailed;
            return _curator?.LastRunAt is { } lastRun
                ? string.Format(CultureInfo.CurrentCulture, Loc.Briefs_LastReviewed, ManagementText.Relative(lastRun))
                : Loc.Briefs_NotReviewedYet;
        }
    }

    /// <summary>Rebuilds the cards from the store; an open brief keeps unsaved edits and otherwise shows the latest text.</summary>
    public void Refresh()
    {
        IsUpdating = _curator?.IsRunning == true;
        var projectIds = ProjectIds();
        var briefs = _dataStore.Data.Briefs.ToArray()
            .Where(brief => IsListable(brief, projectIds))
            .OrderBy(brief => brief.ProjectId is null ? 0 : 1)
            .ThenByDescending(brief => brief.UpdatedAt)
            .ToList();

        Cards.Clear();
        foreach (var brief in briefs)
            Cards.Add(new BriefCard(brief, TitleOf(brief), AudienceOf(brief), Open));
        UpdateVisibleCards();

        if (SelectedBrief is { } open)
        {
            // A brief that was cleared, or whose project was deleted, has no card and closes.
            if (!Cards.Any(card => ReferenceEquals(card.Brief, open)))
                SelectedBrief = null;
            else if (!HasUnsavedChanges)
                SyncEditor(open);
        }

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(HasBriefs));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowGroup));
        OnPropertyChanged(nameof(CanUpdateNow));
        OnPropertyChanged(nameof(StatusText));
        NotifyDetail();
    }

    /// <summary>Shows the briefs for the page's chosen chip and search.</summary>
    internal void ApplyView(string? query, BriefsListMode mode)
    {
        _query = query?.Trim() ?? "";
        _mode = mode;
        UpdateVisibleCards();
    }

    private void UpdateVisibleCards()
    {
        IReadOnlyList<BriefCard> matches = SearchPipeline.Rank(
            Cards,
            _query,
            static card => [SearchField.Primary(card.Title, 3.3), SearchField.Content(card.Brief.Content, 1.1)],
            static card => new SearchSortMetadata(Text: card.Title));
        if (_query.Length > 0)
        {
            // Search forgives a couple of typos per word, and a brief is long enough that nearly every one has a word
            // that close ("SQLite" ~ "isolate"): keep the briefs that say every word, in the engine's order.
            var words = _query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            matches = matches
                .Where(card => words.All(word => card.Title.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                                                 || card.Brief.Content.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
                .ToList();
        }

        var preview = _mode == BriefsListMode.Preview && _query.Length == 0 ? PreviewOf(matches) : null;
        IReadOnlyList<BriefCard> shown = _mode == BriefsListMode.Hidden ? []
            : preview is not null && !IsShowingAll ? preview
            : matches;
        _matchCount = matches.Count;
        _canToggleShowAll = preview is not null && preview.Count < matches.Count;

        VisibleCards.Clear();
        foreach (var card in shown)
            VisibleCards.Add(card);

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowGroup));
        OnPropertyChanged(nameof(CanToggleShowAll));
        OnPropertyChanged(nameof(ShowAllText));
        OnPropertyChanged(nameof(CountText));
    }

    private static List<BriefCard> PreviewOf(IReadOnlyList<BriefCard> cards)
        => [.. cards.Where(card => card.IsGlobal), .. cards.Where(card => !card.IsGlobal).Take(PreviewProjectCount)];

    /// <summary>Opens a scope's brief from elsewhere, such as its project's page.</summary>
    public void OpenFor(Guid? projectId)
    {
        if (FindOpenable(projectId) is { } brief && !ReferenceEquals(SelectedBrief, brief))
            SelectedBrief = brief;
    }

    /// <summary>A scope's brief when the page can show it: the feature is on and the brief is listable.</summary>
    private ContextBrief? FindOpenable(Guid? projectId)
    {
        if (!IsEnabled)
            return null;
        var projectIds = ProjectIds();
        return _dataStore.Data.Briefs.ToArray()
            .FirstOrDefault(brief => brief.ProjectId == projectId && IsListable(brief, projectIds));
    }

    /// <summary>A brief the page lists and opens: it has text, and its project, if it has one, still exists.</summary>
    private static bool IsListable(ContextBrief brief, IReadOnlySet<Guid> projectIds)
        => !string.IsNullOrWhiteSpace(brief.Content) && (brief.ProjectId is not { } id || projectIds.Contains(id));

    private HashSet<Guid> ProjectIds() => _dataStore.Data.Projects.ToArray().Select(project => project.Id).ToHashSet();

    private const string GlobalNavigationKey = "global";

    /// <summary>The open brief for navigation history: its project's id, "global", or null when none is open.</summary>
    internal string? NavigationKey => SelectedBrief is { } brief
        ? brief.ProjectId?.ToString("N") ?? GlobalNavigationKey
        : null;

    /// <summary>Whether a history entry's brief can still be opened; Back skips one that can't.</summary>
    internal bool CanRestoreNavigation(string key) => FindByNavigationKey(key) is not null;

    /// <summary>Opens or closes briefs to match a history entry.</summary>
    internal void RestoreNavigation(string? key)
    {
        if (key is not null && FindByNavigationKey(key) is { } brief)
        {
            if (!ReferenceEquals(SelectedBrief, brief))
                SelectedBrief = brief;
        }
        else if (IsOpen)
        {
            Close();
        }
    }

    private ContextBrief? FindByNavigationKey(string key)
        => key == GlobalNavigationKey ? FindOpenable(null)
            : Guid.TryParse(key, out var projectId) ? FindOpenable(projectId)
            : null;

    private string TitleOf(ContextBrief brief)
        => brief.ProjectId is { } id && _dataStore.Data.Projects.ToArray().FirstOrDefault(project => project.Id == id) is { } project
            ? project.Name
            : Loc.Briefs_GlobalTitle;

    private string AudienceOf(ContextBrief brief)
        => brief.ProjectId is null
            ? Loc.Briefs_GlobalAudience
            : string.Format(CultureInfo.CurrentCulture, Loc.Briefs_ProjectAudience, TitleOf(brief));

    private void Open(BriefCard card) => SelectedBrief = card.Brief;

    partial void OnSelectedBriefChanged(ContextBrief? value)
    {
        IsConfirmingClear = false;
        if (value is not null)
        {
            IsPreviewing = true;
            SyncEditor(value);
        }

        OnPropertyChanged(nameof(IsOpen));
        NotifyDetail();
    }

    private void SyncEditor(ContextBrief brief)
    {
        _syncingEditor = true;
        try
        {
            EditContent = brief.Content;
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = EditContent;
        UpdateDirtyState();
    }

    private void UpdateDirtyState()
    {
        if (_syncingEditor)
            return;
        HasUnsavedChanges = IsOpen && !string.Equals(EditContent, _editorBaseline, StringComparison.Ordinal);
    }

    private void NotifyDetail()
    {
        OnPropertyChanged(nameof(IsGlobal));
        OnPropertyChanged(nameof(IsEditedByUser));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(DetailCaption));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(ClearConfirmText));
        OnPropertyChanged(nameof(PreviewMarkdown));
    }

    partial void OnEditContentChanged(string value)
    {
        UpdateDirtyState();
        OnPropertyChanged(nameof(PreviewMarkdown));
    }

    partial void OnIsPreviewingChanged(bool value) => OnPropertyChanged(nameof(PreviewMarkdown));
    partial void OnIsShowingAllChanged(bool value) => UpdateVisibleCards();
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));
    partial void OnIsConfirmingClearChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));

    partial void OnIsUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUpdateNow));
        OnPropertyChanged(nameof(StatusText));
    }

    [RelayCommand]
    private void Close()
    {
        IsConfirmingClear = false;
        SelectedBrief = null;
    }

    [RelayCommand]
    private void ShowPreview() => IsPreviewing = true;

    [RelayCommand]
    private void ShowEditor() => IsPreviewing = false;

    [RelayCommand]
    private void ToggleShowAll() => IsShowingAll = !IsShowingAll;

    /// <summary>Keeps the user's text; the curator starts from it and keeps the edits unless newer chats contradict them.</summary>
    [RelayCommand]
    private void Save()
    {
        if (SelectedBrief is not { } brief)
            return;

        brief.Content = EditContent.Trim();
        brief.UpdatedAt = DateTimeOffset.Now;
        brief.EditedByUser = true;
        _ = _dataStore.SaveAsync();
        SyncEditor(brief);
        Refresh();
        _onCardsChanged?.Invoke();
        FlashSaved();
    }

    [RelayCommand]
    private void Discard()
    {
        if (SelectedBrief is { } brief)
            SyncEditor(brief);
    }

    [RelayCommand]
    private void RequestClear()
    {
        if (SelectedBrief is not null)
            IsConfirmingClear = true;
    }

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    /// <summary>Empties the brief; the next review writes a new one from the chats since then.</summary>
    [RelayCommand]
    private void ConfirmClear()
    {
        if (SelectedBrief is not { } brief)
            return;

        brief.Content = "";
        brief.UpdatedAt = DateTimeOffset.Now;
        brief.EditedByUser = false;
        _ = _dataStore.SaveAsync();
        IsConfirmingClear = false;
        SelectedBrief = null;
        Refresh();
        _onCardsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task UpdateNow()
    {
        if (_curator is null || IsUpdating)
            return;

        IsUpdating = true;
        try
        {
            await _curator.RunNowAsync();
        }
        finally
        {
            Refresh();
            _onCardsChanged?.Invoke();
        }
    }

    private async void FlashSaved()
    {
        var version = ++_savedToastVersion;
        ShowSavedToast = true;
        await Task.Delay(1800);
        if (version == _savedToastVersion)
            ShowSavedToast = false;
    }
}

/// <summary>A brief in the Memories overview: its scope and the first lines of where things stand now.</summary>
public sealed partial class BriefCard
{
    public BriefCard(ContextBrief brief, string title, string audience, Action<BriefCard> open)
    {
        Brief = brief;
        Title = title;
        Audience = audience;
        Summary = Summarize(brief.Content);
        OpenCommand = new RelayCommand(() => open(this));
    }

    public ContextBrief Brief { get; }
    public string Title { get; }
    public string Audience { get; }
    public string Summary { get; }
    public bool IsGlobal => Brief.ProjectId is null;
    public string UpdatedLabel => ManagementText.Relative(Brief.UpdatedAt);
    public IRelayCommand OpenCommand { get; }

    [GeneratedRegex(@"\s*\([^()]*\)\s*\.?\s*$")]
    private static partial Regex TrailingSource();

    /// <summary>The first lines of the brief's first section ("Now" in a project brief), without bullets or sources.</summary>
    internal static string Summarize(string content)
    {
        var lines = content.Split('\n').Select(line => line.Trim()).ToList();
        var start = lines.FindIndex(line => line.StartsWith("#", StringComparison.Ordinal));
        var section = lines
            .Skip(start + 1)
            .TakeWhile(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Where(line => line.Length > 0)
            .Select(line => TrailingSource().Replace(line.TrimStart('-', '*', ' '), ""))
            .Where(line => line.Length > 0)
            .Take(3);
        return string.Join("\n", section);
    }
}
