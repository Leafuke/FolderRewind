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

    public enum CloudCommandMode
    {
        Rclone = 0,
        Custom = 1
    }

    public enum CloudTemplateKind
    {
        UploadCurrentArchive = 0,
        UploadBackupDirectory = 1,
        Custom = 2
    }

    /// <summary>
    /// 云执行配置（策略层）。
    /// 这里负责保存“怎么跑命令”，具体执行与重试由 CloudSyncService 统一处理。
    /// </summary>
    public class CloudSettings : ObservableObject
    {
        private bool _enabled;
        private CloudCommandMode _commandMode = CloudCommandMode.Rclone;
        private CloudTemplateKind _templateKind = CloudTemplateKind.UploadCurrentArchive;
        private string _executablePath = "rclone.exe";
        private string _argumentsTemplate = "copyto \"{ArchiveFilePath}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}/{ArchiveFileName}\"";
        private string _workingDirectory = "";
        private int _timeoutSeconds = 600;
        private int _retryCount;
        // 推荐写法：remoteName:path，例如 remote:FolderRewind
        private string _remoteBasePath = "remote:FolderRewind";
        private bool _syncHistoryAfterUpload;
        private DateTime _lastRunUtc = DateTime.MinValue;
        private int _lastExitCode;
        private string _lastErrorMessage = "";

        public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

        public CloudCommandMode CommandMode { get => _commandMode; set => SetProperty(ref _commandMode, value); }

        public CloudTemplateKind TemplateKind { get => _templateKind; set => SetProperty(ref _templateKind, value); }

        public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value ?? string.Empty); }

        public string ArgumentsTemplate { get => _argumentsTemplate; set => SetProperty(ref _argumentsTemplate, value ?? string.Empty); }

        public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value ?? string.Empty); }

        public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, value); }

        public int RetryCount { get => _retryCount; set => SetProperty(ref _retryCount, value); }

        public string RemoteBasePath { get => _remoteBasePath; set => SetProperty(ref _remoteBasePath, value ?? string.Empty); }

        public bool SyncHistoryAfterUpload { get => _syncHistoryAfterUpload; set => SetProperty(ref _syncHistoryAfterUpload, value); }

        public DateTime LastRunUtc
        {
            get => _lastRunUtc;
            set
            {
                if (SetProperty(ref _lastRunUtc, value))
                {
                    OnPropertyChanged(nameof(LastRunDisplay));
                    OnPropertyChanged(nameof(LastExitCodeDisplay));
                }
            }
        }

        public int LastExitCode
        {
            get => _lastExitCode;
            set
            {
                if (SetProperty(ref _lastExitCode, value))
                {
                    OnPropertyChanged(nameof(LastExitCodeDisplay));
                }
            }
        }

        public string LastErrorMessage { get => _lastErrorMessage; set => SetProperty(ref _lastErrorMessage, value ?? string.Empty); }

        [JsonIgnore]
        public string LastRunDisplay => LastRunUtc == DateTime.MinValue
            ? "-"
            : UserDisplayFormatter.LongDateTime(LastRunUtc.ToLocalTime());

        [JsonIgnore]
        public string LastExitCodeDisplay => LastRunUtc == DateTime.MinValue
            ? "-"
            : UserDisplayFormatter.Number(LastExitCode);
    }
}
