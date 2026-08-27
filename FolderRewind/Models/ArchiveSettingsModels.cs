using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{

    /// <summary>
    /// 自定义文件类型处理规则：针对匹配指定通配符/后缀的文件使用独立的压缩等级。
    /// 例如 *.mp4 -> 压缩等级 1（仅存储），避免对已压缩媒体文件进行二次压缩浪费时间。
    /// </summary>
    public class FileTypeRule : ObservableObject
    {
        private string _pattern = "";
        private int _compressionLevel = 1;

        /// <summary>
        /// 文件匹配模式，支持通配符（如 *.mp4、*.zip）。
        /// </summary>
        public string Pattern { get => _pattern; set => SetProperty(ref _pattern, value); }

        /// <summary>
        /// 该类型文件使用的压缩等级 (0-9)。0=仅存储，9=最高压缩。
        /// </summary>
        public int CompressionLevel { get => _compressionLevel; set => SetProperty(ref _compressionLevel, value); }
    }

    /// <summary>
    /// 压缩与归档设置
    /// </summary>
    public class ArchiveSettings : ObservableObject
    {
        private string _format = "7z";
        private int _compressionLevel = 5;
        private string _method = "LZMA2";
        private int _keepCount = 0;
        private BackupMode _mode = BackupMode.Full;
        private bool _skipIfUnchanged = true;      // 无变更时跳过备份
        private int _cpuThreads = 0;               // CPU 线程数, 0 = 自动
        private bool _backupBeforeRestore = false;  // 还原前先执行一次备份
        private bool _safeRestoreEnabled = true;     // 安全还原（Clean 模式前做目录快照，失败可回滚）
        private bool _verifyArchiveBeforeRestore = true; // 还原前完整性校验（7z t）
        private int _maxSmartBackupsPerFull = 5;    // 智能备份链长度限制，默认 5
        private bool _runCompressionAtLowPriority = false; // 备份侧 7-Zip 进程以较低优先级运行
        private string _additionalSevenZipArguments = string.Empty;

        // 自定义文件类型处理
        private bool _fileTypeHandlingEnabled = false;
        private ObservableCollection<FileTypeRule> _fileTypeRules = new();

        public string Format { get => _format; set => SetProperty(ref _format, value); }
        public int CompressionLevel { get => _compressionLevel; set => SetProperty(ref _compressionLevel, value); }
        public string Method { get => _method; set => SetProperty(ref _method, value); }
        public int KeepCount { get => _keepCount; set => SetProperty(ref _keepCount, value); }
        public BackupMode Mode { get => _mode; set => SetProperty(ref _mode, value); }

        /// <summary>
        /// 无变更时跳过备份，即使是全量模式也会先检测文件变化
        /// </summary>
        public bool SkipIfUnchanged { get => _skipIfUnchanged; set => SetProperty(ref _skipIfUnchanged, value); }

        /// <summary>
        /// CPU 线程数，0 表示自动（由 7z 决定），传递给 7z 的 -mmt 参数
        /// </summary>
        public int CpuThreads { get => _cpuThreads; set => SetProperty(ref _cpuThreads, value); }

        /// <summary>
        /// 还原前先自动执行一次备份，防止误操作丢失当前数据
        /// </summary>
        public bool BackupBeforeRestore { get => _backupBeforeRestore; set => SetProperty(ref _backupBeforeRestore, value); }

        /// <summary>
        /// 安全还原：在 Clean 还原前将目标目录迁移到临时目录快照，若还原失败可自动回滚。
        /// </summary>
        public bool SafeRestoreEnabled { get => _safeRestoreEnabled; set => SetProperty(ref _safeRestoreEnabled, value); }

        /// <summary>
        /// 还原前完整性校验：对还原链中的压缩包执行 7z test。
        /// </summary>
        public bool VerifyArchiveBeforeRestore { get => _verifyArchiveBeforeRestore; set => SetProperty(ref _verifyArchiveBeforeRestore, value); }

        /// <summary>
        /// 智能备份链长度限制：当连续增量备份达到此数量时，强制执行一次全量备份以截断链条。
        /// 0 表示不限制（增量链可以无限延长）。
        /// 参考 MineBackup 的 maxSmartBackupsPerFull 逻辑。
        /// </summary>
        public int MaxSmartBackupsPerFull { get => _maxSmartBackupsPerFull; set => SetProperty(ref _maxSmartBackupsPerFull, value); }

        /// <summary>
        /// 备份侧压缩任务使用较低的进程优先级，减少对前台游戏/应用的抢占。
        /// 仅影响归档创建、追加压缩与自动清理中的安全删除，不影响还原和校验。
        /// </summary>
        public bool RunCompressionAtLowPriority { get => _runCompressionAtLowPriority; set => SetProperty(ref _runCompressionAtLowPriority, value); }

        /// <summary>
        /// Additional protected 7-Zip switches appended only to backup-side archive creation/update commands.
        /// FolderRewind validates this value before save and again before process launch.
        /// </summary>
        public string AdditionalSevenZipArguments
        {
            get => _additionalSevenZipArguments;
            set => SetProperty(ref _additionalSevenZipArguments, value ?? string.Empty);
        }

        /// <summary>
        /// 是否启用自定义文件类型处理。启用后将关闭固实压缩 (-ms=off)，
        /// 按规则对不同类型文件使用不同压缩等级。
        /// </summary>
        public bool FileTypeHandlingEnabled { get => _fileTypeHandlingEnabled; set => SetProperty(ref _fileTypeHandlingEnabled, value); }

        /// <summary>
        /// 自定义文件类型处理规则列表。每条规则包含一个通配符模式和对应的压缩等级。
        /// </summary>
        public ObservableCollection<FileTypeRule> FileTypeRules
        {
            get => _fileTypeRules;
            set => SetProperty(ref _fileTypeRules, value ?? new ObservableCollection<FileTypeRule>());
        }
    }
}
