using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

public sealed record NotificationCorrelation(string Group, string ItemId, DateTimeOffset ItemCreatedUtc, DateTimeOffset EventUtc);

internal sealed class LatestNotificationTracker
{
    private readonly Dictionary<string, NotificationCorrelation> _latest = new(StringComparer.Ordinal);
    internal bool Accept(NotificationCorrelation? incoming)
    {
        if (incoming is null) return true;
        if (_latest.TryGetValue(incoming.Group, out var previous)
            && (previous == incoming || previous.ItemCreatedUtc > incoming.ItemCreatedUtc
                || previous.ItemCreatedUtc == incoming.ItemCreatedUtc && (previous.EventUtc > incoming.EventUtc
                    || previous.ItemId != incoming.ItemId && StringComparer.Ordinal.Compare(previous.ItemId, incoming.ItemId) > 0))) return false;
        _latest[incoming.Group] = incoming;
        while (_latest.Count > 2048) _latest.Remove(_latest.MinBy(p => p.Value.EventUtc).Key);
        return true;
    }
    internal void Clear() => _latest.Clear();
}
