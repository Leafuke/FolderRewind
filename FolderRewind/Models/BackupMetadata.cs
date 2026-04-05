using System;
using System.Collections.Generic;

namespace FolderRewind.Models
{
    /// <summary>
    /// 用于存储备份元数据，实现增量检测
    /// </summary>
    public class BackupMetadata
    {
        public string Version { get; set; } = "2.0";
        public DateTime LastBackupTime { get; set; }
        public string LastBackupFileName { get; set; } = ""; // 上次生成的备份文件名
        public string BasedOnFullBackup { get; set; } = "";  // 基于哪个全量备份（如果是增量链）

        // Key: 文件的相对路径
        // Value: 文件状态 (Hash 或 Size+Time)
        public Dictionary<string, FileState> FileStates { get; set; } = new Dictionary<string, FileState>();

        // 每次备份相对上一次元数据的变化记录，用于精确重建 Smart 还原计划。
        public List<BackupChangeRecord> BackupRecords { get; set; } = new List<BackupChangeRecord>();
    }

    /// <summary>
    /// 新版元数据状态文件（state.json），仅保存最新快照状态。
    /// </summary>
    public class BackupMetadataState
    {
        public string Version { get; set; } = "3.0";
        public DateTime LastBackupTime { get; set; }
        public string LastBackupFileName { get; set; } = "";
        public string BasedOnFullBackup { get; set; } = "";

        // Key: 文件的相对路径
        // Value: 文件状态 (Hash 或 Size+Time)
        public Dictionary<string, FileState> FileStates { get; set; } = new Dictionary<string, FileState>();
    }

    public class BackupChangeRecord
    {
        public string ArchiveFileName { get; set; } = "";
        public string BackupType { get; set; } = "";
        public string BasedOnFullBackup { get; set; } = "";
        public string PreviousBackupFileName { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public List<string> AddedFiles { get; set; } = new List<string>();
        public List<string> ModifiedFiles { get; set; } = new List<string>();
        public List<string> DeletedFiles { get; set; } = new List<string>();

        // Full 备份会记录完整文件列表，供 Smart 还原从基准 Full 建图。
        public List<string> FullFileList { get; set; } = new List<string>();
    }

    public class FileState
    {
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string Hash { get; set; } = ""; // MD5 或 SHA256，视性能要求而定
    }
}
