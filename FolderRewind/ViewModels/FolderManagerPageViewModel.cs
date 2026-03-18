using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace FolderRewind.ViewModels
{
    public sealed class FolderManagerPageViewModel : ViewModelBase, IDisposable
    {
        public enum AddFolderResult
        {
            Added,
            DuplicatePath,
            DuplicateDisplayName,
            Invalid
        }

        public sealed class BatchAddFoldersResult
        {
            public bool Success { get; set; } = true;

            public string ErrorMessage { get; set; } = string.Empty;

            public List<ManagedFolder> AddedFolders { get; } = new();

            public List<string> DuplicateDisplayNames { get; } = new();
        }

        public sealed class PluginDiscoverCandidatesResult
        {
            public List<ManagedFolder> ToAdd { get; } = new();

            public List<string> DuplicateDisplayNames { get; } = new();
        }

        private bool _isActive;
        private BackupConfig? _currentConfig;
        private ManagedFolder? _selectedFolder;
        private string _backupComment = string.Empty;
        private string? _pendingFolderPath;
        private bool _mineRewindHintShown;

        public event Action? PendingFolderSelectionRequested;

        public ObservableCollection<BackupConfig> Configs =>
            ConfigService.CurrentConfig?.BackupConfigs ?? new ObservableCollection<BackupConfig>();

        // WinUI + MSIX Trim 下，使用 object 投影视图可规避部分泛型集合绑定异常。
        public ObservableCollection<object> ConfigsView { get; } = new();

        public ObservableCollection<object> CurrentFoldersView { get; } = new();

        public GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public BackupConfig? CurrentConfig
        {
            get => _currentConfig;
            set
            {
                if (ReferenceEquals(_currentConfig, value))
                {
                    return;
                }

                var old = _currentConfig;
                // 切配置前先解绑旧集合监听，避免旧配置变更继续污染当前页面。
                UnhookCurrentFoldersChanged(old);

                _currentConfig = value;
                OnPropertyChanged(nameof(CurrentConfig));

                HookCurrentFoldersChanged(_currentConfig);
                RefreshCurrentFoldersView();

                if (_selectedFolder != null && (_currentConfig == null || !_currentConfig.SourceFolders.Contains(_selectedFolder)))
                {
                    SetSelectedFolder(null, persistSelection: false);
                }

                // 这里只持久化配置选择；文件夹选择由显式用户操作触发。
                PersistManagerSelection(_currentConfig?.Id, null);
            }
        }

        public ManagedFolder? SelectedFolder => _selectedFolder;

        public bool HasSelectedFolder => _selectedFolder != null;

        public string SelectedFolderDisplayName => _selectedFolder?.DisplayName ?? I18n.GetString("FolderManager_NotSelected");

        public string BackupComment
        {
            get => _backupComment;
            set => SetProperty(ref _backupComment, value ?? string.Empty);
        }

        public string? PendingFolderPath => _pendingFolderPath;

        public void Activate()
        {
            if (_isActive)
            {
                return;
            }

            // 页面走缓存时会重复进入，订阅放在激活阶段更安全。
            _isActive = true;
            HookConfigsChanged();
            RefreshConfigsView();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            // 与 Activate 成对解绑，防止重复回调与内存滞留。
            _isActive = false;
            UnhookConfigsChanged();
            UnhookCurrentFoldersChanged(_currentConfig);
        }

        public void Dispose()
        {
            UnhookConfigsChanged();
            UnhookCurrentFoldersChanged(_currentConfig);
        }

        public void EnsureCurrentConfigSelectedFromSettings()
        {
            if (CurrentConfig != null || Configs.Count == 0)
            {
                return;
            }

            // 优先恢复上次选择，找不到再退回首项。
            var lastConfigId = Settings?.LastManagerConfigId;
            CurrentConfig = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(lastConfigId) && c.Id == lastConfigId)
                ?? Configs.FirstOrDefault();

            SetPendingFolderPath(Settings?.LastManagerFolderPath);
        }

        public void SetPendingFolderPath(string? path)
        {
            _pendingFolderPath = string.IsNullOrWhiteSpace(path) ? null : path;
        }

        public void ClearPendingFolderPath()
        {
            _pendingFolderPath = null;
        }

        public BackupConfig? FindConfigById(string? configId)
        {
            if (string.IsNullOrWhiteSpace(configId))
            {
                return null;
            }

            return Configs.FirstOrDefault(c => c.Id == configId);
        }

        public BackupConfig? FindConfigByFolderPath(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            return Configs.FirstOrDefault(c => c.SourceFolders.Any(f =>
                !string.IsNullOrWhiteSpace(f.Path) &&
                string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase)));
        }

        public ManagedFolder? FindFolderInCurrentConfig(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || CurrentConfig?.SourceFolders == null)
            {
                return null;
            }

            return CurrentConfig.SourceFolders.FirstOrDefault(f =>
                !string.IsNullOrWhiteSpace(f.Path) &&
                string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        }

        public void SetSelectedFolder(ManagedFolder? folder, bool persistSelection)
        {
            if (ReferenceEquals(_selectedFolder, folder))
            {
                return;
            }

            _selectedFolder = folder;
            OnPropertyChanged(nameof(SelectedFolder));
            OnPropertyChanged(nameof(HasSelectedFolder));
            OnPropertyChanged(nameof(SelectedFolderDisplayName));

            if (persistSelection && folder != null)
            {
                // 仅在用户明确选中时持久化，程序内部清空选择不覆盖历史路径。
                PersistManagerSelection(CurrentConfig?.Id, folder.Path);
            }
        }

        public void ClearRememberedFolderPathIfMatches(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var settings = Settings;
            if (settings == null)
            {
                return;
            }

            if (!string.Equals(settings.LastManagerFolderPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settings.LastManagerFolderPath = string.Empty;
            ConfigService.Save();
        }

        public AddFolderResult AddFolder(string path, string? name, out ManagedFolder? addedFolder)
        {
            return AddFolderInternal(path, name, null, null, persist: true, out addedFolder);
        }

        public BatchAddFoldersResult AddSubFolders(string rootPath)
        {
            var result = new BatchAddFoldersResult();
            if (CurrentConfig == null || string.IsNullOrWhiteSpace(rootPath))
            {
                result.Success = false;
                result.ErrorMessage = "Invalid context.";
                return result;
            }

            try
            {
                var subDirs = Directory.GetDirectories(rootPath);
                var knownPaths = new HashSet<string>(
                    CurrentConfig.SourceFolders.Select(f => f.Path ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);
                var knownDisplayNames = new HashSet<string>(
                    CurrentConfig.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var dir in subDirs)
                {
                    var addResult = AddFolderInternal(
                        dir,
                        Path.GetFileName(dir),
                        knownPaths,
                        knownDisplayNames,
                        persist: false,
                        out var added);

                    if (addResult == AddFolderResult.Added && added != null)
                    {
                        result.AddedFolders.Add(added);
                    }
                    else if (addResult == AddFolderResult.DuplicateDisplayName)
                    {
                        result.DuplicateDisplayNames.Add(FolderNameConflictService.ResolveDisplayName(null, dir));
                    }
                }

                if (result.AddedFolders.Count > 0)
                {
                    ConfigService.Save();
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public PluginDiscoverCandidatesResult BuildPluginDiscoverCandidates(IEnumerable<ManagedFolder> discovered)
        {
            var result = new PluginDiscoverCandidatesResult();
            if (CurrentConfig == null)
            {
                return result;
            }

            var knownPaths = new HashSet<string>(
                CurrentConfig.SourceFolders.Select(f => f.Path ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var knownDisplayNames = new HashSet<string>(
                CurrentConfig.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var discoveredFolder in discovered.Where(f => f != null && !string.IsNullOrWhiteSpace(f.Path)))
            {
                var candidatePath = discoveredFolder.Path;
                var candidateName = FolderNameConflictService.ResolveDisplayName(discoveredFolder);

                if (knownPaths.Contains(candidatePath))
                {
                    continue;
                }

                if (knownDisplayNames.Contains(candidateName))
                {
                    result.DuplicateDisplayNames.Add(candidateName);
                    continue;
                }

                knownPaths.Add(candidatePath);
                knownDisplayNames.Add(candidateName);
                result.ToAdd.Add(BuildManagedFolderCandidate(candidatePath, candidateName, discoveredFolder));
            }

            return result;
        }

        public void AddDiscoveredFolders(IEnumerable<ManagedFolder> folders)
        {
            if (CurrentConfig == null)
            {
                return;
            }

            var addedAny = false;
            foreach (var folder in folders)
            {
                CurrentConfig.SourceFolders.Add(folder);
                addedAny = true;
            }

            if (addedAny)
            {
                ConfigService.Save();
            }
        }

        public bool RemoveFolder(ManagedFolder folder)
        {
            if (CurrentConfig == null || folder == null)
            {
                return false;
            }

            var removed = CurrentConfig.SourceFolders.Remove(folder);
            if (!removed)
            {
                return false;
            }

            if (ReferenceEquals(_selectedFolder, folder))
            {
                SetSelectedFolder(null, persistSelection: false);
            }

            ConfigService.Save();
            return true;
        }

        public string BuildHotkeyBackupComment()
        {
            var baseComment = BackupComment;
            if (string.IsNullOrWhiteSpace(baseComment))
            {
                return "[快捷键]";
            }

            return baseComment.Contains("[快捷键]", StringComparison.OrdinalIgnoreCase)
                ? baseComment
                : $"{baseComment} [快捷键]";
        }

        public bool TryOpenFolder(ManagedFolder folder)
        {
            if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
            {
                return false;
            }

            try
            {
                Process.Start("explorer.exe", folder.Path);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Open folder failed: {ex.Message}");
                return false;
            }
        }

        public bool TryOpenMiniWindow(ManagedFolder folder)
        {
            if (CurrentConfig == null || folder == null)
            {
                return false;
            }

            MiniWindowService.Open(CurrentConfig, folder);
            return true;
        }

        public void SaveFolderDescription(ManagedFolder folder)
        {
            if (folder == null)
            {
                return;
            }

            ConfigService.Save();
        }

        public bool TryReplaceFolderIcon(ManagedFolder folder, string sourcePath, out string? errorMessage)
        {
            errorMessage = null;
            if (folder == null || string.IsNullOrWhiteSpace(folder.Path) || string.IsNullOrWhiteSpace(sourcePath))
            {
                errorMessage = "Invalid icon source or folder path.";
                return false;
            }

            try
            {
                var destPath = Path.Combine(folder.Path, "icon.png");
                File.Copy(sourcePath, destPath, overwrite: true);

                // 先清空再赋值，确保绑定图片强制刷新。
                folder.CoverImagePath = string.Empty;
                folder.CoverImagePath = destPath;
                ConfigService.Save();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public bool TryMarkNeedMineRewindSuggestion(ManagedFolder? folder)
        {
            if (folder == null)
            {
                return false;
            }

            return TryMarkNeedMineRewindSuggestion(new[] { folder });
        }

        public bool TryMarkNeedMineRewindSuggestion(IEnumerable<ManagedFolder> folders)
        {
            if (_mineRewindHintShown || folders == null)
            {
                return false;
            }

            foreach (var folder in folders)
            {
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    continue;
                }

                if (!IsMinecraftLike(folder.Path, folder.DisplayName))
                {
                    continue;
                }

                _mineRewindHintShown = true;
                return true;
            }

            return false;
        }

        public void ToggleFavorite(ManagedFolder folder)
        {
            if (folder == null)
            {
                return;
            }

            folder.IsFavorite = !folder.IsFavorite;
            ConfigService.Save();
        }

        public void PinFolderToTop(ManagedFolder folder)
        {
            if (CurrentConfig?.SourceFolders == null || folder == null)
            {
                return;
            }

            var index = CurrentConfig.SourceFolders.IndexOf(folder);
            if (index <= 0)
            {
                return;
            }

            CurrentConfig.SourceFolders.Move(index, 0);
            ConfigService.Save();
        }

        public void SaveConfig()
        {
            ConfigService.Save();
        }

        public async Task BackupCurrentConfigAsync()
        {
            if (CurrentConfig == null)
            {
                return;
            }

            await BackupService.BackupConfigAsync(CurrentConfig);
        }

        public async Task BackupSelectedFolderAsync(string? comment)
        {
            if (CurrentConfig == null || _selectedFolder == null)
            {
                return;
            }

            await BackupService.BackupFolderAsync(CurrentConfig, _selectedFolder, comment);
        }

        private AddFolderResult AddFolderInternal(
            string path,
            string? name,
            ISet<string>? knownPaths,
            ISet<string>? knownDisplayNames,
            bool persist,
            out ManagedFolder? addedFolder)
        {
            addedFolder = null;
            if (CurrentConfig == null || string.IsNullOrWhiteSpace(path))
            {
                return AddFolderResult.Invalid;
            }

            knownPaths ??= new HashSet<string>(
                CurrentConfig.SourceFolders.Select(f => f.Path ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            knownDisplayNames ??= new HashSet<string>(
                CurrentConfig.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
                StringComparer.OrdinalIgnoreCase);

            var displayName = FolderNameConflictService.ResolveDisplayName(name, path);

            if (knownPaths.Contains(path))
            {
                return AddFolderResult.DuplicatePath;
            }

            if (knownDisplayNames.Contains(displayName))
            {
                return AddFolderResult.DuplicateDisplayName;
            }

            addedFolder = BuildManagedFolderCandidate(path, displayName);
            CurrentConfig.SourceFolders.Add(addedFolder);

            knownPaths.Add(path);
            knownDisplayNames.Add(displayName);

            if (persist)
            {
                ConfigService.Save();
            }

            return AddFolderResult.Added;
        }

        private ManagedFolder BuildManagedFolderCandidate(string path, string? name = null, ManagedFolder? template = null)
        {
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            var displayName = FolderNameConflictService.ResolveDisplayName(name ?? template?.DisplayName, path);

            var folder = new ManagedFolder
            {
                Path = path,
                DisplayName = displayName,
                Description = template?.Description ?? string.Empty,
                IsFavorite = template?.IsFavorite ?? false,
                CoverImagePath = template?.CoverImagePath ?? string.Empty,
                LastBackupTime = string.IsNullOrWhiteSpace(template?.LastBackupTime)
                    ? resourceLoader.GetString("FolderManager_NeverBackedUp")
                    : template!.LastBackupTime
            };

            if (string.IsNullOrWhiteSpace(folder.CoverImagePath))
            {
                var potentialIcon = Path.Combine(path, "icon.png");
                if (File.Exists(potentialIcon))
                {
                    folder.CoverImagePath = potentialIcon;
                }
            }

            return folder;
        }

        private void HookConfigsChanged()
        {
            try
            {
                if (ConfigService.CurrentConfig?.BackupConfigs != null)
                {
                    ConfigService.CurrentConfig.BackupConfigs.CollectionChanged -= OnConfigsChanged;
                    ConfigService.CurrentConfig.BackupConfigs.CollectionChanged += OnConfigsChanged;
                }
            }
            catch
            {
            }
        }

        private void UnhookConfigsChanged()
        {
            try
            {
                if (ConfigService.CurrentConfig?.BackupConfigs != null)
                {
                    ConfigService.CurrentConfig.BackupConfigs.CollectionChanged -= OnConfigsChanged;
                }
            }
            catch
            {
            }
        }

        private void OnConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            EnqueueOnUiThread(() =>
            {
                RefreshConfigsView();

                if (CurrentConfig != null && !Configs.Contains(CurrentConfig))
                {
                    CurrentConfig = Configs.FirstOrDefault();
                }

                if (CurrentConfig == null && Configs.Count > 0)
                {
                    var lastConfigId = Settings?.LastManagerConfigId;
                    CurrentConfig = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(lastConfigId) && c.Id == lastConfigId)
                        ?? Configs.FirstOrDefault();
                }

                if (CurrentConfig != null && !string.IsNullOrWhiteSpace(Settings?.LastManagerFolderPath))
                {
                    SetPendingFolderPath(Settings.LastManagerFolderPath);
                    // 触发一次“延迟选中”，由 View 在列表就绪后完成真正定位。
                    PendingFolderSelectionRequested?.Invoke();
                }
            });
        }

        private void HookCurrentFoldersChanged(BackupConfig? config)
        {
            if (config?.SourceFolders == null)
            {
                return;
            }

            try
            {
                config.SourceFolders.CollectionChanged -= OnCurrentFoldersChanged;
                config.SourceFolders.CollectionChanged += OnCurrentFoldersChanged;
            }
            catch
            {
            }
        }

        private void UnhookCurrentFoldersChanged(BackupConfig? config)
        {
            if (config?.SourceFolders == null)
            {
                return;
            }

            try
            {
                config.SourceFolders.CollectionChanged -= OnCurrentFoldersChanged;
            }
            catch
            {
            }
        }

        private void OnCurrentFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            EnqueueOnUiThread(() =>
            {
                RefreshCurrentFoldersView();

                // 当前选中文件夹被删/移出后，立刻清空选择避免悬挂引用。
                if (_selectedFolder != null && (CurrentConfig == null || !CurrentConfig.SourceFolders.Contains(_selectedFolder)))
                {
                    SetSelectedFolder(null, persistSelection: false);
                }
            });
        }

        public void RefreshConfigsView()
        {
            ConfigsView.Clear();
            foreach (var cfg in Configs)
            {
                ConfigsView.Add(cfg);
            }
        }

        public void RefreshCurrentFoldersView()
        {
            CurrentFoldersView.Clear();
            if (CurrentConfig?.SourceFolders == null)
            {
                return;
            }

            foreach (var folder in CurrentConfig.SourceFolders)
            {
                CurrentFoldersView.Add(folder);
            }
        }

        private void PersistManagerSelection(string? configId, string? folderPath)
        {
            var settings = Settings;
            if (settings == null)
            {
                return;
            }

            var updated = false;

            if (!string.IsNullOrWhiteSpace(configId) && settings.LastManagerConfigId != configId)
            {
                settings.LastManagerConfigId = configId;
                updated = true;
            }

            if (!string.IsNullOrWhiteSpace(folderPath) && settings.LastManagerFolderPath != folderPath)
            {
                settings.LastManagerFolderPath = folderPath;
                updated = true;
            }

            if (updated)
            {
                // 只在值变化时落盘，避免列表选择抖动导致高频写配置。
                ConfigService.Save();
            }
        }

        private static bool IsMinecraftLike(string path, string? displayName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var folderName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(path)
                : displayName;

            var isMinecraftRoot = string.Equals(folderName, ".minecraft", StringComparison.OrdinalIgnoreCase);
            var hasLevelDat = File.Exists(Path.Combine(path, "level.dat"));
            return isMinecraftRoot || hasLevelDat;
        }
    }
}
