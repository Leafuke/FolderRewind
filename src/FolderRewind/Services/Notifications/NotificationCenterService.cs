using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services;

/// <summary>Session singleton facade over the independently testable notification store.</summary>
internal static class NotificationCenterService
{
    public static event Action? Changed { add => NotificationService.Core.Changed += value; remove => NotificationService.Core.Changed -= value; }
    public static IReadOnlyList<NotificationRecord> Snapshot() => NotificationService.Core.Store.Snapshot();
    public static int UnreadCount => NotificationService.Core.Store.UnreadCount;
    public static int ImportantUnreadCount => NotificationService.Core.Store.ImportantUnreadCount;
    public static bool HasRead => NotificationService.Core.Store.HasRead;
    public static int UndoCount => NotificationService.Core.Store.UndoCount;
    public static NotificationRecord? Find(string id) => NotificationService.Core.Store.Find(id);
    public static void MarkRead(string id) => MarkRead(id, Find(id)?.Revision ?? "");
    public static void MarkRead(string id, string revision)
    {
        NotificationService.Core.MarkRead(id, revision);
        if (Find(id) is { IsRead: true } record && record.Revision == revision && record.Product is not null)
            ProductMessageService.MarkReadRecords([record]);
    }
    public static void MarkAllRead()
    {
        NotificationService.Core.MarkAllRead();
        ProductMessageService.MarkReadRecords(Snapshot());
    }
    public static void ClearRead()
    {
        var cleared = Snapshot().Where(r => r.IsRead).ToArray();
        NotificationService.Core.ClearRead(); ProductMessageService.Cleared(cleared);
    }
    public static void UndoClear()
    {
        NotificationService.Core.UndoClear(); ProductMessageService.UndoCleared(Snapshot());
    }
}
