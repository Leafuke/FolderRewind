using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

internal sealed class NotificationActivationQueue
{
    private readonly object _sync = new();
    private readonly Queue<IReadOnlyDictionary<string, string>> _pending = new();
    private bool _ready;
    public bool Ready { get { lock (_sync) return _ready; } }
    public void SetReady(bool ready) { lock (_sync) _ready = ready; }
    public void Enqueue(IEnumerable<KeyValuePair<string, string>> arguments)
    {
        var snapshot = arguments.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        lock (_sync) { _pending.Enqueue(snapshot); while (_pending.Count > 32) _pending.Dequeue(); }
    }
    public bool TryTake(out IReadOnlyDictionary<string, string> arguments)
    {
        lock (_sync)
        {
            if (_ready && _pending.TryDequeue(out var next)) { arguments = next; return true; }
            arguments = new Dictionary<string, string>(); return false;
        }
    }
}

internal static class NotificationActivationRouter
{
    public static bool Route(IReadOnlyDictionary<string, string> args, Func<string, NotificationRecord?> find,
        Action<string> markRead, Func<NotificationTarget, bool> open, Func<string, bool> openLegacy,
        Action<string> showDetails, Action unavailable, Action<string, string>? markVersionRead = null)
    {
        var id = args.GetValueOrDefault("notification");
        if (id is { Length: <= 128 } && find(id) is { } record)
        {
            if (markVersionRead is null) markRead(id); else markVersionRead(id, record.Revision);
            if (record.Target is null or { Kind: NotificationTargetKind.None })
            {
                if (LegacyPage(args) is { } page) return openLegacy(page);
                showDetails(id); return true;
            }
            var opened = open(record.Target);
            if (!opened) showDetails(id);
            return opened;
        }
        if (Enum.TryParse<NotificationTargetKind>(args.GetValueOrDefault("kind"), out var kind) && Enum.IsDefined(kind))
        {
            if (kind == NotificationTargetKind.None && LegacyPage(args) is { } page) return openLegacy(page);
            string? Read(string key) => args.GetValueOrDefault(key) is { Length: > 0 and <= 128 } value ? value : null;
            return open(new(kind, Read("operation"), Read("config"), Read("folder"), Read("task"), Read("run"), Read("version"), Read("job"), Read("product"),
                bool.TryParse(Read("historyCreated"), out var created) ? created : null));
        }
        if (LegacyPage(args) is { } legacy) return openLegacy(legacy);
        unavailable();
        return false;
    }
    private static string? LegacyPage(IReadOnlyDictionary<string, string> args)
        => args.GetValueOrDefault("target") is { } page && page is "Home" or "Tasks" or "History" or "Logs" ? page : null;
}
