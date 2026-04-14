using System.Collections.Generic;

namespace FolderRewind.Models
{
    public enum ConfigCloudSyncMode
    {
        HistoryOnly = 0,
        HistoryAndBackups = 1
    }

    public sealed class ConfigCloudHistoryAnalysisResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int TotalRemoteEntries { get; set; }

        public int MatchedEntries { get; set; }

        public int ImportableEntries { get; set; }

        public int UnmappedEntries { get; set; }

        public int AmbiguousEntries { get; set; }

        public IReadOnlyList<HistoryItem> MappedItems { get; set; } = [];
    }

    public sealed class ConfigCloudSyncResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int ImportedHistoryCount { get; set; }

        public int DuplicateHistoryCount { get; set; }

        public int RecoveredBackupCount { get; set; }

        public ConfigCloudHistoryAnalysisResult Analysis { get; set; } = new();
    }
}
