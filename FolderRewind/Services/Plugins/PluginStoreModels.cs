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

        public string Name { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
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

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public string SizeDisplay => SizeBytes <= 0 ? string.Empty : $"{SizeBytes / 1024.0 / 1024.0:F2} MB";

        public string DownloadCountDisplay => DownloadCount <= 0 ? "-" : DownloadCount.ToString("N0");

        public string UpdatedDisplay => UpdatedAt.HasValue ? UpdatedAt.Value.LocalDateTime.ToString("yyyy-MM-dd") : "-";

        public string FileType => Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "ZIP" : "OTHER";
    }
}
