using FolderRewind.Models;
using System;

namespace FolderRewind.Services.Plugins
{
    /// <summary>
    /// 插件商店列表项（用于 XAML x:DataType）。
    /// </summary>
    public class PluginStoreAssetItem : ObservableObject
    {
        private bool _isBusy;
        private string _status = string.Empty;
        private bool _isInstalled;
        private bool _canEnableNow;
        private bool _requiresRestart;

        public string Name { get; set; } = string.Empty;
        public string PluginId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public int PluginApiMajor { get; set; }
        public int PluginApiMinor { get; set; }
        public string Channel { get; set; } = string.Empty;
        public string TrustClassification { get; set; } = string.Empty;
        public string[] Architectures { get; set; } = Array.Empty<string>();
        public long SizeBytes { get; set; }
        public long DownloadCount { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? ReleaseTag { get; set; }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanInstall));
                }
            }
        }

        public bool CanInstall => !IsBusy;

        public bool IsInstalled
        {
            get => _isInstalled;
            set => SetProperty(ref _isInstalled, value);
        }

        public bool CanEnableNow
        {
            get => _canEnableNow;
            set => SetProperty(ref _canEnableNow, value);
        }

        public bool RequiresRestart
        {
            get => _requiresRestart;
            set => SetProperty(ref _requiresRestart, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public string SizeDisplay => SizeBytes <= 0
            ? string.Empty
            : $"{UserDisplayFormatter.Number(SizeBytes / 1024.0 / 1024.0, 2)} MB";

        public string DownloadCountDisplay => DownloadCount <= 0 ? "-" : UserDisplayFormatter.Number(DownloadCount);

        public string UpdatedDisplay => UpdatedAt.HasValue
            ? UserDisplayFormatter.Date(UpdatedAt.Value.LocalDateTime)
            : "-";

        public string FileType => "FRPLUGIN";
    }
}
