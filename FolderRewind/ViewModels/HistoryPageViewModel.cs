using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    public sealed class HistoryPageViewModel : ViewModelBase
    {
        private readonly List<HistoryItem> _currentAllItems = new();
        private bool _historyEventsSubscribed;
        private int _missingCount;
        private bool _isEmpty = true;
        private string _commentFilterText = string.Empty;

        private BackupConfig? _currentConfig;
        private ManagedFolder? _currentFolder;

        public ObservableCollection<HistoryItem> FilteredHistory { get; } = new();

        public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig?.BackupConfigs ?? new ObservableCollection<BackupConfig>();

        private GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public bool IsEmpty
        {
            get => _isEmpty;
            private set => SetProperty(ref _isEmpty, value);
        }

        public bool HasMissing => _missingCount > 0;

        public bool CanUseCloudHistoryActions => CloudSyncService.CanUseHistoryCloudActions(_currentConfig);

        public string CommentFilterText
        {
            get => _commentFilterText;
            set
            {
                if (!SetProperty(ref _commentFilterText, value ?? string.Empty))
                {
                    return;
                }

                ApplyCommentFilter();
            }
        }

        public bool UseHistoryStatusColors
        {
            get => Settings?.UseHistoryStatusColors ?? true;
            set
            {
                if (Settings != null)
                {
                    Settings.UseHistoryStatusColors = value;
                    ConfigService.Save();
                }

                UpdateTimelineVisuals(FilteredHistory);
                OnPropertyChanged();
            }
        }

        public void Initialize()
        {
            HistoryService.Initialize();

            if (_historyEventsSubscribed)
            {
                return;
            }

            HistoryService.HistoryChanged += OnHistoryChanged;
            _historyEventsSubscribed = true;
        }

        public void SetCurrentSelection(BackupConfig? config, ManagedFolder? folder, bool refreshHistoryIfFolder, bool persistSelection)
        {
            _currentConfig = config;
            _currentFolder = folder;
            OnPropertyChanged(nameof(CanUseCloudHistoryActions));

            // 页面初始化阶段可关闭刷新，避免控件尚未就绪时重复拉取历史。
            if (refreshHistoryIfFolder && _currentConfig != null && _currentFolder != null)
            {
                RefreshHistory(_currentConfig, _currentFolder);
            }

            if (persistSelection)
            {
                PersistHistorySelection(_currentConfig, _currentFolder);
            }
        }

        public bool TryGetCurrentSelection(out BackupConfig? config, out ManagedFolder? folder)
        {
            config = _currentConfig;
            folder = _currentFolder;
            return config != null && folder != null;
        }

        public bool TryResolveSelection(string? configId, string? folderPath, out BackupConfig? config, out ManagedFolder? folder)
        {
            config = null;
            folder = null;

            if (!string.IsNullOrWhiteSpace(configId))
            {
                config = Configs.FirstOrDefault(c => c.Id == configId);
            }

            if (config == null && !string.IsNullOrWhiteSpace(folderPath))
            {
                config = Configs.FirstOrDefault(c => c.SourceFolders.Any(f => f.Path == folderPath));
            }

            if (config == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                folder = config.SourceFolders.FirstOrDefault(f => f.Path == folderPath);
            }

            return true;
        }

        public bool TryResolveLastSelection(out BackupConfig? config, out ManagedFolder? folder)
        {
            config = null;
            folder = null;

            var settings = Settings;
            if (settings == null || Configs.Count == 0)
            {
                return false;
            }

            config = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(settings.LastHistoryConfigId) && c.Id == settings.LastHistoryConfigId)
                     ?? Configs.FirstOrDefault();

            if (config == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(settings.LastHistoryFolderPath))
            {
                folder = config.SourceFolders.FirstOrDefault(f => f.Path == settings.LastHistoryFolderPath);
            }

            if (folder == null && config.SourceFolders.Count > 0)
            {
                // 历史路径失效时兜底到首项，保证页面总有可展示目标。
                folder = config.SourceFolders[0];
            }

            return true;
        }

        public void RefreshCurrentHistory()
        {
            if (_currentConfig == null || _currentFolder == null)
            {
                return;
            }

            RefreshHistory(_currentConfig, _currentFolder);
        }

        public void RefreshHistory(BackupConfig config, ManagedFolder folder)
        {
            _currentAllItems.Clear();
            FilteredHistory.Clear();

            var items = HistoryService.GetHistoryForFolder(config, folder);
            foreach (var item in items)
            {
                _currentAllItems.Add(item);
            }

            ApplyCommentFilter();
        }

        public int GetMissingCount()
        {
            return _currentAllItems.Count(i => i.IsMissing);
        }

        public void ClearMissingEntries()
        {
            if (_currentConfig == null || _currentFolder == null)
            {
                return;
            }

            try
            {
                HistoryService.RemoveMissingEntries(_currentConfig, _currentFolder);
            }
            catch
            {
            }
        }

        public int ScanAndRecoverHistory(string scanPath)
        {
            if (_currentConfig == null || _currentFolder == null || string.IsNullOrWhiteSpace(scanPath))
            {
                return 0;
            }

            return HistoryService.ScanAndRecoverHistory(scanPath, _currentConfig, _currentFolder);
        }

        public string? GetBackupFilePath(HistoryItem item)
        {
            if (_currentConfig == null || _currentFolder == null)
            {
                return null;
            }

            return HistoryService.GetBackupFilePath(_currentConfig, _currentFolder, item);
        }

        public bool TryRevealBackupFile(HistoryItem item, out string? errorMessage)
        {
            errorMessage = null;

            var filePath = GetBackupFilePath(item);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                errorMessage = I18n.GetString("History_ViewFile_PathEmpty");
                return false;
            }

            if (!File.Exists(filePath))
            {
                errorMessage = I18n.Format("History_ViewFile_NotFound", Path.GetFileName(filePath));
                return false;
            }

            if (!ShellPathService.TryRevealPathInExplorer(filePath, out var revealError))
            {
                errorMessage = I18n.Format("History_ViewFile_Failed", revealError ?? string.Empty);
                return false;
            }

            return true;
        }

        public void UpdateComment(HistoryItem item, string newComment)
        {
            HistoryService.UpdateComment(item, newComment);
            item.OnPropertyChanged(nameof(item.Message));
        }

        public void ToggleImportant(HistoryItem item)
        {
            HistoryService.ToggleImportant(item);
            UpdateTimelineVisuals(FilteredHistory);
        }

        public async Task<bool> UploadToCloudAsync(HistoryItem item)
        {
            if (_currentConfig == null || _currentFolder == null || item == null)
            {
                return false;
            }

            bool success = await CloudSyncService.UploadHistoryItemAsync(_currentConfig, _currentFolder, item).ConfigureAwait(true);
            RefreshCurrentHistory();
            return success;
        }

        public async Task<bool> DownloadFromCloudAsync(HistoryItem item)
        {
            if (_currentConfig == null || _currentFolder == null || item == null)
            {
                return false;
            }

            bool success = await CloudSyncService.DownloadHistoryItemAsync(_currentConfig, _currentFolder, item).ConfigureAwait(true);
            RefreshCurrentHistory();
            return success;
        }

        public async Task<BackupService.DeleteBackupResult> DeleteHistoryItemAsync(HistoryItem item, BackupDeleteMode deleteMode)
        {
            if (_currentConfig == null || _currentFolder == null || item == null)
            {
                return new BackupService.DeleteBackupResult
                {
                    Success = false,
                    Message = I18n.GetString("History_Delete_InvalidRequest")
                };
            }

            var result = await BackupService.DeleteBackupAsync(_currentConfig, _currentFolder, item, deleteMode).ConfigureAwait(true);
            RefreshCurrentHistory();
            return result;
        }

        private void OnHistoryChanged()
        {
            _ = UiDispatcherService.RunOnUiAsync(() =>
            {
                RefreshCurrentHistory();
            });
        }

        private void ApplyCommentFilter()
        {
            UpdateCloudPresentation(_currentAllItems);
            FilteredHistory.Clear();

            IEnumerable<HistoryItem> query = _currentAllItems;
            var needle = (CommentFilterText ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(needle))
            {
                query = query.Where(i => (i.Comment ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            foreach (var item in query)
            {
                FilteredHistory.Add(item);
            }

            // 缺失统计基于完整历史，不受当前筛选词影响。
            _missingCount = _currentAllItems.Count(i => i.IsMissing);
            OnPropertyChanged(nameof(HasMissing));

            IsEmpty = FilteredHistory.Count == 0;
            UpdateTimelineVisuals(FilteredHistory);
            OnPropertyChanged(nameof(CanUseCloudHistoryActions));
        }

        private static Brush TryGetThemeBrush(string key, Windows.UI.Color fallback)
        {
            try
            {
                if (Application.Current?.Resources != null && Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b)
                {
                    return b;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }

        private void UpdateTimelineVisuals(IEnumerable<HistoryItem> items)
        {
            var use = UseHistoryStatusColors;

            var offLine = TryGetThemeBrush("SystemControlForegroundBaseLowBrush", Colors.Gray);
            var offFill = TryGetThemeBrush("SystemControlBackgroundChromeMediumBrush", Colors.Transparent);
            var offBorder = TryGetThemeBrush("SystemControlForegroundBaseHighBrush", Colors.Gray);

            var ok = new SolidColorBrush(Colors.DodgerBlue);
            var cloudOnly = new SolidColorBrush(Colors.LightSkyBlue);
            var bad = new SolidColorBrush(Colors.OrangeRed);
            var warn = new SolidColorBrush(Colors.Gold);
            var importantFill = new SolidColorBrush(Colors.Gold);

            foreach (var item in items)
            {
                if (!use)
                {
                    item.TimelineLineBrush = offLine;
                    item.TimelineNodeFillBrush = offFill;
                    item.TimelineNodeBorderBrush = offBorder;
                    continue;
                }

                // 状态优先级：缺失 > 文件过小 > 正常，保持和旧版一致，避免视觉语义变化。
                if (item.IsMissing)
                {
                    item.TimelineLineBrush = bad;
                    item.TimelineNodeBorderBrush = bad;
                }
                else if (item.IsCloudOnly)
                {
                    item.TimelineLineBrush = cloudOnly;
                    item.TimelineNodeBorderBrush = cloudOnly;
                }
                else if (item.IsSmallFile)
                {
                    item.TimelineLineBrush = warn;
                    item.TimelineNodeBorderBrush = warn;
                }
                else
                {
                    item.TimelineLineBrush = ok;
                    item.TimelineNodeBorderBrush = ok;
                }

                // 重要标记只影响节点填充，不改变线条颜色，便于一眼看出“时间状态 + 收藏状态”。
                item.TimelineNodeFillBrush = item.IsImportant ? importantFill : offFill;
            }
        }

        private void PersistHistorySelection(BackupConfig? config, ManagedFolder? folder)
        {
            var settings = Settings;
            if (settings == null)
            {
                return;
            }

            var updated = false;

            if (config != null && settings.LastHistoryConfigId != config.Id)
            {
                settings.LastHistoryConfigId = config.Id;
                updated = true;
            }

            if (folder != null && settings.LastHistoryFolderPath != folder.Path)
            {
                settings.LastHistoryFolderPath = folder.Path;
                updated = true;
            }

            if (updated)
            {
                // 仅在值变化时写盘，避免切换列表时产生多余 I/O。
                ConfigService.Save();
            }
        }

        private void UpdateCloudPresentation(IEnumerable<HistoryItem> items)
        {
            string availabilityHint = GetCloudAvailabilityHint();
            bool cloudActionsEnabled = CanUseCloudHistoryActions;

            foreach (var item in items)
            {
                item.CloudStatusText = item.HasCloudCopy
                    ? item.IsCloudOnly
                        ? I18n.GetString("History_CloudStatus_CloudOnly")
                        : I18n.GetString("History_CloudStatus_CloudAvailable")
                    : string.Empty;

                item.CanUploadToCloud = cloudActionsEnabled && item.HasLocalFile;
                item.CanDownloadFromCloud = cloudActionsEnabled && item.HasCloudCopy;

                item.CloudActionHintText = item.CanUploadToCloud
                    ? I18n.GetString("History_CloudUpload_Action")
                    : !string.IsNullOrWhiteSpace(availabilityHint)
                        ? availabilityHint
                        : I18n.GetString("History_CloudUpload_NoLocalFile");

                item.DownloadFromCloudHintText = item.CanDownloadFromCloud
                    ? I18n.GetString("History_CloudDownload_Action")
                    : !string.IsNullOrWhiteSpace(availabilityHint)
                        ? availabilityHint
                        : I18n.GetString("History_CloudDownload_NoCloudCopy");
            }
        }

        private string GetCloudAvailabilityHint()
        {
            if (_currentConfig?.Cloud?.Enabled != true)
            {
                return I18n.GetString("History_CloudAction_NotEnabled");
            }

            if (_currentConfig.Cloud.CommandMode != CloudCommandMode.Rclone)
            {
                return I18n.GetString("History_CloudAction_CustomModeOnly");
            }

            return string.Empty;
        }
    }
}
