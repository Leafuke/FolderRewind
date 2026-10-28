using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.ViewModels
{
    public sealed partial class FolderManagerPageViewModel : ViewModelBase, IDisposable
    {
        public enum AddFolderResult
        {
            Added,
            DuplicatePath,
            DuplicateDisplayName,
            UnsafePathOverlap,
            Invalid
        }

        public sealed class BatchAddFoldersResult
        {
            public bool Success { get; set; } = true;

            public string ErrorMessage { get; set; } = string.Empty;

            public List<ManagedFolder> AddedFolders { get; } = new();

            public List<string> DuplicateDisplayNames { get; } = new();

            public List<string> UnsafePaths { get; } = new();
        }

        public sealed class PluginDiscoverCandidatesResult
        {
            public List<ManagedFolder> ToAdd { get; } = new();

            public List<string> DuplicateDisplayNames { get; } = new();

            public List<string> UnsafePaths { get; } = new();
        }

        private bool _isActive;
        private bool _isDisposed;
        private BackupConfig? _subscribedConfig;
        private readonly CollectionChangedSubscription _configSubscription = new();
        private readonly CollectionChangedSubscription _folderSubscription = new();
        private BackupConfig? _currentConfig;
        private readonly FolderManagerContext<BackupConfig, ManagedFolder> _context = new(c => c.SourceFolders, f => f.Id, f => f.Path);
        private ManagedFolder? _selectedFolder => _context.SelectedFolder;
        private bool _refreshingConfigs;
        private bool _refreshingFolders;
        private string _backupComment = string.Empty;
        private bool _mineRewindHintShown;
        // Prevents N redundant RefreshCurrentFoldersView enqueues when
        // SourceFolders.Add is called in a tight loop (e.g. by AddDiscoveredFolders,
        // AddSubFolders). Only the first event enqueues; subsequent events in the
        // same batch skip until the pending refresh runs.
        private bool _currentFoldersRefreshPending;

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
                if (_refreshingConfigs || ReferenceEquals(_currentConfig, value))
                {
                    return;
                }

                _commands.Cancel();
                // 切配置前先解绑旧集合监听，避免旧配置变更继续污染当前页面。
                UnhookCurrentFoldersChanged();

                _currentConfig = value;
                _context.SwitchProject(value);
                BackupComment = string.Empty;

                OnPropertyChanged(nameof(CurrentConfig));
                OnPropertyChanged(nameof(HasCurrentConfig));
                OnPropertyChanged(nameof(CurrentConfigDisplayName));
                NotifyActionState();

                HookCurrentFoldersChanged(_currentConfig);
                RefreshCurrentFoldersView();

                // 这里只持久化配置选择；文件夹选择由显式用户操作触发。
                PersistManagerSelection(_currentConfig?.Id, null);
            }
        }

        public ManagedFolder? SelectedFolder => _selectedFolder;

        public bool HasSelectedFolder => _selectedFolder != null;
        public bool HasCurrentConfig => _currentConfig != null;
        public string CurrentConfigDisplayName => _currentConfig?.Name ?? string.Empty;

        public string BackupComment
        {
            get => _backupComment;
            set => SetProperty(ref _backupComment, value ?? string.Empty);
        }

        public string SelectedFolderDisplayName => _selectedFolder?.DisplayName ?? I18n.GetString("FolderManager_NotSelected");

        public ManagedFolder? SelectedFolderItem
        {
            get => _selectedFolder;
            set
            {
                if (!_refreshingFolders && !ReferenceEquals(value, _selectedFolder))
                    SetSelectedFolder(value, persistSelection: true);
            }
        }

        public bool HasFolders => CurrentFoldersView.Count > 0;
        public bool IsEmpty => !HasFolders;

        public string? PendingFolderPath => _context.Pending?.Path;

        public void Activate()
        {
            if (_isActive || _isDisposed)
            {
                return;
            }

            // 页面走缓存时会重复进入，订阅放在激活阶段更安全。
            _isActive = true;


            _commands.Activate();
            ConfigService.Saved += OnConfigurationSaved;
            HookCurrentFoldersChanged(_currentConfig);
            HookConfigsChanged();
            RefreshConfigsView();
            if (_currentConfig is not null && !Configs.Contains(_currentConfig))
                CurrentConfig = FindConfigById(_currentConfig.Id) ?? Configs.FirstOrDefault();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            // 与 Activate 成对解绑，防止重复回调与内存滞留。
            _isActive = false;
            _currentFoldersRefreshPending = false;
            _context.InvalidatePending();

            ConfigService.Saved -= OnConfigurationSaved;
            _commands.Deactivate();
            UnhookConfigsChanged();
            UnhookCurrentFoldersChanged();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            Deactivate();
            _isDisposed = true;
            _commands.Dispose();
            UnhookConfigsChanged();
            UnhookCurrentFoldersChanged();
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
            _context.RequestSelection(path);
        }

        public void ClearPendingFolderPath()
        {
            _context.InvalidatePending();
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
            _context.Select(folder);
            NotifySelectionChanged();

            if (persistSelection && _selectedFolder != null)
                PersistManagerSelection(CurrentConfig?.Id, _selectedFolder.Path);
        }

        private void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedFolder));
            OnPropertyChanged(nameof(HasSelectedFolder));
            OnPropertyChanged(nameof(SelectedFolderDisplayName));
            OnPropertyChanged(nameof(SelectedFolderItem));
            NotifyActionState();
        }

        internal FolderManagerContext<BackupConfig, ManagedFolder>.PendingSelection? PendingSelection => _context.Pending;

        internal bool ApplyPendingSelection(FolderManagerContext<BackupConfig, ManagedFolder>.PendingSelection request)
        {
            if (!_isActive || !_context.ApplySelection(request)) return false;
            NotifySelectionChanged();
            if (_selectedFolder is not null) PersistManagerSelection(CurrentConfig?.Id, _selectedFolder.Path);
            else ClearRememberedFolderPathIfMatches(request.Path);
            return true;
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
            TaskObserver.Observe(TaskObserver.SaveConfigAsync(), nameof(FolderManagerPageViewModel));
        }

        public AddFolderResult AddFolder(string path, string? name, out ManagedFolder? addedFolder)
        {
            return AddFolderInternal(path, name, null, null, out addedFolder);
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

                if (!IsSourceStorageSafe(candidatePath, candidateName))
                {
                    result.UnsafePaths.Add(candidatePath);
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
                // Persisted by the owning asynchronous command.
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

            // Persisted by the owning asynchronous command.
            return true;
        }

        public string BuildHotkeyBackupComment()
        {
            var comment = BackupComment.Trim();
            var marker = I18n.GetString("Hotkeys_BackupCommentMarker");
            return string.IsNullOrWhiteSpace(comment) ? marker
                : comment.Contains(marker, StringComparison.OrdinalIgnoreCase) ? comment : $"{comment} {marker}";
        }

        public bool TryOpenFolder(ManagedFolder folder)
        {
            if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
            {
                return false;
            }

            return ShellPathService.TryOpenPath(folder.Path, out _);
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
            // Persisted by the owning asynchronous command.
        }



        public async Task BackupCurrentConfigAsync(BackupInvocationOptions? invocationOptions = null)
        {
            if (CurrentConfig == null)
            {
                return;
            }

            await BackupService.BackupConfigAsync(
                CurrentConfig,
                invocationOptions ?? BackupInvocationOptions.ForManual(BackupComment.Trim()));
        }

        public async Task BackupSelectedFolderAsync(BackupInvocationOptions? invocationOptions = null)
        {
            if (CurrentConfig == null || _selectedFolder == null)
            {
                return;
            }

            await BackupService.BackupFolderAsync(
                CurrentConfig,
                _selectedFolder,
                invocationOptions ?? BackupInvocationOptions.ForManual(BackupComment.Trim()));
        }

        private AddFolderResult AddFolderInternal(
            string path,
            string? name,
            ISet<string>? knownPaths,
            ISet<string>? knownDisplayNames,
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

            if (!IsSourceStorageSafe(path, displayName))
            {
                return AddFolderResult.UnsafePathOverlap;
            }

            addedFolder = BuildManagedFolderCandidate(path, displayName);
            CurrentConfig.SourceFolders.Add(addedFolder);

            knownPaths.Add(path);
            knownDisplayNames.Add(displayName);


            return AddFolderResult.Added;
        }

        private bool IsSourceStorageSafe(string path, string displayName)
        {
            if (CurrentConfig == null
                || !BackupStoragePathService.TryResolveBackupStoragePaths(
                    CurrentConfig.DestinationPath,
                    displayName,
                    path,
                    out _,
                    out var backupSubDir,
                    out var metadataDir))
            {
                return false;
            }

            return BackupPathOverlapPolicy.Validate(path, backupSubDir, metadataDir).IsSafe;
        }

        private ManagedFolder BuildManagedFolderCandidate(string path, string? name = null, ManagedFolder? template = null)
        {
            var resourceLoader = ResourceLoader.GetForViewIndependentUse();
            var displayName = FolderNameConflictService.ResolveDisplayName(name ?? template?.DisplayName, path);

            var folder = template is null ? new ManagedFolder()
                : JsonCloneService.Clone(template, AppJsonContext.Default.ManagedFolder, I18n.GetString("Common_Failed"));
            folder.Id = Guid.NewGuid().ToString("N");
            folder.Path = path;
            folder.DisplayName = displayName;
            folder.LastBackupTime = resourceLoader.GetString("FolderManager_NeverBackedUp");

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

        private bool HookConfigsChanged() =>
            _configSubscription.SetSource(_isActive ? ConfigService.CurrentConfig.BackupConfigs : null, OnConfigsChanged);

        private void UnhookConfigsChanged() => _configSubscription.Dispose();

        private void OnConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            EnqueueOnUiThread(() =>
            {
                if (!_isActive) return;
                RefreshConfigsView();

                if (CurrentConfig != null && !Configs.Contains(CurrentConfig))
                {
                    CurrentConfig = FindConfigById(CurrentConfig.Id) ?? Configs.FirstOrDefault();
                }

                if (CurrentConfig == null && Configs.Count > 0)
                {
                    var lastConfigId = Settings?.LastManagerConfigId;
                    CurrentConfig = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(lastConfigId) && c.Id == lastConfigId)
                        ?? Configs.FirstOrDefault();
                }

                if (CurrentConfig != null && _selectedFolder is null && Settings?.LastManagerConfigId == CurrentConfig.Id && !string.IsNullOrWhiteSpace(Settings?.LastManagerFolderPath))
                {
                    SetPendingFolderPath(Settings.LastManagerFolderPath);
                    // 触发一次“延迟选中”，由 View 在列表就绪后完成真正定位。
                    PendingFolderSelectionRequested?.Invoke();
                }
            });
        }

        private void HookCurrentFoldersChanged(BackupConfig? config)
        {
            var activeConfig = _isActive ? config : null;
            if (!ReferenceEquals(_subscribedConfig, activeConfig))
            {
                if (_subscribedConfig is not null) _subscribedConfig.PropertyChanged -= OnCurrentConfigPropertyChanged;
                _subscribedConfig = activeConfig;
                if (_subscribedConfig is not null) _subscribedConfig.PropertyChanged += OnCurrentConfigPropertyChanged;
            }
            _folderSubscription.SetSource(activeConfig?.SourceFolders, OnCurrentFoldersChanged);
        }

        private void UnhookCurrentFoldersChanged()
        {
            if (_subscribedConfig is not null) _subscribedConfig.PropertyChanged -= OnCurrentConfigPropertyChanged;
            _subscribedConfig = null;
            _folderSubscription.Dispose();
        }

        private void OnCurrentConfigPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(BackupConfig.Name)) OnPropertyChanged(nameof(CurrentConfigDisplayName));
            if (args.PropertyName != nameof(BackupConfig.SourceFolders)) return;
            HookCurrentFoldersChanged(_currentConfig);
            OnCurrentFoldersChanged(sender, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void OnConfigurationSaved()
        {
            if (!_isActive) return;
            if (HookConfigsChanged())
                OnConfigsChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void OnCurrentFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // When folder additions happen in a tight loop (AddDiscoveredFolders,
            // AddSubFolders), each SourceFolders.Add fires CollectionChanged and
            // would enqueue a separate full RefreshCurrentFoldersView. Skip
            // redundant enqueues — only the first one is honoured, and the single
            // pending rebuild will capture all accumulated changes.
            if (_currentFoldersRefreshPending)
            {
                return;
            }

            _currentFoldersRefreshPending = true;
            EnqueueOnUiThread(() =>
            {
                _currentFoldersRefreshPending = false;
                if (!_isActive) return;
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
            _refreshingConfigs = true;
            try
            {
                SynchronizeView(ConfigsView, Configs.Cast<object>().ToArray());
            }
            finally { _refreshingConfigs = false; }
            OnPropertyChanged(nameof(CurrentConfig));
        }

        public void RefreshCurrentFoldersView()
        {
            _refreshingFolders = true;
            try
            {
                var desired = CurrentConfig?.SourceFolders.Cast<object>().ToArray() ?? Array.Empty<object>();
                SynchronizeView(CurrentFoldersView, desired);
                _context.RefreshSelection();
            }
            finally { _refreshingFolders = false; }
            NotifySelectionChanged();
            OnPropertyChanged(nameof(HasFolders));
            OnPropertyChanged(nameof(IsEmpty));
        }

        private static void SynchronizeView(ObservableCollection<object> view, object[] desired)
        {
            for (var i = 0; i < desired.Length; i++)
            {
                if (i < view.Count && ReferenceEquals(view[i], desired[i])) continue;
                var previous = view.IndexOf(desired[i]);
                if (previous >= 0) view.Move(previous, i);
                else view.Insert(i, desired[i]);
            }
            while (view.Count > desired.Length) view.RemoveAt(view.Count - 1);
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
                TaskObserver.Observe(TaskObserver.SaveConfigAsync(), nameof(FolderManagerPageViewModel));
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
