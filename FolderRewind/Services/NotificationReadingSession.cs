using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>Reading snapshot independent of controls and the live store.</summary>
internal sealed class NotificationReadingSession
{
    private IReadOnlyList<NotificationRecord> _latest = [];
    private IReadOnlyList<NotificationRecord> _displayed = [];
    public bool IsOpen { get; private set; }
    public bool Protected { get; private set; }
    public bool ImportantOnly { get; set; }
    public bool UnreadOnly { get; set; }
    public int PendingCount => _latest.Count(r => !_displayed.Any(d => d.Id == r.Id && d.Revision == r.Revision));
    public IReadOnlyList<NotificationRecord> Visible => _displayed.Where(r => (!ImportantOnly || r.Importance >= NotificationImportance.Important)
        && (!UnreadOnly || !r.IsRead)).ToArray();
    public void Open(IReadOnlyList<NotificationRecord> latest)
    {
        IsOpen = true; Protected = false; UnreadOnly = false;
        ImportantOnly = latest.Any(r => !r.IsRead && r.Importance >= NotificationImportance.Important);
        Observe(latest, true);
    }
    public void Close() { IsOpen = false; Protected = false; }
    public void SetProtection(bool value)
    {
        Protected = value;
    }
    public void Observe(IReadOnlyList<NotificationRecord> latest, bool force = false)
    {
        var waitingForRefresh = PendingCount > 0;
        _latest = latest;
        if (!IsOpen || !Protected && !waitingForRefresh || force) ApplyPending();
        else
        {
            // A read-state change is safe to reflect without replacing the content being read.
            var current = latest.ToDictionary(r => r.Id);
            _displayed = _displayed.Select(r => current.TryGetValue(r.Id, out var next) && next.Revision == r.Revision
                ? r with { IsRead = next.IsRead } : r).ToArray();
        }
    }
    public void ApplyPending() => _displayed = _latest;
}
