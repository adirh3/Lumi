using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Lumi.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole contents in one notification.
///
/// Clearing and re-adding item by item raises one CollectionChanged event per item, and a
/// virtualizing panel does layout work for every one of them. The Library republishes a list of
/// thousands of rows on each progress report of a long scan, which turns into hundreds of thousands
/// of notifications on the UI thread. <see cref="Reset"/> collapses that to a single Reset event.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces every element, raising a single Reset notification.</summary>
    public void Reset(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
