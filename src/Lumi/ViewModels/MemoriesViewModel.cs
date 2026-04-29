using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class MemoriesViewModel : ObservableObject
{
    private readonly DataStore _dataStore;

    [ObservableProperty] private Memory? _selectedMemory;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editKey = "";
    [ObservableProperty] private string _editContent = "";
    [ObservableProperty] private string _editCategory = "General";
    [ObservableProperty] private string _searchQuery = "";

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    public ObservableCollection<Memory> Memories { get; } = [];

     public MemoriesViewModel(DataStore dataStore)
     {
         _dataStore = dataStore;
         RefreshList();
     }

     public void RefreshFromStore()
     {
         RefreshList();

         if (SelectedMemory is null)
             return;

         var selectedMemory = _dataStore.Data.Memories.FirstOrDefault(memory => memory.Id == SelectedMemory.Id);
         if (selectedMemory is null
             || !string.Equals(selectedMemory.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase))
         {
             SelectedMemory = null;
             IsEditing = false;
             return;
         }

         if (!ReferenceEquals(SelectedMemory, selectedMemory))
         {
             SelectedMemory = selectedMemory;
             return;
         }

         SyncEditorFromMemory(selectedMemory);
     }

    private void RefreshList()
    {
        Memories.Clear();
        var activeMemories = _dataStore.Data.Memories
            .Where(m => string.Equals(m.Status, MemoryStatuses.Active, StringComparison.OrdinalIgnoreCase));
        var items = string.IsNullOrWhiteSpace(SearchQuery)
            ? activeMemories
            : activeMemories.Where(m =>
                m.Key.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                m.Content.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                m.Category.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        foreach (var memory in items.OrderBy(m => m.Category).ThenBy(m => m.Key))
            Memories.Add(memory);
    }

    [RelayCommand]
    private void NewMemory()
    {
        SelectedMemory = null;
        EditKey = "";
        EditContent = "";
        EditCategory = "General";
        IsEditing = true;
    }

    [RelayCommand]
    private void EditMemory(Memory memory)
    {
        SelectedMemory = memory;
    }

     partial void OnSelectedMemoryChanged(Memory? value)
     {
         if (value is null) return;
         SyncEditorFromMemory(value);
         IsEditing = true;
     }

     private void SyncEditorFromMemory(Memory memory)
     {
         EditKey = memory.Key;
         EditContent = memory.Content;
         EditCategory = memory.Category;
     }

    [RelayCommand]
    private void SaveMemory()
    {
        if (string.IsNullOrWhiteSpace(EditKey)) return;

        if (SelectedMemory is not null)
        {
            SelectedMemory.Key = EditKey.Trim();
            SelectedMemory.Content = EditContent.Trim();
            SelectedMemory.Category = EditCategory.Trim();
            SelectedMemory.Source = "manual";
            SelectedMemory.UpdatedAt = DateTimeOffset.Now;
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
        }

        _ = _dataStore.SaveAsync();
        IsEditing = false;
        RefreshList();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
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
