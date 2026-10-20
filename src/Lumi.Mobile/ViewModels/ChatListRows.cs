using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Lumi.Mobile.ViewModels;

/// <summary>
/// The grouped chat list as one flat sequence of group headers and their rows. A single virtualizing
/// panel over it recycles row controls across date groups; a panel per group would realize every
/// group's rows from scratch as it scrolled into view.
/// </summary>
public sealed class ChatListRows : IReadOnlyList<object>, IList, INotifyCollectionChanged
{
    private readonly ObservableCollection<ChatGroupViewModel> _groups;
    private readonly List<object> _items = [];
    // Row counts as last mirrored. A group's own collection has already changed when its event
    // arrives, so offsets are computed from these instead.
    private readonly Dictionary<ChatGroupViewModel, int> _mirroredCounts = new(ReferenceEqualityComparer.Instance);

    public ChatListRows(ObservableCollection<ChatGroupViewModel> groups)
    {
        _groups = groups;
        _groups.CollectionChanged += OnGroupsChanged;
        foreach (var group in _groups)
            Track(group);
        Rebuild();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _items.Count;

    public object this[int index] => _items[index];

    public IEnumerator<object> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                InsertGroups(e.NewStartingIndex, e.NewItems.Cast<ChatGroupViewModel>());
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                RemoveGroups(e.OldStartingIndex, e.OldItems.Cast<ChatGroupViewModel>());
                break;
            case NotifyCollectionChangedAction.Replace when e.OldItems is not null && e.NewItems is not null:
                RemoveGroups(e.OldStartingIndex, e.OldItems.Cast<ChatGroupViewModel>());
                InsertGroups(e.NewStartingIndex, e.NewItems.Cast<ChatGroupViewModel>());
                break;
            case NotifyCollectionChangedAction.Move
                when e.OldItems?.Count == 1 && e.OldItems[0] is ChatGroupViewModel moved:
                // The mirror still has the old order: the current order with the moved group put back.
                var previousOrder = _groups.ToList();
                previousOrder.RemoveAt(e.NewStartingIndex);
                previousOrder.Insert(e.OldStartingIndex, moved);
                var from = 0;
                for (var i = 0; i < e.OldStartingIndex; i++)
                    from += 1 + _mirroredCounts.GetValueOrDefault(previousOrder[i]);
                var block = _items.GetRange(from, 1 + _mirroredCounts.GetValueOrDefault(moved));
                Remove(from, block.Count);
                Insert(OffsetOf(e.NewStartingIndex), block);
                break;
            default:
                foreach (var group in _handlers.Keys.ToList())
                    Untrack(group);
                foreach (var group in _groups)
                    Track(group);
                Rebuild();
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                break;
        }
    }

    private void InsertGroups(int groupIndex, IEnumerable<ChatGroupViewModel> groups)
    {
        var offset = OffsetOf(groupIndex);
        var block = new List<object>();
        foreach (var group in groups)
        {
            Track(group);
            block.Add(group);
            block.AddRange(group.Chats);
            _mirroredCounts[group] = group.Chats.Count;
        }

        Insert(offset, block);
    }

    private void RemoveGroups(int groupIndex, IEnumerable<ChatGroupViewModel> groups)
    {
        var removed = groups.ToList();
        Remove(OffsetOf(groupIndex), removed.Sum(group => 1 + _mirroredCounts.GetValueOrDefault(group)));
        foreach (var group in removed)
            Untrack(group);
    }

    private void OnChatsChanged(ChatGroupViewModel group, NotifyCollectionChangedEventArgs e)
    {
        var groupIndex = _groups.IndexOf(group);
        if (groupIndex < 0 || !_mirroredCounts.ContainsKey(group))
            return;

        var first = OffsetOf(groupIndex) + 1;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                Insert(first + e.NewStartingIndex, e.NewItems.Cast<object>().ToList());
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                Remove(first + e.OldStartingIndex, e.OldItems.Count);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldItems is not null && e.NewItems is not null:
                Remove(first + e.OldStartingIndex, e.OldItems.Count);
                Insert(first + e.NewStartingIndex, e.NewItems.Cast<object>().ToList());
                break;
            case NotifyCollectionChangedAction.Move when e.OldItems is not null:
                Remove(first + e.OldStartingIndex, e.OldItems.Count);
                Insert(first + e.NewStartingIndex, e.OldItems.Cast<object>().ToList());
                break;
            default:
                Remove(first, _mirroredCounts[group]);
                Insert(first, group.Chats.Cast<object>().ToList());
                break;
        }

        _mirroredCounts[group] = group.Chats.Count;
    }

    private int OffsetOf(int groupIndex)
    {
        var offset = 0;
        for (var i = 0; i < groupIndex && i < _groups.Count; i++)
            offset += 1 + _mirroredCounts.GetValueOrDefault(_groups[i]);
        return offset;
    }

    private void Insert(int index, List<object> items)
    {
        if (items.Count == 0)
            return;
        _items.InsertRange(index, items);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items, index));
    }

    private void Remove(int index, int count)
    {
        if (count == 0)
            return;
        var removed = _items.GetRange(index, count);
        _items.RemoveRange(index, count);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, index));
    }

    private void Rebuild()
    {
        _items.Clear();
        foreach (var group in _groups)
        {
            _items.Add(group);
            _items.AddRange(group.Chats);
            _mirroredCounts[group] = group.Chats.Count;
        }
    }

    private void Track(ChatGroupViewModel group)
    {
        if (_handlers.ContainsKey(group))
            return;
        NotifyCollectionChangedEventHandler handler = (_, e) => OnChatsChanged(group, e);
        _handlers[group] = handler;
        group.Chats.CollectionChanged += handler;
    }

    private void Untrack(ChatGroupViewModel group)
    {
        if (_handlers.Remove(group, out var handler))
            group.Chats.CollectionChanged -= handler;
        _mirroredCounts.Remove(group);
    }

    private readonly Dictionary<ChatGroupViewModel, NotifyCollectionChangedEventHandler> _handlers =
        new(ReferenceEqualityComparer.Instance);

    // IList lets item controls index the rows without copying them.
    bool IList.IsFixedSize => false;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    object? IList.this[int index] { get => _items[index]; set => throw new NotSupportedException(); }
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is not null && _items.Contains(value);
    int IList.IndexOf(object? value) => value is null ? -1 : _items.IndexOf(value);
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
}
