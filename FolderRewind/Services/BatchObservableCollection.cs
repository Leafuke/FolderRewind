using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace FolderRewind.Services;

public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.ToArray();
        if (this.SequenceEqual(snapshot)) return;

        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Synchronize(IEnumerable<T> items)
    {
        var next = items.ToArray();
        var index = 0;
        while (index < Count && index < next.Length && EqualityComparer<T>.Default.Equals(this[index], next[index])) index++;
        if (next.Length == Count + 1 && this.Skip(index).SequenceEqual(next.Skip(index + 1)))
            Insert(index, next[index]);
        else if (Count == next.Length + 1 && this.Skip(index + 1).SequenceEqual(next.Skip(index)))
            RemoveAt(index);
        else ReplaceAll(next);
    }
}
