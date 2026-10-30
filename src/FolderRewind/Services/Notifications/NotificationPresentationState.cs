using System;
using System.Collections.Generic;

namespace FolderRewind.Services;

internal sealed class PriorityNotificationQueue<T>
{
    private readonly LinkedList<T> _items = new();
    public void Enqueue(T item) => _items.AddLast(item);
    public void Enqueue(T item, Comparison<T> comparison)
    {
        var node = _items.First;
        while (node is not null && comparison(item, node.Value) >= 0) node = node.Next;
        if (node is null) _items.AddLast(item);
        else _items.AddBefore(node, item);
    }
    public void EnqueueFirst(T item) => _items.AddFirst(item);
    public void RemoveWhere(Predicate<T> predicate)
    {
        var node = _items.First;
        while (node is not null)
        {
            var next = node.Next;
            if (predicate(node.Value)) _items.Remove(node);
            node = next;
        }
    }
    public bool TryDequeue(out T item)
    {
        if (_items.First is not { } first) { item = default!; return false; }
        item = first.Value;
        _items.RemoveFirst();
        return true;
    }
    public void Clear() => _items.Clear();
}

internal sealed class NotificationCountdown(TimeProvider timeProvider)
{
    private long _started;
    public int RemainingMilliseconds { get; private set; }
    public void Start(int milliseconds)
    {
        RemainingMilliseconds = Math.Max(0, milliseconds);
        _started = timeProvider.GetTimestamp();
    }
    public void Pause()
    {
        if (RemainingMilliseconds == 0) return;
        RemainingMilliseconds = Math.Max(1000, RemainingMilliseconds - (int)timeProvider.GetElapsedTime(_started).TotalMilliseconds);
    }
    public void Resume() => _started = timeProvider.GetTimestamp();
}
