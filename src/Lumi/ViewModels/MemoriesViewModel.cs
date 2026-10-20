using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataSearch;

namespace Lumi.ViewModels;

public partial class MemoriesViewModel : ObservableObject
{
    internal const string DefaultCategory = "General";

    private readonly DataStore _dataStore;
    private string? _editorBaseline;
    private bool _restoringSelection;
    private bool _syncingEditor;
    private int _savedToastVersion;

    [ObservableProperty] private Memory? _selectedMemory;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editKey = "";
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private string _editCategory = DefaultCategory;
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>The open memory differs from what is stored.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _showSavedToast;

    /// <summary>Category the list is narrowed to; null shows every category.</summary>
    [ObservableProperty] private string? _categoryFilter;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<Memory> Memories { get; } = [];

    /// <summary>The overview: the filtered memories, grouped by category.</summary>
    public ObservableCollection<MemoryGroup> MemoryGroups { get; } = [];

    /// <summary>"All" plus every category that has memories, with counts.</summary>
    public ObservableCollection<ManagementFilterOption> CategoryFilters { get; } = [];

    /// <summary>Existing categories offered as one-click choices in the editor; the current one is selected.</summary>
    public ObservableCollection<ManagementFilterOption> CategorySuggestions { get; } = [];

    /// <summary>Raised when the project a memory belongs to is clicked; the shell opens the project.</summary>
    public event Action<Guid>? OpenProjectRequested;

