using FolderRewind.Services;
using System;

namespace FolderRewind.Models;

public enum NotificationTargetKind { None, Tasks, History, CloudProject, Logs, Configuration, FolderManager }

// Host-owned navigation identity. Never retains a view, task instance or callback.
public sealed record NotificationTarget(
    NotificationTargetKind Kind,
    string? OperationId = null,
    string? ConfigId = null,
    string? FolderId = null,
    string? TaskId = null,
    string? RunId = null,
    string? VersionId = null,
    string? JobId = null);

public sealed record NotificationRecord(
    string Id, string Title, string Message, string Details,
    NotificationSeverity Severity, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    bool IsRead, string ResultKey, NotificationTarget? Target,
    NotificationImportance Importance = NotificationImportance.Info,
    string Revision = "", NotificationContent? Content = null);

public enum NotificationOperationKind { Other, Backup, Restore, Cloud }
public enum NotificationTrigger { Unknown, Manual, Automatic }
public enum NotificationResultKind { Unknown, Running, Success, NoChanges, Warning, Failed, Canceled, RecoveryRequired }
public sealed record NotificationStatistics(int Created, int Unchanged, int Warnings, int Failed);
// Display snapshots, never live configuration or task references.
public sealed record NotificationContent(NotificationOperationKind Operation, NotificationTrigger Trigger,
    NotificationResultKind Result, string ProjectName, string ObjectName, NotificationStatistics? Statistics = null, string Summary = "");

public sealed record NotificationNavigationParameter(NotificationTarget Target);

public sealed record LogNavigationParameter(string OperationId);
