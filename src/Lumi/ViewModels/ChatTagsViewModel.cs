using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public sealed record ChatTagColorOption(string Hex);

public sealed record ChatTagAssignment(Chat Chat, ChatTag? Tag);

public partial class ChatTagsViewModel : ObservableObject, IDisposable
{
    private readonly DataStore _dataStore;
    private readonly ObservableCollection<ChatTag> _tags = [];
    private bool _isDisposed;

    public IReadOnlyList<ChatTagColorOption> ColorOptions { get; } =
        ChatTag.Palette.Select(static hex => new ChatTagColorOption(hex)).ToArray();

    public ReadOnlyObservableCollection<ChatTag> Tags { get; }

    public bool HasTags => _dataStore.Data.ChatTags.Count > 0;
    public bool HasNoTags => !HasTags;
    public bool IsEditorVisible => IsCreating || SelectedTag is not null;
    public bool CanDeleteSelectedTag => !IsCreating && SelectedTag is not null;
    public string EditorTitle => IsCreating ? Loc.ChatTags_NewTitle : Loc.ChatTags_EditTitle;

    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private ChatTag? _selectedTag;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private ChatTagColorOption? _selectedColorOption;
    [ObservableProperty] private string _validationMessage = "";

    public ChatTagsViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        Tags = new(_tags);
        RefreshTagList();
        _selectedColorOption = ColorOptions[0];
        _dataStore.ChatTagCatalogChanged += OnChatTagCatalogChanged;
    }

    [RelayCommand]
    private void OpenManager()
    {
        RefreshTagList();
        IsDialogOpen = true;

        var selected = SelectedTag is null
            ? null
            : _dataStore.Data.ChatTags.FirstOrDefault(tag => tag.Id == SelectedTag.Id);
        if (selected is not null)
        {
            SelectedTag = selected;
            SyncEditor(selected);
            return;
        }

        var first = Tags.FirstOrDefault();
        if (first is not null)
            SelectedTag = first;
        else
            NewTag();
    }

    [RelayCommand]
    private void CloseManager()
    {
        IsDialogOpen = false;
        ValidationMessage = "";
    }

    [RelayCommand]
    private void NewTag()
    {
        IsCreating = true;
        SelectedTag = null;
        EditName = "";
        SelectedColorOption = ColorOptions[0];
        ValidationMessage = "";
        NotifyEditorStateChanged();
    }

    [RelayCommand]
    private async Task SaveTag()
    {
        var name = EditName.Trim();
        if (name.Length == 0)
        {
            ValidationMessage = Loc.ChatTags_NameRequired;
            return;
        }

        if (_dataStore.Data.FindChatTagByName(name, exceptId: SelectedTag?.Id) is not null)
        {
            ValidationMessage = Loc.ChatTags_DuplicateName;
            return;
        }

        var color = SelectedColorOption?.Hex ?? ChatTag.DefaultColor;
        ChatTag savedTag;
        if (IsCreating)
        {
            savedTag = new ChatTag { Name = name, Color = color };
            _dataStore.Data.ChatTags.Add(savedTag);
        }
        else
        {
            var existingTag = SelectedTag is null
                ? null
                : _dataStore.Data.ChatTags.FirstOrDefault(tag => tag.Id == SelectedTag.Id);
            if (existingTag is null)
            {
                ValidationMessage = Loc.ChatTags_TagNoLongerExists;
                ReconcileSelection();
                return;
            }

            savedTag = existingTag;
            savedTag.Name = name;
            savedTag.Color = color;
        }

        foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.TagId == savedTag.Id))
        {
            if (ReferenceEquals(chat.Tag, savedTag))
                chat.NotifyTagDetailsChanged();
            else
                chat.Tag = savedTag;
        }

        IsCreating = false;
        RefreshTagList();
        SelectedTag = savedTag;
        ValidationMessage = "";
        await _dataStore.SaveAsync();
        _dataStore.NotifyChatTagCatalogChanged(savedTag.Id, this);
    }

    [RelayCommand]
    private async Task DeleteTag()
    {
        if (IsCreating || SelectedTag is null)
        {
            ValidationMessage = Loc.ChatTags_SelectTag;
            return;
        }

        var tagId = SelectedTag.Id;
        _dataStore.Data.ChatTags.RemoveAll(tag => tag.Id == tagId);
        foreach (var chat in _dataStore.Data.Chats.Where(chat => chat.TagId == tagId))
        {
            chat.TagId = null;
            chat.Tag = null;
            _dataStore.MarkChatChanged(chat);
        }

        SelectedTag = null;
        RefreshTagList();
        var first = Tags.FirstOrDefault();
        if (first is not null)
            SelectedTag = first;
        else
            NewTag();
        await _dataStore.SaveAsync();
        _dataStore.NotifyChatTagCatalogChanged(tagId, this);
    }

    [RelayCommand]
    private async Task AssignTag(ChatTagAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!_dataStore.Data.Chats.Contains(assignment.Chat))
            throw new ArgumentException("The chat is not part of this data store.", nameof(assignment));
        if (assignment.Tag is not null
            && !_dataStore.Data.ChatTags.Any(tag => tag.Id == assignment.Tag.Id))
        {
            throw new ArgumentException("The tag is not part of this data store.", nameof(assignment));
        }

        if (assignment.Chat.TagId == assignment.Tag?.Id
            && ReferenceEquals(assignment.Chat.Tag, assignment.Tag))
        {
            return;
        }

        assignment.Chat.TagId = assignment.Tag?.Id;
        assignment.Chat.Tag = assignment.Tag;
        _dataStore.MarkChatChanged(assignment.Chat);
        await _dataStore.SaveAsync();
    }

    public void ResolveTag(Chat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var tag = chat.TagId is { } tagId
            ? _dataStore.Data.ChatTags.FirstOrDefault(candidate => candidate.Id == tagId)
            : null;
        chat.Tag = tag;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _dataStore.ChatTagCatalogChanged -= OnChatTagCatalogChanged;
    }

    partial void OnSelectedTagChanged(ChatTag? value)
    {
        if (value is not null)
        {
            IsCreating = false;
            SyncEditor(value);
        }

        NotifyEditorStateChanged();
    }

    partial void OnIsCreatingChanged(bool value) => NotifyEditorStateChanged();

    private void SyncEditor(ChatTag tag)
    {
        EditName = tag.Name;
        SelectedColorOption = ColorOptions.FirstOrDefault(option =>
                                  string.Equals(option.Hex, tag.Color, StringComparison.OrdinalIgnoreCase))
                              ?? ColorOptions[0];
        ValidationMessage = "";
    }

    private void RefreshTagList()
    {
        var sorted = _dataStore.Data.ChatTags.OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (var index = _tags.Count - 1; index >= 0; index--)
        {
            if (!sorted.Contains(_tags[index]))
                _tags.RemoveAt(index);
        }

        for (var index = 0; index < sorted.Length; index++)
        {
            var existing = _tags.IndexOf(sorted[index]);
            if (existing < 0)
                _tags.Insert(index, sorted[index]);
            else if (existing != index)
                _tags.Move(existing, index);
        }

        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasNoTags));
    }

    private void OnChatTagCatalogChanged(Guid tagId, object? source)
    {
        if (_isDisposed || ReferenceEquals(source, this))
            return;

        if (SelectedTag?.Id == tagId)
            ReconcileSelection();
        RefreshTagList();
    }

    private void ReconcileSelection()
    {
        if (SelectedTag is null)
            return;

        var current = _dataStore.Data.ChatTags.FirstOrDefault(tag => tag.Id == SelectedTag.Id);
        if (current is not null)
        {
            SelectedTag = current;
            SyncEditor(current);
            return;
        }

        SelectedTag = null;
        IsCreating = false;
        EditName = "";
        SelectedColorOption = ColorOptions[0];
        ValidationMessage = Loc.ChatTags_TagNoLongerExists;
        NotifyEditorStateChanged();
    }

    private void NotifyEditorStateChanged()
    {
        OnPropertyChanged(nameof(IsEditorVisible));
        OnPropertyChanged(nameof(CanDeleteSelectedTag));
        OnPropertyChanged(nameof(EditorTitle));
    }
}
