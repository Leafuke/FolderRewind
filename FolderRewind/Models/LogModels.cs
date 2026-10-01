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
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Source { get; set; }
        public string? Exception { get; set; }
    }

    public class LogOptions
    {
        public bool EnableFileLogging { get; set; } = true;
        public int MaxEntries { get; set; } = 4000;
        public int MaxFileSizeKb { get; set; } = 1024 * 5;
        public int RetentionDays { get; set; } = 7;
    }
}
