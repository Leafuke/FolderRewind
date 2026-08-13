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

    public class HistoryItem : ObservableObject
    {
        // 核心字段 (需要保存)
        public string Id { get; set; } = "";
        public string CreatedByRunId { get; set; } = "";
        public string ConfigId { get; set; } = "";        // 所属配置ID
        public Guid? FolderId { get; set; }                // v3 stable source identity; null for legacy history
        public string FolderPath { get; set; } = "";      // 所属源文件夹路径 (作为唯一标识)
        public string FolderName { get; set; } = "";      // 文件夹名 (冗余备份，防止源被删后无法识别)
        public string FileName { get; set; } = "";        // 备份文件名 (如 [Full]...7z)
        public DateTime Timestamp { get; set; }     // 备份时间
        public string BackupType { get; set; } = "";      // Full, Smart, Overwrite
        public Guid? ArtifactRootId { get; set; }
        public string ArtifactGraphRevision { get; set; } = string.Empty;
        public PersistedOperationOutcome Outcome { get; set; }
        public List<OperationDiagnosticRecord> Diagnostics { get; set; } = new();

        /// <summary>
        /// 是否为部分备份。白名单/插件区域备份会标记它，Clean 还原时需要额外提醒。
        /// </summary>
        public bool IsPartialBackup { get; set; }

        private string _comment = "";
        public string Comment
        {
            get => _comment;
            set => SetProperty(ref _comment, value ?? string.Empty);
        }

        private bool _isImportant;
        public bool IsImportant
        {
            get => _isImportant;
            set => SetProperty(ref _isImportant, value);
        }

        private bool _isCloudArchived;
        /// <summary>
        /// 是否确认存在云端副本。实际可下载性仍需结合远端路径字段判断。
        /// </summary>
        public bool IsCloudArchived
        {
            get => _isCloudArchived;
            set => SetProperty(ref _isCloudArchived, value);
        }

        public DateTime CloudArchivedAtUtc { get; set; }

        private string _cloudArchiveRemotePath = "";
        /// <summary>
        /// 归档文件在云端的完整路径。
        /// </summary>
        public string CloudArchiveRemotePath
        {
            get => _cloudArchiveRemotePath;
            set => SetProperty(ref _cloudArchiveRemotePath, value ?? string.Empty);
        }

        private string _cloudMetadataRecordRemotePath = "";
        /// <summary>
        /// 云端 records/{archive}.json 路径，用于增量链路定位。
        /// </summary>
        public string CloudMetadataRecordRemotePath
        {
            get => _cloudMetadataRecordRemotePath;
            set => SetProperty(ref _cloudMetadataRecordRemotePath, value ?? string.Empty);
        }

        private string _cloudMetadataStateRemotePath = "";
        /// <summary>
        /// 云端 state.json 路径，用于恢复元数据状态。
        /// </summary>
        public string CloudMetadataStateRemotePath
        {
            get => _cloudMetadataStateRemotePath;
            set => SetProperty(ref _cloudMetadataStateRemotePath, value ?? string.Empty);
        }

        // --- UI 辅助属性 (不存入 JSON) ---

        [JsonIgnore]
        public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

        [JsonIgnore]
        public string DateDisplay => Timestamp.ToString("yyyy-MM-dd");

        [JsonIgnore]
        public string Message => string.IsNullOrEmpty(Comment)
            ? I18n.Format("HistoryItem_Message_NoComment", BackupType)
            : I18n.Format("HistoryItem_Message_WithComment", BackupType, Comment);

        [JsonIgnore]
        public string FileSizeDisplay { get; set; } = "-"; // 需动态获取

        private bool _isMissing;
        /// <summary>
        /// 运行时状态：备份文件是否缺失
        /// </summary>
        [JsonIgnore]
        public bool IsMissing { get => _isMissing; set => SetProperty(ref _isMissing, value); }

        private bool _hasLocalFile;
        [JsonIgnore]
        public bool HasLocalFile { get => _hasLocalFile; set => SetProperty(ref _hasLocalFile, value); }

        private bool _hasCloudCopy;
        [JsonIgnore]
        public bool HasCloudCopy { get => _hasCloudCopy; set => SetProperty(ref _hasCloudCopy, value); }

        private bool _isCloudOnly;
        [JsonIgnore]
        public bool IsCloudOnly { get => _isCloudOnly; set => SetProperty(ref _isCloudOnly, value); }

        private bool _isSmallFile;
        /// <summary>
        /// 运行时状态：备份文件大小低于警告阈值
        /// </summary>
        [JsonIgnore]
        public bool IsSmallFile { get => _isSmallFile; set => SetProperty(ref _isSmallFile, value); }

        private string _cloudStatusText = "";
        [JsonIgnore]
        public string CloudStatusText { get => _cloudStatusText; set => SetProperty(ref _cloudStatusText, value ?? string.Empty); }

        private string _cloudActionHintText = "";
        [JsonIgnore]
        public string CloudActionHintText { get => _cloudActionHintText; set => SetProperty(ref _cloudActionHintText, value ?? string.Empty); }

        private string _downloadFromCloudHintText = "";
        [JsonIgnore]
        public string DownloadFromCloudHintText { get => _downloadFromCloudHintText; set => SetProperty(ref _downloadFromCloudHintText, value ?? string.Empty); }

        private bool _canUploadToCloud;
        [JsonIgnore]
        public bool CanUploadToCloud { get => _canUploadToCloud; set => SetProperty(ref _canUploadToCloud, value); }

        private bool _canDownloadFromCloud;
        [JsonIgnore]
        public bool CanDownloadFromCloud { get => _canDownloadFromCloud; set => SetProperty(ref _canDownloadFromCloud, value); }

        private Brush? _timelineLineBrush;
        private Brush? _timelineNodeFillBrush;
        private Brush? _timelineNodeBorderBrush;

        [JsonIgnore]
        public Brush? TimelineLineBrush { get => _timelineLineBrush; set => SetProperty(ref _timelineLineBrush, value); }

        [JsonIgnore]
        public Brush? TimelineNodeFillBrush { get => _timelineNodeFillBrush; set => SetProperty(ref _timelineNodeFillBrush, value); }

        [JsonIgnore]
        public Brush? TimelineNodeBorderBrush { get => _timelineNodeBorderBrush; set => SetProperty(ref _timelineNodeBorderBrush, value); }
    }

    public class BackupTask : ObservableObject
    {
        private string _folderName = "";
        private double _progress; // 0 - 100
        private string _status = "";
        private string _speed = "";
        private bool _isCompleted;
        private string _log = ""; // 实时日志片段
        private string _errorMessage = "";
        private bool _isIndeterminate = true;
        private bool _isSuccess;
        private string _iconGlyph = "\uE8B7"; // Segoe MDL2 SaveLocal（备份图标）

        public string FolderName { get => _folderName; set => SetProperty(ref _folderName, value ?? string.Empty); }
        public double Progress
        {
            get => _progress;
            set
            {
                if (SetProperty(ref _progress, value))
                    OnPropertyChanged(nameof(ProgressText));
            }
        }
        public string Status { get => _status; set => SetProperty(ref _status, value ?? string.Empty); }
        public string Speed { get => _speed; set => SetProperty(ref _speed, value ?? string.Empty); }
        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                SetProperty(ref _isCompleted, value);
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        // 这里的 Log 用于给 TaskPage 显示详细信息
        public string Log { get => _log; set => SetProperty(ref _log, value ?? string.Empty); }

        /// <summary>
        /// 失败原因（仅在任务失败时有值），通常来自 7z 的 stderr 输出
        /// </summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                SetProperty(ref _errorMessage, value ?? string.Empty);
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        /// <summary>
        /// 进度条是否为不确定模式（尚未收到 7z 进度数据时为 true）
        /// </summary>
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set
            {
                if (SetProperty(ref _isIndeterminate, value))
                    OnPropertyChanged(nameof(ProgressText));
            }
        }

        /// <summary>
        /// 任务是否成功完成
        /// </summary>
        public bool IsSuccess
        {
            get => _isSuccess;
            set
            {
                SetProperty(ref _isSuccess, value);
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        /// <summary>
        /// 任务图标（备份/还原使用不同图标）
        /// </summary>
        public string IconGlyph { get => _iconGlyph; set => SetProperty(ref _iconGlyph, value ?? string.Empty); }

        /// <summary>
        /// 格式化的进度文本（如 "42%"），不确定模式时为空
        /// </summary>
        [JsonIgnore]
        public string ProgressText => IsIndeterminate ? "" : $"{Progress:F0}%";

        /// <summary>
        /// 返回与任务状态对应的颜色画刷。
        /// 失败: 严重色/红色; 成功: 成功色/绿色; 运行中: 强调色。
        /// </summary>
        [JsonIgnore]
        public SolidColorBrush? StatusBrush
        {
            get
            {
                if (IsCompleted && !IsSuccess && !string.IsNullOrEmpty(ErrorMessage))
                    return (SolidColorBrush?)Application.Current.Resources["SystemFillColorCriticalBrush"];
                if (IsCompleted && IsSuccess)
                    return (SolidColorBrush?)Application.Current.Resources["SystemFillColorSuccessBrush"];
                return (SolidColorBrush?)Application.Current.Resources["AccentFillColorDefaultBrush"];
            }
        }
    }
}
