using System.Collections.Generic;
using System;

namespace FolderRewind.Models
{
    /// <summary>
    /// 配置级云同步模式：只同步历史，或历史+备份包一起同步。
    /// </summary>
    public enum ConfigCloudSyncMode
    {
        HistoryOnly = 0,
        HistoryAndBackups = 1
    }

    /// <summary>
    /// 云端历史分析结果，供 UI 先预览再执行同步。
    /// </summary>
    public sealed class ConfigCloudHistoryAnalysisResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int TotalRemoteEntries { get; set; }

        public int MatchedEntries { get; set; }

        public int ImportableEntries { get; set; }

        public int UnmappedEntries { get; set; }

        public int AmbiguousEntries { get; set; }

        // 已映射到本地 ManagedFolder 的可导入项。
        public IReadOnlyList<HistoryItem> MappedItems { get; set; } = [];
    }

    /// <summary>
    /// 配置级云同步执行结果（分析 + 导入 + 可选备份下载）。
    /// </summary>
    public sealed class ConfigCloudSyncResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int ImportedHistoryCount { get; set; }

        public int DuplicateHistoryCount { get; set; }

        public int RecoveredBackupCount { get; set; }

        public ConfigCloudHistoryAnalysisResult Analysis { get; set; } = new();
    }

    /// <summary>
    /// 配置级历史上传结果。
    /// </summary>
    public sealed class ConfigCloudHistoryUploadResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int UploadedEntryCount { get; set; }

        public int ReplacedRemoteEntryCount { get; set; }
    }

    /// <summary>
    /// 当前配置在云端仍视为有效的历史清单。
    /// 只用于过滤导入/下载范围，不会删除归档本体。
    /// </summary>
    public sealed class CloudActiveHistoryManifest
    {
        public string ConfigId { get; set; } = string.Empty;

        public string ConfigName { get; set; } = string.Empty;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public List<CloudActiveHistoryEntry> Entries { get; set; } = [];
    }

    public sealed class CloudActiveHistoryEntry
    {
        public string FolderPath { get; set; } = string.Empty;

        public string FolderName { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; }
    }
}
