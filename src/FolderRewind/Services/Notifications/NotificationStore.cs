using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal sealed class NotificationStore(TimeProvider clock, int capacity = 200)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, NotificationRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Key, NotificationTarget? Target, string Revision, long At)> _accepted = new(StringComparer.Ordinal);
    private NotificationRecord[] _undo = [];
    private long _clearedAt;
    public IReadOnlyList<NotificationRecord> Snapshot()
    { lock (_sync) return _records.Values.OrderByDescending(r => r.UpdatedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray(); }
    public int UnreadCount { get { lock (_sync) return _records.Values.Count(r => !r.IsRead); } }
    public int ImportantUnreadCount { get { lock (_sync) return _records.Values.Count(r => !r.IsRead && r.Importance >= NotificationImportance.Important); } }
    public bool HasRead { get { lock (_sync) return _records.Values.Any(r => r.IsRead); } }
    public int UndoCount { get { lock (_sync) { ExpireUndo(); return _undo.Length; } } }
    public NotificationRecord? Find(string id) { lock (_sync) return _records.GetValueOrDefault(id); }
    public bool Upsert(NotificationEvent item)
    {
        lock (_sync)
        {
            var previous = _records.GetValueOrDefault(item.Id);
            PruneAccepted();
            if (_accepted.TryGetValue(item.Id, out var accepted) && accepted.Key == item.ResultKey && accepted.Target == item.Target) return false;
            var now = clock.GetUtcNow();
            _accepted[item.Id] = (item.ResultKey, item.Target, item.Revision, clock.GetTimestamp());
            _records[item.Id] = new(item.Id, item.Title, item.Message, item.Details, item.Severity,
                previous?.CreatedUtc ?? item.Product?.PublishedUtc ?? now, item.Product?.PublishedUtc ?? now,
                item.RestoreReadState ?? false, item.ResultKey, item.Target,
                item.Category == NotificationCategory.Cancellation ? NotificationImportance.Info : item.Importance, item.Revision, item.Content, item.Product, item.TechnicalDetails);
            Bound();
            return true;
        }
    }
    public bool Remove(string id)
    { lock (_sync) { _accepted.Remove(id); return _records.Remove(id); } }
    public bool MarkRead(string id, string? revision = null)
    {
        lock (_sync)
        {
            if (!_records.TryGetValue(id, out var record) || record.IsRead || revision is not null && record.Revision != revision) return false;
            _records[id] = record with { IsRead = true };
            return true;
        }
    }
    public void MarkAllRead()
    { lock (_sync) foreach (var id in _records.Keys.ToArray()) _records[id] = _records[id] with { IsRead = true }; }

    public int ClearRead()
    {
        lock (_sync)
        {
            _undo = _records.Values.Where(r => r.IsRead).ToArray();
            _clearedAt = clock.GetTimestamp();
            foreach (var record in _undo) _records.Remove(record.Id);
            return _undo.Length;
        }
    }
    public void UndoClear()
    {
        lock (_sync)
        {
            ExpireUndo();
            foreach (var record in _undo)
                if (!_records.ContainsKey(record.Id) && _accepted.TryGetValue(record.Id, out var latest) && latest.Revision == record.Revision)
                    _records[record.Id] = record;
            _undo = [];
            Bound();
        }
    }
    private void ExpireUndo() { if (clock.GetElapsedTime(_clearedAt) >= TimeSpan.FromSeconds(10)) _undo = []; }
    private void Bound()
    { while (_records.Count > Math.Max(1, capacity)) _records.Remove(_records.Values.MinBy(r => r.UpdatedUtc)!.Id); }
    private void PruneAccepted()
    {
        ExpireUndo();
        foreach (var id in _accepted.Where(p => !_records.ContainsKey(p.Key) && clock.GetElapsedTime(p.Value.At) >= TimeSpan.FromHours(24)).Select(p => p.Key).ToArray())
            _accepted.Remove(id);
        while (_accepted.Count >= 2048) _accepted.Remove(_accepted.Where(p => !_records.ContainsKey(p.Key)).MinBy(p => p.Value.At).Key);
    }
}