     public MemoriesViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
     }

    public bool IsNewMemory => IsEditing && SelectedMemory is null;
    public bool ShowSaveBar => IsEditing && (HasUnsavedChanges || IsNewMemory) && !IsConfirmingDelete;
    public bool CanSave => !string.IsNullOrWhiteSpace(EditKey);
    public string SaveButtonText => IsNewMemory ? Loc.Memories_Create : Loc.Mg_SaveChanges;
    public string SaveBarText => IsNewMemory ? Loc.Mg_NotSavedYet : Loc.Mg_Unsaved;
    public string DiscardButtonText => IsNewMemory ? Loc.Common_Cancel : Loc.Mg_Discard;
    public string DetailTitle => string.IsNullOrWhiteSpace(EditKey) ? Loc.Memories_NewTitle : EditKey;
    public string DeleteConfirmText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_DeleteConfirm, DetailTitle);
    public bool HasAnyMemories => ActiveMemories().Any();
    public bool HasResults => MemoryGroups.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool ShowNoResults => HasAnyMemories && !HasResults;
    public bool ShowEmptyState => !HasAnyMemories;
    public string NoResultsText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_NoResults, SearchQuery.Trim());
    public string TotalCountText => ActiveMemories().Count().ToString(CultureInfo.CurrentCulture);
    public string CategoryCountText => ActiveMemories().Select(memory => CategoryOf(memory)).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.CurrentCulture);
    public string ProjectMemoryCountText => ActiveMemories().Count(IsProjectScoped).ToString(CultureInfo.CurrentCulture);
    public bool IsProjectMemory => SelectedMemory is { } memory && IsProjectScoped(memory);
    public string ScopeLabel => SelectedMemory is { } memory ? DescribeScope(memory) : Loc.Memories_Everywhere;
    public string SourceLabel => SelectedMemory is { } memory && !string.Equals(memory.Source, "manual", StringComparison.OrdinalIgnoreCase)
        ? Loc.Memories_SourceChat
        : Loc.Memories_SourceManual;
    public string UpdatedLabel => SelectedMemory is { } memory
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Updated, ManagementText.Relative(memory.UpdatedAt))
        : "";
    public string CreatedLabel => SelectedMemory is { } memory
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Created, ManagementText.Date(memory.CreatedAt))
        : "";
    public bool HasLastUsed => SelectedMemory?.LastUsedAt is not null;
    public string LastUsedLabel => SelectedMemory?.LastUsedAt is { } lastUsed
        ? string.Format(CultureInfo.CurrentCulture, Loc.Memories_LastUsed, ManagementText.Relative(lastUsed))
        : "";

     public void RefreshFromStore()
     {
         // Rebuilding the list clears the sidebar's selection. Put the open memory back quietly so the
         // page stays open or closed as it was, and keeps edits in progress.
         var open = SelectedMemory;
         var hasUnsavedEdits = IsEditing && HasUnsavedChanges;
         RefreshList();

         if (open is null)
             return;

         var selectedMemory = _dataStore.Data.Memories.FirstOrDefault(memory => memory.Id == open.Id);
         if (selectedMemory is null
             || !string.Equals(selectedMemory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase))
         {
             SelectedMemory = null;
             IsEditing = false;
             return;
         }

         RestoreSelection(selectedMemory);
         if (!hasUnsavedEdits)
             SyncEditorFromMemory(selectedMemory);
         NotifyDetailFacts();
     }

    private void RestoreSelection(Memory memory)
    {
        _restoringSelection = true;
        try
        {
            SelectedMemory = memory;
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private IEnumerable<Memory> ActiveMemories()
        => _dataStore.Data.Memories
            .Where(m => string.Equals(m.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase));

    private static string CategoryOf(Memory memory)
        => string.IsNullOrWhiteSpace(memory.Category) ? DefaultCategory : memory.Category.Trim();

    private static bool IsProjectScoped(Memory memory)
        => string.Equals(memory.Scope, MemoryScopes.Project, StringComparison.OrdinalIgnoreCase);

    private string DescribeScope(Memory memory)
    {
        if (!IsProjectScoped(memory) || memory.ProjectId is not { } projectId)
            return Loc.Memories_Everywhere;

        var project = _dataStore.Data.Projects.FirstOrDefault(p => p.Id == projectId);
        return project is null
            ? Loc.Memories_Everywhere
            : string.Format(CultureInfo.CurrentCulture, Loc.Memories_OnlyIn, project.Name);
    }

    private void RefreshList()
    {
        var activeMemories = ActiveMemories().ToList();
        var categories = activeMemories
            .GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (Name: group.Key, Count: group.Count()))
            .ToList();

        // A filter on a category that no longer exists falls back to "All".
        if (CategoryFilter is not null
            && !categories.Any(category => string.Equals(category.Name, CategoryFilter, StringComparison.OrdinalIgnoreCase)))
        {
            CategoryFilter = null;
            return;
        }

        Memories.Clear();
        var inScope = CategoryFilter is null
            ? activeMemories
            : activeMemories.Where(memory => string.Equals(CategoryOf(memory), CategoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        var items = hasQuery
            ? SearchPipeline.Rank(
                inScope,
                SearchQuery,
                static memory =>
                [
                    SearchField.Primary(memory.Key, 3.3),
                    new SearchField(memory.Category, 1.5),
                    SearchField.Content(memory.Content, 1.1)
                ],
                static memory => new SearchSortMetadata(Text: $"{memory.Category} {memory.Key}"))
            : inScope.OrderBy(memory => CategoryOf(memory), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(memory => memory.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

        foreach (var memory in items)
            Memories.Add(memory);

        // The overview groups by category in the list's own order, so search ranking is preserved
        // within each group and categories appear in the order their best match does.
        MemoryGroups.Clear();
        foreach (var group in Memories.GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase))
        {
            var cards = group.Select(memory => new MemoryCard(memory, DescribeScope(memory), EditMemory)).ToList();
            MemoryGroups.Add(new MemoryGroup(group.Key, cards));
        }

        CategoryFilters.Clear();
        var all = new ManagementFilterOption("", Loc.Memories_AllCategories, activeMemories.Count, SelectCategory)
        {
            IsSelected = CategoryFilter is null
        };
        CategoryFilters.Add(all);
        foreach (var (name, count) in categories)
        {
            CategoryFilters.Add(new ManagementFilterOption(name, name, count, SelectCategory)
            {
                IsSelected = string.Equals(name, CategoryFilter, StringComparison.OrdinalIgnoreCase)
            });
        }

        CategorySuggestions.Clear();
        if (!categories.Any(category => string.Equals(category.Name, DefaultCategory, StringComparison.OrdinalIgnoreCase)))
            CategorySuggestions.Add(new ManagementFilterOption(DefaultCategory, DefaultCategory, 0, ChooseCategory));
        foreach (var (name, count) in categories)
            CategorySuggestions.Add(new ManagementFilterOption(name, name, count, ChooseCategory));
        MarkChosenCategory();

        // The sidebar follows the open memory one way; re-announcing it re-selects the row after a rebuild.
        OnPropertyChanged(nameof(SelectedMemory));
        OnPropertyChanged(nameof(HasAnyMemories));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(CategoryCountText));
        OnPropertyChanged(nameof(ProjectMemoryCountText));
    }

    private void SelectCategory(ManagementFilterOption option)
        => CategoryFilter = string.IsNullOrEmpty(option.Key) ? null : option.Key;

    partial void OnCategoryFilterChanged(string? value) => RefreshList();

    [RelayCommand]
    private void NewMemory()
    {
        SelectedMemory = null;
        _syncingEditor = true;
        try
        {
            EditKey = "";
            EditContent = "";
            EditCategory = CategoryFilter ?? DefaultCategory;
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = CaptureEditor();
        IsConfirmingDelete = false;
        IsEditing = true;
        UpdateDirtyState();
        NotifyDetailFacts();
    }

    [RelayCommand]
    private void EditMemory(Memory memory)
    {
        SelectedMemory = memory;
    }

    /// <summary>Back to the overview.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        IsConfirmingDelete = false;
        SelectedMemory = null;
        IsEditing = false;
    }

     partial void OnSelectedMemoryChanged(Memory? value)
     {
         if (value is null || _restoringSelection) return;
         SyncEditorFromMemory(value);
         IsConfirmingDelete = false;
         IsEditing = true;
         NotifyDetailFacts();
     }

     private void SyncEditorFromMemory(Memory memory)
     {
         _syncingEditor = true;
         try
         {
             EditKey = memory.Key;
             EditContent = memory.Content;
             EditCategory = memory.Category;
         }
         finally
         {
             _syncingEditor = false;
         }

         _editorBaseline = CaptureEditor();
         UpdateDirtyState();
     }

    private string CaptureEditor() => string.Join('\u001F', EditKey, EditContent, EditCategory);

    private void UpdateDirtyState()
    {
        if (_syncingEditor)
            return;

        HasUnsavedChanges = IsEditing && CaptureEditor() != _editorBaseline;
        OnPropertyChanged(nameof(IsNewMemory));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(DiscardButtonText));
        OnPropertyChanged(nameof(DetailTitle));
    }

    private void NotifyDetailFacts()
    {
        OnPropertyChanged(nameof(IsProjectMemory));
        OnPropertyChanged(nameof(ScopeLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(UpdatedLabel));
        OnPropertyChanged(nameof(CreatedLabel));
        OnPropertyChanged(nameof(HasLastUsed));
        OnPropertyChanged(nameof(LastUsedLabel));
    }

    partial void OnEditKeyChanged(string value) => UpdateDirtyState();
    partial void OnEditContentChanged(string value) => UpdateDirtyState();
    partial void OnEditCategoryChanged(string value)
    {
        MarkChosenCategory();
        UpdateDirtyState();
    }
    partial void OnIsEditingChanged(bool value) => UpdateDirtyState();
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));
    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    [RelayCommand]
    private void SetCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
            EditCategory = category;
    }

    private void ChooseCategory(ManagementFilterOption option) => SetCategory(option.Key);

    private void MarkChosenCategory()
    {
        foreach (var option in CategorySuggestions)
            option.IsSelected = string.Equals(option.Key, EditCategory?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void OpenProject()
    {
        if (SelectedMemory is { ProjectId: { } projectId } memory && IsProjectScoped(memory))
            OpenProjectRequested?.Invoke(projectId);
    }

    [RelayCommand]
    private void SaveMemory()
    {
        if (string.IsNullOrWhiteSpace(EditKey)) return;

        Memory saved;
        if (SelectedMemory is not null)
        {
            SelectedMemory.Key = EditKey.Trim();
            SelectedMemory.Content = EditContent.Trim();
            SelectedMemory.Category = EditCategory.Trim();
            SelectedMemory.Source = "manual";
            SelectedMemory.UpdatedAt = DateTimeOffset.Now;
            saved = SelectedMemory;
        }
        else
        {
            var memory = new Memory
            {
                Key = EditKey.Trim(),
                Content = EditContent.Trim(),
                Category = EditCategory.Trim(),
                Source = "manual"
            };
            _dataStore.Data.Memories.Add(memory);
            saved = memory;
        }

        _ = _dataStore.SaveAsync();

        // Saving keeps the memory open: the page now shows what was stored.
        RefreshList();
        RestoreSelection(saved);
        SyncEditorFromMemory(saved);
        IsEditing = true;
        UpdateDirtyState();
        NotifyDetailFacts();
        FlashSaved();
    }

    /// <summary>Throws away edits: an existing memory goes back to what is stored, a new one is dropped.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        if (SelectedMemory is null)
        {
            IsEditing = false;
            return;
        }

        SyncEditorFromMemory(SelectedMemory);
    }

    private async void FlashSaved()
    {
        var version = ++_savedToastVersion;
        ShowSavedToast = true;
        await Task.Delay(1800);
        if (version == _savedToastVersion)
            ShowSavedToast = false;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (SelectedMemory is not null)
            IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (SelectedMemory is { } memory)
            DeleteMemory(memory);
    }

    [RelayCommand]
    private void DeleteMemory(Memory memory)
    {
        _dataStore.Data.Memories.Remove(memory);
        _ = _dataStore.SaveAsync();
        if (SelectedMemory == memory)
        {
            SelectedMemory = null;
            IsEditing = false;
        }
        RefreshList();
    }

    partial void OnSearchQueryChanged(string value) => RefreshList();
}
