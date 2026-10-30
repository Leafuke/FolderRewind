using System.Collections.Generic;
using System;

namespace FolderRewind.Models
{
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

    }

    /// <summary>
    /// 配置级云历史元数据同步执行结果；载荷由按版本恢复入口下载。
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

}
