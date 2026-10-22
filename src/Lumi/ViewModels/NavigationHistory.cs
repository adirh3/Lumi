using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumi.Services.Discover;

namespace Lumi.ViewModels;

internal record NavigationState(int PageIndex)
{
    internal virtual bool IsSameLocation(NavigationState other) => Equals(other);
}

internal sealed record ChatNavigationState(Guid? ChatId, Guid? ProjectFilterId) : NavigationState(0);

internal sealed record SettingsNavigationState(int SettingsPageIndex, string SearchQuery = "") : NavigationState(7)
{
    internal override bool IsSameLocation(NavigationState other)
        => other is SettingsNavigationState settings
           && SettingsPageIndex == settings.SettingsPageIndex
           && string.IsNullOrWhiteSpace(SearchQuery) == string.IsNullOrWhiteSpace(settings.SearchQuery);
}

internal sealed record ManagementNavigationState(
    int PageIndex,
    Guid? ItemId,
    bool IsEditing,
    string? ExternalItemKey = null,
    string? CategoryFilter = null,
    string? BriefKey = null) : NavigationState(PageIndex);

internal sealed record DiscoverNavigationState(
    DiscoverFilter Filter,
    string? SourceKey,
    DiscoverCard? Card,
    string SearchQuery) : NavigationState(MainViewModel.DiscoverNavIndex)
{
    internal override bool IsSameLocation(NavigationState other)
        => other is DiscoverNavigationState discover && Filter == discover.Filter
           && SourceKey == discover.SourceKey && ReferenceEquals(Card, discover.Card)
           && string.IsNullOrWhiteSpace(SearchQuery) == string.IsNullOrWhiteSpace(discover.SearchQuery);
}

internal sealed record LibraryNavigationState(
    string? ItemKey,
    string KindId,
    string OriginId,
    string TimeRangeId,
    string ProjectId,
    string SearchQuery) : NavigationState(MainViewModel.LibraryNavIndex)
{
    internal override bool IsSameLocation(NavigationState other)
        => other is LibraryNavigationState library && ItemKey == library.ItemKey
           && KindId == library.KindId && OriginId == library.OriginId
           && TimeRangeId == library.TimeRangeId && ProjectId == library.ProjectId
           && string.IsNullOrWhiteSpace(SearchQuery) == string.IsNullOrWhiteSpace(library.SearchQuery);
}

internal sealed record JobNavigationState(Guid? JobId, bool IsEditing) : NavigationState(1);

internal sealed class NavigationHistory
{
    private const int MaxEntries = 200;
    private readonly List<NavigationState> _entries = [];
    private int _currentIndex = -1;

    public bool IsRestoring { get; private set; }

    public void Record(NavigationState entry)
    {
        if (IsRestoring)
            return;

        // Keep a search's latest text without adding a Back step for every keystroke.
        if (HasCurrentEntry && _entries[_currentIndex].IsSameLocation(entry))
        {
            _entries[_currentIndex] = entry;
            return;
        }

        TruncateForwardHistory();
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
        _currentIndex = _entries.Count - 1;
    }

    public void RemoveChat(Guid chatId)
    {
        RemoveEntries(entry => entry is ChatNavigationState chat && chat.ChatId == chatId);
    }

    public async Task<bool> TryNavigateAsync(
        int direction,
        Func<NavigationState, bool> isAvailable,
        Func<NavigationState, Task<bool>> applyAsync)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));

        ArgumentNullException.ThrowIfNull(isAvailable);
        ArgumentNullException.ThrowIfNull(applyAsync);

        if (IsRestoring)
            return false;

        var targetIndex = _currentIndex + direction;
        while (targetIndex >= 0 && targetIndex < _entries.Count)
        {
            var target = _entries[targetIndex];
            if (!isAvailable(target))
            {
                RemoveEntryAt(targetIndex);
                if (direction < 0)
                    targetIndex--;
                continue;
            }

            using var _ = BeginRestoreScope();
            if (!await applyAsync(target))
                return false;

            // Removing a chat while an awaited restore runs can shift the target's index.
            var appliedIndex = _entries.FindIndex(entry => ReferenceEquals(entry, target));
            if (appliedIndex < 0)
                return false;

            _currentIndex = appliedIndex;
            return true;
        }

        return false;
    }

    private bool HasCurrentEntry => _currentIndex >= 0 && _currentIndex < _entries.Count;

    private void TruncateForwardHistory()
    {
        if (_currentIndex < _entries.Count - 1)
        {
            _entries.RemoveRange(
                _currentIndex + 1,
                _entries.Count - _currentIndex - 1);
        }
    }

    private void RemoveEntries(Func<NavigationState, bool> shouldRemove)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!shouldRemove(_entries[i]))
                continue;

            RemoveEntryAt(i);
        }
    }

    private void RemoveEntryAt(int index)
    {
        _entries.RemoveAt(index);
        if (index < _currentIndex)
            _currentIndex--;
        else if (index == _currentIndex)
            _currentIndex = Math.Min(_currentIndex, _entries.Count - 1);
    }

    private IDisposable BeginRestoreScope()
    {
        IsRestoring = true;
        return new RestoreScope(this);
    }

    private sealed class RestoreScope(NavigationHistory owner) : IDisposable
    {
        private readonly NavigationHistory _owner = owner;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner.IsRestoring = false;
        }
    }
}
