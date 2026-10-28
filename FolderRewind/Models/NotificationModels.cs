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
    bool IsRead, string ResultKey, NotificationTarget? Target);

public sealed record NotificationNavigationParameter(NotificationTarget Target);

public sealed record LogNavigationParameter(string OperationId);
