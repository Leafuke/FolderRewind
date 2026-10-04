using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace FolderRewind.Services;

/// <summary>UI-owned task registry; only terminal records are eligible for eviction.</summary>
public sealed class BackupTaskCollection : ObservableCollection<BackupTask>
{
    private readonly Dictionary<BackupTask, bool> _states = new();
    private readonly LinkedList<BackupTask> _completed = new();
    private bool _pruning;
    public int RunningCount { get; private set; }
    public event Action? RunningCountChanged;

    protected override void InsertItem(int index, BackupTask item)
    {
        if (_states.ContainsKey(item)) return;
        _states.Add(item, item.IsCompleted);
        item.PropertyChanged += OnTaskChanged;
        if (item.IsCompleted) _completed.AddLast(item);
        else RunningCount++;
        base.InsertItem(index, item);
        RunningCountChanged?.Invoke();
        Prune();
    }

    protected override void RemoveItem(int index)
    {
        var item = this[index];
        item.PropertyChanged -= OnTaskChanged;
        if (!_states[item]) RunningCount--;
        _states.Remove(item);
        _completed.Remove(item);
        base.RemoveItem(index);
        RunningCountChanged?.Invoke();
    }

    protected override void SetItem(int index, BackupTask item)
    {
        if (ReferenceEquals(this[index], item)) return;
        if (_states.ContainsKey(item)) throw new ArgumentException("Task is already registered.", nameof(item));
        RemoveItem(index);
        InsertItem(index, item);
    }

    protected override void ClearItems()
    {
        foreach (var item in this) item.PropertyChanged -= OnTaskChanged;
        _states.Clear(); _completed.Clear(); RunningCount = 0;
        base.ClearItems();
        RunningCountChanged?.Invoke();
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not BackupTask item || args.PropertyName is not (null or "" or nameof(BackupTask.IsCompleted))) return;
        if (!_states.TryGetValue(item, out var previous) || previous == item.IsCompleted) return;
        _states[item] = item.IsCompleted;
        RunningCount += item.IsCompleted ? -1 : 1;
        if (item.IsCompleted) _completed.AddLast(item);
        else _completed.Remove(item);
        RunningCountChanged?.Invoke();
        Prune();
    }

    private void Prune()
    {
        if (_pruning) return;
        _pruning = true;
        try { while (_completed.Count > 200) Remove(_completed.First!.Value); }
        finally { _pruning = false; }
    }
}
