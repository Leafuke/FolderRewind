using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services;

/// <summary>Session-only bounded result store. Presentation and notification preferences are independent.</summary>
internal static class NotificationCenterService
{
    private const int Capacity = 500;
    private readonly record struct Entry(NotificationRecord Record, int AlertPriority);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    public static event Action? Changed;

    public static IReadOnlyList<NotificationRecord> Snapshot()
    {
        lock (Sync) return Entries.Values.Select(e => e.Record).OrderByDescending(r => r.UpdatedUtc)
            .ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }

    public static int UnreadCount { get { lock (Sync) return Entries.Values.Count(e => !e.Record.IsRead); } }
    public static NotificationRecord? Find(string id) { lock (Sync) return Entries.GetValueOrDefault(id).Record; }

    internal static (NotificationRecord Record, bool Changed, bool Alert) Upsert(InAppNotificationRequest request,
        NotificationImportance? importance, bool canAlert)
    {
        var id = request.RecordId ?? Guid.NewGuid().ToString("N");
        var details = Limit(CloudCommandSecurity.Redact(string.IsNullOrWhiteSpace(request.Details) ? request.Message : request.Details), 8192);
        var title = Limit(CloudCommandSecurity.Redact(request.Title), 512);
        var message = Limit(CloudCommandSecurity.Redact(request.Message), 2048);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            request.ResultKey ?? $"{request.Severity}\n{title}\n{message}\n{details}")));
        NotificationRecord record;
        bool alert;
        lock (Sync)
        {
            var exists = Entries.TryGetValue(id, out var previous);
            if (exists && previous.Record.ResultKey == key && previous.Record.Target == request.Target)
                return (previous.Record, false, false);
            var priority = importance is { } level ? (int)level : 0;
            alert = canAlert && (!request.AlertOnce || !exists || priority > previous.AlertPriority);
            var now = DateTimeOffset.UtcNow;
            record = new(id, title, message, details, request.Severity,
                exists ? previous.Record.CreatedUtc : now, now, false, key, request.Target);
            Entries[id] = new(record, alert ? Math.Max(exists ? previous.AlertPriority : -1, priority)
                : exists ? previous.AlertPriority : -1);
            while (Entries.Count > Capacity)
                Entries.Remove(Entries.Values.MinBy(e => e.Record.UpdatedUtc).Record.Id);
        }
        NotifyChanged();
        return (record, true, alert);
    }

    public static void MarkRead(string id)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(id, out var entry) || entry.Record.IsRead) return;
            Entries[id] = entry with { Record = entry.Record with { IsRead = true } };
        }
        NotifyChanged();
    }

    public static void MarkAllRead()
    {
        lock (Sync)
        {
            foreach (var id in Entries.Keys.ToArray())
                Entries[id] = Entries[id] with { Record = Entries[id].Record with { IsRead = true } };
        }
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        if (Changed is not { } changed) return;
        foreach (Action handler in changed.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"[NotificationCenter] Observer failed: {error}"); }
        }
    }

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
