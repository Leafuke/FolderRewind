using System;
using System.Collections.Generic;

namespace FolderRewind.Models;

public enum BackupRunStatus
{
    Completed = 0,
    Partial = 1
}

public enum BackupRunTriggerSource
{
    Unknown = 0,
    Manual = 1,
    Automatic = 2,
    Remote = 3,
    PluginHotkey = 4,
    Internal = 5
}

public enum BackupRunSourceStatus
{
    NewArchive = 0,
    Reused = 1,
    Failed = 2,
    Unavailable = 3
}

public sealed class BackupRunSourceRecord
{
    public Guid? FolderId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public BackupRunSourceStatus Status { get; set; }
    public string HistoryItemId { get; set; } = string.Empty;
    public string ArchiveFileName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class BackupRunRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConfigId { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public BackupRunTriggerSource TriggerSource { get; set; }
    public string Comment { get; set; } = string.Empty;
    public bool IsImportant { get; set; }
    public BackupRunStatus Status { get; set; }
    public PersistedOperationOutcome Outcome { get; set; }
    public List<OperationDiagnosticRecord> Diagnostics { get; set; } = new();
    public List<BackupRunSourceRecord> Sources { get; set; } = new();
}

public sealed class BackupRunDocument
{
    public const string CurrentMagic = "FolderRewindBackupRuns";
    public const string CurrentSchemaVersion = "2.0";

    public string Magic { get; set; } = CurrentMagic;
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<BackupRunRecord> Runs { get; set; } = new();
}

public sealed class BackupRunRestoreSourceResult
{
    public string FolderPath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class BackupRunRestoreResult
{
    public string RunId { get; set; } = string.Empty;
    public List<BackupRunRestoreSourceResult> Sources { get; set; } = new();
    public bool Success => Sources.Count > 0 && Sources.TrueForAll(source => source.Success);
}

public sealed class BackupRetentionHistoryRecord
{
    public string HistoryItemId { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsImportant { get; set; }
}
