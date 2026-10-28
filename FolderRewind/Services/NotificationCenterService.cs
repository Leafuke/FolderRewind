using FolderRewind.Models;
using System;
using System.Collections.Generic;

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
    public static void MarkRead(string id) => NotificationService.Core.MarkRead(id);
    public static void MarkRead(string id, string revision) => NotificationService.Core.MarkRead(id, revision);
    public static void MarkAllRead() => NotificationService.Core.MarkAllRead();
    public static void ClearRead() => NotificationService.Core.ClearRead();
    public static void UndoClear() => NotificationService.Core.UndoClear();
}
