using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

/// <summary>UI-owner buffer: live appends are incremental; snapshots and filter changes publish one reset.</summary>
internal sealed class LogPresentationController
{
    private readonly int _capacity;
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly HashSet<LogEntry> _seen = new(ReferenceEqualityComparer.Instance);
    private long _generation;
    private bool _active;
    private string _keyword = string.Empty;
    private LogLevel? _level;
    private long _discardThrough;

    public LogPresentationController(int capacity = 5000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public BatchObservableCollection<LogEntry> FilteredEntries { get; } = new();
    public bool IsLive { get; private set; } = true;

    public long BeginSession()
    {
        _active = true;
        return Interlocked.Increment(ref _generation);
    }

    public void EndSession()
    {
        _active = false;
        Interlocked.Increment(ref _generation);
    }

    public void LoadSnapshot(IEnumerable<LogEntry> snapshot)
    {
        _entries.Clear();
        _seen.Clear();
        _discardThrough = 0;
        foreach (var entry in snapshot.TakeLast(_capacity))
        {
            if (_seen.Add(entry)) _entries.AddLast(entry);
            _discardThrough = Math.Max(_discardThrough, entry.Sequence);
        }
        RefreshFiltered();
    }

    public bool Append(LogEntry entry, long generation)
    {
        if (!_active || generation != Volatile.Read(ref _generation)
            || (entry.Sequence > 0 && entry.Sequence <= _discardThrough)
            || !_seen.Add(entry)) return false;
        // LogService assigns sequence under its buffer lock, but publishers may
        // enqueue UI callbacks in a different order after leaving that lock.
        var previous = _entries.Last;
        if (entry.Sequence > 0)
        {
            while (previous is not null && previous.Value.Sequence > entry.Sequence) previous = previous.Previous;
        }
        if (previous is null) _entries.AddFirst(entry);
        else _entries.AddAfter(previous, entry);
        while (_entries.Count > _capacity)
        {
            var removed = _entries.First!.Value;
            _entries.RemoveFirst();
            _seen.Remove(removed);
            _discardThrough = Math.Max(_discardThrough, removed.Sequence);
            if (IsLive && FilteredEntries.Count > 0)
            {
                if (ReferenceEquals(FilteredEntries[0], removed)) FilteredEntries.RemoveAt(0);
                else FilteredEntries.Remove(removed);
            }
        }
        if (!IsLive || !_seen.Contains(entry) || !Passes(entry)) return false;
        var index = FilteredEntries.Count;
        if (entry.Sequence > 0)
        {
            while (index > 0 && FilteredEntries[index - 1].Sequence > entry.Sequence) index--;
        }
        FilteredEntries.Insert(index, entry);
        return true;
    }

    public void SetFilter(LogLevel? level, string? keyword)
    {
        _level = level;
        _keyword = keyword ?? string.Empty;
        RefreshFiltered();
    }

    public void SetLive(bool isLive)
    {
        if (IsLive == isLive) return;
        IsLive = isLive;
        if (isLive) RefreshFiltered();
    }

    private void RefreshFiltered() => FilteredEntries.ReplaceAll(_entries.Where(Passes));

    private bool Passes(LogEntry entry)
        => (!_level.HasValue || entry.Level == _level)
            && (string.IsNullOrWhiteSpace(_keyword)
                || $"{entry.Message} {entry.Exception} {entry.Source}".Contains(_keyword, StringComparison.OrdinalIgnoreCase));
}
