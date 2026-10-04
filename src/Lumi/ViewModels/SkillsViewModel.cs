using System;
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

public partial class SkillsViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    public event Action? SkillsChanged;

    /// <summary>
    /// Raised to open the share sheet for a skill; the shell owns the sheet. The flag is true when the
    /// open editor holds edits that the shared (saved) version does not include.
    /// </summary>
    public event Action<Skill, bool>? ShareRequested;

    /// <summary>Raised for one-click "Copy for chat"; the flag has the same meaning as for <see cref="ShareRequested"/>.</summary>
    public event Action<Skill, bool>? CopyForChatRequested;

    /// <summary>Raised when a Lumi in "Used by" is clicked; the shell switches to the Lumis page.</summary>
    public event Action<Guid>? OpenAgentRequested;

    private string? _editorBaseline;
    private bool _restoringSelection;
    private bool _syncingEditor;
    private int _savedToastVersion;

    /// <summary>Raised to open the import sheet; the shell owns the sheet.</summary>
    public event Action? ImportRequested;

    [ObservableProperty] private Skill? _selectedSkill;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private string _editIconGlyph = "⚡";
    [ObservableProperty] private string _searchQuery = "";

    /// <summary>The open skill differs from what is stored.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    /// <summary>Instructions are shown rendered rather than as editable markdown.</summary>
    [ObservableProperty] private bool _isPreviewingContent;

    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _showSavedToast;

    /// <summary>0 sorts by name, 1 puts the newest first.</summary>
    [ObservableProperty] private int _sortIndex;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<Skill> Skills { get; } = [];

    /// <summary>The overview gallery: the same filtered list as <see cref="Skills"/>, with usage.</summary>
    public ObservableCollection<SkillCard> SkillCards { get; } = [];

    /// <summary>Lumis that include the open skill.</summary>
    public ObservableCollection<RelatedItem> UsedByAgents { get; } = [];

     public SkillsViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
     }

    public bool IsNewSkill => IsEditing && SelectedSkill is null;
    public bool ShowSaveBar => IsEditing && (HasUnsavedChanges || IsNewSkill) && !IsConfirmingDelete;
    public bool CanSave => !string.IsNullOrWhiteSpace(EditName);
    public string SaveButtonText => IsNewSkill ? Loc.Skills_Create : Loc.Mg_SaveChanges;
    public string SaveBarText => IsNewSkill ? Loc.Mg_NotSavedYet : Loc.Mg_Unsaved;
    public string DiscardButtonText => IsNewSkill ? Loc.Common_Cancel : Loc.Mg_Discard;
    public string DetailTitle => string.IsNullOrWhiteSpace(EditName) ? Loc.Skills_NewTitle : EditName;
    public bool HasAnySkills => _dataStore.Data.Skills.Count > 0;
    public bool HasResults => SkillCards.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool ShowNoResults => HasAnySkills && !HasResults;
    public bool ShowEmptyState => !HasAnySkills;
    public string NoResultsText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_NoResults, SearchQuery.Trim());
    public int TotalCount => _dataStore.Data.Skills.Count;
    public int InUseCount => _dataStore.Data.Skills.Count(skill => _dataStore.Data.Agents.Any(agent => agent.SkillIds.Contains(skill.Id)));
    public string TotalCountText => TotalCount.ToString(CultureInfo.CurrentCulture);
    public string InUseCountText => InUseCount.ToString(CultureInfo.CurrentCulture);
    public string ContentSizeLabel => ManagementText.Size(EditContent);
    public bool HasContent => !string.IsNullOrWhiteSpace(EditContent);

    /// <summary>Only feeds the markdown renderer while the preview is showing, so typing never re-renders it.</summary>
    public string PreviewContent => IsPreviewingContent ? EditContent : "";
    public string DeleteConfirmText => string.Format(CultureInfo.CurrentCulture, Loc.Mg_DeleteConfirm, DetailTitle);

    /// <summary>A long rendered preview has been unfolded.</summary>
    [ObservableProperty] private bool _isPreviewExpanded;

    public bool IsPreviewLong => ManagementText.IsLongMarkdown(EditContent);
    public bool IsPreviewClipped => IsPreviewLong && !IsPreviewExpanded;
    public string PreviewToggleText => IsPreviewExpanded ? Loc.Mg_ShowLess : Loc.Mg_ShowMore;

    [RelayCommand]
    private void TogglePreviewExpanded() => IsPreviewExpanded = !IsPreviewExpanded;

    partial void OnIsPreviewExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPreviewClipped));
        OnPropertyChanged(nameof(PreviewToggleText));
    }
    public bool HasUsedBy => UsedByAgents.Count > 0;
    public bool IsBuiltInSkill => SelectedSkill?.IsBuiltIn == true;
    public string CreatedLabel => SelectedSkill is { } skill
        ? string.Format(CultureInfo.CurrentCulture, Loc.Mg_Created, ManagementText.Date(skill.CreatedAt))
        : "";
    public string UsedByLabel => HasUsedBy
        ? ManagementText.Count(UsedByAgents.Count, Loc.Skills_UsedByOne, Loc.Skills_UsedByMany)
        : Loc.Skills_NotUsed;
    public int ActiveChatCount => SelectedSkill is { } skill
        ? _dataStore.Data.Chats.Count(chat => chat.ActiveSkillIds.Contains(skill.Id))
        : 0;
    public bool HasActiveChats => ActiveChatCount > 0;
    public string ActiveChatsLabel => ManagementText.Count(ActiveChatCount, Loc.Skills_ActiveInChatsOne, Loc.Skills_ActiveInChatsMany);
    public bool IsSortedByName => SortIndex == 0;
    public bool IsSortedByRecent => SortIndex == 1;

     public void RefreshFromStore()
     {
         // Rebuilding the list clears the sidebar's selection, and through its two-way binding the
         // open skill. Put it back quietly afterwards: the editor stays attached, stays open or closed
         // as it was, and keeps edits in progress. Without edits it shows what is stored now.
         var open = SelectedSkill;
         var hasUnsavedEdits = IsEditing && CaptureEditor() != _editorBaseline;
         RefreshList();

         if (open is null)
             return;

         var stored = _dataStore.Data.Skills.FirstOrDefault(skill => skill.Id == open.Id);
         if (stored is null)
         {
             SelectedSkill = null;
             IsEditing = false;
             return;
         }

         RestoreSelection(stored);
         if (!hasUnsavedEdits)
             SyncEditorFromSkill(stored);
         RefreshRelations(stored);
     }

     private void RestoreSelection(Skill skill)
     {
         _restoringSelection = true;
         try
         {
             SelectedSkill = skill;
         }
         finally
         {
             _restoringSelection = false;
         }
     }

    private void RefreshList()
    {
        Skills.Clear();
        var hasQuery = !string.IsNullOrWhiteSpace(SearchQuery);
        var items = hasQuery
            ? SearchPipeline.Rank(
                _dataStore.Data.Skills,
                SearchQuery,
                static skill =>
                [
                    SearchField.Primary(skill.Name, 3.4),
                    new SearchField(skill.Description, 1.8),
                    SearchField.Content(skill.Content, 0.95)
                ],
                static skill => new SearchSortMetadata(Text: skill.Name))
            : SortIndex == 1
                ? _dataStore.Data.Skills.OrderByDescending(skill => skill.CreatedAt).ToArray()
                : _dataStore.Data.Skills.OrderBy(skill => skill.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        foreach (var skill in items)
            Skills.Add(skill);

        SkillCards.Clear();
        foreach (var skill in Skills)
        {
            var usedBy = _dataStore.Data.Agents.Count(agent => agent.SkillIds.Contains(skill.Id));
            SkillCards.Add(new SkillCard(skill, usedBy, EditSkill));
        }

        // The sidebar follows the open skill one way; re-announcing it re-selects the row after a rebuild.
        OnPropertyChanged(nameof(SelectedSkill));
        NotifyOverviewChanged();
    }

    private void NotifyOverviewChanged()
    {
        OnPropertyChanged(nameof(HasAnySkills));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(InUseCount));
        OnPropertyChanged(nameof(InUseCountText));
    }

    [RelayCommand]
    private void NewSkill()
    {
        SelectedSkill = null;
        _syncingEditor = true;
        try
        {
            EditName = "";
            EditDescription = "";
            EditContent = "";
            EditIconGlyph = "⚡";
        }
        finally
        {
            _syncingEditor = false;
        }

        _editorBaseline = CaptureEditor();
        IsPreviewingContent = false;
        IsPreviewExpanded = false;
        IsConfirmingDelete = false;
        UsedByAgents.Clear();
        IsEditing = true;
        UpdateDirtyState();
    }

    [RelayCommand]
    private void EditSkill(Skill skill)
    {
        SelectedSkill = skill;
    }

    /// <summary>Opens a skill by id, clearing a search that would hide it from the list.</summary>
    public void OpenById(Guid id)
    {
        if (_dataStore.Data.Skills.FirstOrDefault(skill => skill.Id == id) is not { } skill)
            return;

        if (!Skills.Contains(skill))
            SearchQuery = "";
        if (ReferenceEquals(SelectedSkill, skill))
        {
            IsEditing = true;
            return;
        }

        SelectedSkill = skill;
    }

    /// <summary>Back to the overview.</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        IsConfirmingDelete = false;
        SelectedSkill = null;
        IsEditing = false;
    }

     partial void OnSelectedSkillChanged(Skill? value)
     {
         if (value is null || _restoringSelection) return;
         SyncEditorFromSkill(value);
         IsPreviewingContent = !string.IsNullOrWhiteSpace(value.Content);
         IsPreviewExpanded = false;
         IsConfirmingDelete = false;
         RefreshRelations(value);
         IsEditing = true;
     }

     private void SyncEditorFromSkill(Skill skill)
     {
         _syncingEditor = true;
         try
         {
             EditName = skill.Name;
             EditDescription = skill.Description;
             EditContent = skill.Content;
             EditIconGlyph = skill.IconGlyph;
         }
         finally
         {
             _syncingEditor = false;
         }

         _editorBaseline = CaptureEditor();
         UpdateDirtyState();
     }

    private void RefreshRelations(Skill skill)
    {
        UsedByAgents.Clear();
        foreach (var agent in _dataStore.Data.Agents
                     .Where(agent => agent.SkillIds.Contains(skill.Id))
                     .OrderBy(agent => agent.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            UsedByAgents.Add(new RelatedItem(agent.Id, agent.Name, string.IsNullOrWhiteSpace(agent.IconGlyph) ? "✦" : agent.IconGlyph));
        }

        OnPropertyChanged(nameof(HasUsedBy));
        OnPropertyChanged(nameof(UsedByLabel));
        OnPropertyChanged(nameof(ActiveChatCount));
        OnPropertyChanged(nameof(HasActiveChats));
        OnPropertyChanged(nameof(ActiveChatsLabel));
        OnPropertyChanged(nameof(CreatedLabel));
        OnPropertyChanged(nameof(IsBuiltInSkill));
    }

    private string CaptureEditor() => string.Join('\u001F', EditName, EditDescription, EditContent, EditIconGlyph);

    private void UpdateDirtyState()
    {
        if (_syncingEditor)
            return;

        HasUnsavedChanges = IsEditing && CaptureEditor() != _editorBaseline;
        NotifyEditorState();
    }

    private void NotifyEditorState()
    {
        OnPropertyChanged(nameof(IsNewSkill));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(DiscardButtonText));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(ContentSizeLabel));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsPreviewLong));
        OnPropertyChanged(nameof(IsPreviewClipped));
        if (IsPreviewingContent)
            OnPropertyChanged(nameof(PreviewContent));
    }

    partial void OnEditNameChanged(string value) => UpdateDirtyState();
    partial void OnEditDescriptionChanged(string value) => UpdateDirtyState();
    partial void OnEditContentChanged(string value) => UpdateDirtyState();
    partial void OnEditIconGlyphChanged(string value) => UpdateDirtyState();
    partial void OnIsEditingChanged(bool value) => UpdateDirtyState();
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(ShowSaveBar));
    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    partial void OnIsPreviewingContentChanged(bool value) => OnPropertyChanged(nameof(PreviewContent));

    [RelayCommand]
    private void SaveSkill()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        Skill saved;
        if (SelectedSkill is not null)
        {
            SelectedSkill.Name = EditName.Trim();
            SelectedSkill.Description = EditDescription.Trim();
            SelectedSkill.Content = EditContent.Trim();
            SelectedSkill.IconGlyph = EditIconGlyph;
            saved = SelectedSkill;
        }
        else
        {
            var skill = new Skill
            {
                Name = EditName.Trim(),
                Description = EditDescription.Trim(),
                Content = EditContent.Trim(),
                IconGlyph = EditIconGlyph
            };
            _dataStore.Data.Skills.Add(skill);
            saved = skill;
        }

        _ = _dataStore.SaveAsync();
        _dataStore.SyncSkillFiles();

        // Saving keeps the skill open: the page now shows what was stored.
        RefreshList();
        RestoreSelection(saved);
        SyncEditorFromSkill(saved);
        RefreshRelations(saved);
        IsEditing = true;
        UpdateDirtyState();
        FlashSaved();
        SkillsChanged?.Invoke();
    }

    /// <summary>Throws away edits: an existing skill goes back to what is stored, a new one is dropped.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        if (SelectedSkill is null)
        {
            IsEditing = false;
            return;
        }

        SyncEditorFromSkill(SelectedSkill);
        UpdateDirtyState();
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
    private void ShowPreview() => IsPreviewingContent = true;

    [RelayCommand]
    private void ShowEditor() => IsPreviewingContent = false;

    [RelayCommand]
    private void RequestDelete()
    {
        if (SelectedSkill is not null)
            IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (SelectedSkill is { } skill)
            DeleteSkill(skill);
    }

    [RelayCommand]
    private void DeleteSkill(Skill skill)
    {
        var result = new LumiFeatureManager(_dataStore).ManageSkills("delete", identifier: skill.Id.ToString());
        if (!result.DataChanged)
            return;

        _ = _dataStore.SaveAsync();
        _dataStore.SyncSkillFiles();
        if (SelectedSkill == skill)
        {
            SelectedSkill = null;
            IsEditing = false;
        }
        RefreshList();
        SkillsChanged?.Invoke();
    }

    [RelayCommand]
    private void OpenAgent(RelatedItem? item)
    {
        if (item is not null)
            OpenAgentRequested?.Invoke(item.Id);
    }

    [RelayCommand]
    private void SortByName() => SortIndex = 0;

    [RelayCommand]
    private void SortByRecent() => SortIndex = 1;

    partial void OnSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedByRecent));
        RefreshList();
    }

    [RelayCommand]
    private void ShareSkill(Skill? skill)
    {
        skill ??= SelectedSkill;
        if (skill is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(skill, SelectedSkill) && CaptureEditor() != _editorBaseline;
        ShareRequested?.Invoke(skill, hasUnsavedEdits);
    }

    [RelayCommand]
    private void CopySkillForChat(Skill? skill)
    {
        skill ??= SelectedSkill;
        if (skill is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(skill, SelectedSkill) && CaptureEditor() != _editorBaseline;
        CopyForChatRequested?.Invoke(skill, hasUnsavedEdits);
    }

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke();

    partial void OnSearchQueryChanged(string value) => RefreshList();
}
