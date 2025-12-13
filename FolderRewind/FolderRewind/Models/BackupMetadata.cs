using System;
using System.Collections.Generic;

namespace FolderRewind.Models
{
    /// <summary>
    /// 用于存储备份元数据，实现增量检测
    /// </summary>
    public class BackupMetadata
    {
        public string Version { get; set; } = "1.0";
        public DateTime LastBackupTime { get; set; }
        public string LastBackupFileName { get; set; } // 上次生成的备份文件名
        public string BasedOnFullBackup { get; set; }  // 基于哪个全量备份（如果是增量链）

        // Key: 文件的相对路径
        // Value: 文件状态 (Hash 或 Size+Time)
        public Dictionary<string, FileState> FileStates { get; set; } = new Dictionary<string, FileState>();
    }

    public class FileState
    {
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string Hash { get; set; } // MD5 或 SHA256，视性能要求而定
    }
}