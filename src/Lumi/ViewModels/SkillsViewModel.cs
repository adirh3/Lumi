using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private string? _editorBaseline;

    /// <summary>Raised to open the import sheet; the shell owns the sheet.</summary>
    public event Action? ImportRequested;

    [ObservableProperty] private Skill? _selectedSkill;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private string _editIconGlyph = "⚡";
    [ObservableProperty] private string _searchQuery = "";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<Skill> Skills { get; } = [];

     public SkillsViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
     }

     public void RefreshFromStore()
     {
         RefreshList();

         if (SelectedSkill is null)
             return;

         var selectedSkill = _dataStore.Data.Skills.FirstOrDefault(skill => skill.Id == SelectedSkill.Id);
         if (selectedSkill is null)
         {
             SelectedSkill = null;
             IsEditing = false;
             return;
         }

         if (!ReferenceEquals(SelectedSkill, selectedSkill))
         {
             SelectedSkill = selectedSkill;
             return;
         }

         SyncEditorFromSkill(selectedSkill);
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
            : _dataStore.Data.Skills.OrderBy(skill => skill.Name).ToArray();

        foreach (var skill in items)
            Skills.Add(skill);
    }

    [RelayCommand]
    private void NewSkill()
    {
        SelectedSkill = null;
        EditName = "";
        EditDescription = "";
        EditContent = "";
        EditIconGlyph = "⚡";
        IsEditing = true;
    }

    [RelayCommand]
    private void EditSkill(Skill skill)
    {
        SelectedSkill = skill;
    }

     partial void OnSelectedSkillChanged(Skill? value)
     {
         if (value is null) return;
         SyncEditorFromSkill(value);
         IsEditing = true;
     }

     private void SyncEditorFromSkill(Skill skill)
     {
         EditName = skill.Name;
         EditDescription = skill.Description;
         EditContent = skill.Content;
         EditIconGlyph = skill.IconGlyph;
         _editorBaseline = CaptureEditor();
     }

    private string CaptureEditor() => string.Join('\u001F', EditName, EditDescription, EditContent, EditIconGlyph);

    [RelayCommand]
    private void SaveSkill()
    {
        if (string.IsNullOrWhiteSpace(EditName)) return;

        if (SelectedSkill is not null)
        {
            SelectedSkill.Name = EditName.Trim();
            SelectedSkill.Description = EditDescription.Trim();
            SelectedSkill.Content = EditContent.Trim();
            SelectedSkill.IconGlyph = EditIconGlyph;
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
        }

        _ = _dataStore.SaveAsync();
        _dataStore.SyncSkillFiles();
        IsEditing = false;
        RefreshList();
        SkillsChanged?.Invoke();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
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
    private void ShareSkill(Skill? skill)
    {
        skill ??= SelectedSkill;
        if (skill is null)
            return;

        var hasUnsavedEdits = IsEditing && ReferenceEquals(skill, SelectedSkill) && CaptureEditor() != _editorBaseline;
        ShareRequested?.Invoke(skill, hasUnsavedEdits);
    }

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke();

    partial void OnSearchQueryChanged(string value) => RefreshList();
}
