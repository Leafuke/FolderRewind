using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal sealed class NotificationExecutionOrigins(TimeProvider clock)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _manual = new(StringComparer.Ordinal);
    public void RequestManual(string jobId)
    {
        lock (_sync)
        {
            Prune();
            _manual[jobId] = clock.GetTimestamp();
            while (_manual.Count > 2048) _manual.Remove(_manual.MinBy(p => p.Value).Key);
        }
    }
    public NotificationTrigger Take(string jobId, bool automatic)
    {
        lock (_sync)
        {
            Prune();
            return _manual.Remove(jobId) || !automatic ? NotificationTrigger.Manual : NotificationTrigger.Automatic;
        }
    }
    private void Prune()
    {
        foreach (var id in _manual.Where(p => clock.GetElapsedTime(p.Value) >= TimeSpan.FromHours(24)).Select(p => p.Key).ToArray()) _manual.Remove(id);
    }
}
