using System;

namespace FolderRewind.Models
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    public class LogEntry
    {
        // In-memory publication order only; not part of persisted logs or plugin APIs.
        internal long Sequence { get; set; }
        public DateTime Timestamp { get; set; }
        public string DisplayTimestamp => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.CurrentCulture);
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Source { get; set; }
        public string? Exception { get; set; }
        public string? OperationId { get; set; }
        public LogOperationContext? Context { get; set; }
        public bool PersistenceAccepted { get; internal set; } = true;
    }

    public sealed record LogOperationContext(string OperationId, string? OperationType = null,
        string? ConfigId = null, string? SourceId = null, string? RunId = null,
        string? TransactionId = null, string? MergeSessionId = null, string? PlanRevision = null,
        string? ProviderId = null, string? JobId = null, int? JobAttempt = null, int? CommandAttempt = null,
        string? PluginId = null, string? Capability = null, string? CoordinationOperationId = null,
        string? ParentOperationId = null);

    public enum LogLifecycleState { Running, Stopping, Stopped, Faulted }
    public enum LogWriterState { Healthy, Disabled, Degraded, Faulted }
    public enum LogPersistenceStatus { Persisted, Disabled, KnownLoss, Unavailable, Faulted, TimedOut, Rejected }
    public sealed record LogHealthSnapshot(LogLifecycleState LifecycleState, LogWriterState WriterState,
        string? LastWriteFailure, string? LastMaintenanceFailure, DateTimeOffset? LastSuccessfulFlushUtc,
        long FailedWriteAttempts, int ConsecutiveFailures, long RejectedAfterStopCount, bool HasPersistenceGap);
    public sealed record LogPersistenceResult(LogPersistenceStatus Status, LogHealthSnapshot Health)
    {
        public bool Succeeded => Status == LogPersistenceStatus.Persisted;
    }

    public class LogOptions
    {
        public bool EnableFileLogging { get; set; } = true;
        public int MaxEntries { get; set; } = 4000;
        public int MaxFileSizeKb { get; set; } = 1024 * 5;
        public int RetentionDays { get; set; } = 7;
    }
}
