using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels
{
    public sealed partial class HomePageViewModel : ViewModelBase, IDisposable
    {
        private bool _isActive;
        private bool _refreshScheduled;
        private int _sortGeneration;
        private bool _isFavoritesEmpty = true;
        private ObservableCollection<BackupConfig>? _subscribedConfigs;

        // 业务层仍保留强类型集合，方便后续查找所属配置等逻辑。
        public BatchObservableCollection<ManagedFolder> FavoriteFolders { get; } = new();

        // WinUI + MSIX Trim 下，x:Bind 对 object 视图更稳定，这里保留投影视图。
        public BatchObservableCollection<object> FavoriteFoldersView { get; } = new();

        public BatchObservableCollection<object> ConfigsView { get; } = new();

        public ObservableCollection<BackupConfig>? Configs => ConfigService.CurrentConfig?.BackupConfigs;

        public GlobalSettings? Settings => ConfigService.CurrentConfig?.GlobalSettings;

        public bool IsFavoritesEmpty
        {
            get => _isFavoritesEmpty;
            private set => SetProperty(ref _isFavoritesEmpty, value);
        }

        public string CurrentSortMode => Settings?.HomeSortMode ?? "NameAsc";
        public bool HasProjects => ConfigsView.Count != 0;
        public bool HasFavorites => FavoriteFoldersView.Count != 0;

        public HomePageViewModel()
            : this(new HomeInteractionService(MainWindowService.GetXamlRoot))
        {
        }

        internal HomePageViewModel(IHomeInteractionService interactions)
        {
            _interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
            InitializeCommands();
            // 收藏源集合变化时统一刷新投影视图，避免页面手动维护两份数据。

        }

        public void Activate()
        {
            if (_isActive)
            {
                return;
            }

            // 页面通常被导航缓存，进入页面时再挂订阅比构造函数更稳妥。
            _isActive = true;
            if (_pageLifetime.IsCancellationRequested)
            {
                _pageLifetime.Dispose();
                _pageLifetime = new System.Threading.CancellationTokenSource();
            }
            HookConfigsChanged();
            RefreshFavorites();
            RefreshConfigsView();
            ConfigService.Saved += OnConfigurationSaved;

        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            // 对称解绑，避免下次激活后收到重复 CollectionChanged。
            _isActive = false;
            ++_sortGeneration;
            _pageLifetime.Cancel();
            UnhookConfigsChanged();
            ConfigService.Saved -= OnConfigurationSaved;
        }

        public void Dispose()
        {
            Deactivate();
        }

        public void RefreshFavorites()
        {
            var folders = Configs?.SelectMany(config => config.SourceFolders).Where(folder => folder.IsFavorite).ToArray() ?? [];
            FavoriteFolders.Synchronize(folders);
            FavoriteFoldersView.Synchronize(folders.Cast<object>());
            IsFavoritesEmpty = folders.Length == 0;
            OnPropertyChanged(nameof(HasFavorites));
        }

        public void RefreshConfigsView() => TaskObserver.Observe(RefreshConfigsAsync(), nameof(HomePageViewModel));

        private async Task RefreshConfigsAsync()
        {
            var generation = ++_sortGeneration;
            var mode = CurrentSortMode;
            var configs = Configs?.ToArray() ?? [];
            BackupConfig[] sorted;
            if (mode == "LastModifiedDesc")
            {
                var snapshots = configs.Select(config => (Config: config, Name: config.Name,
                    Paths: config.SourceFolders.Select(folder => folder.Path).ToArray())).ToArray();
                sorted = await Task.Run(() => snapshots.Select(item => (item.Config, item.Name,
                    Modified: item.Paths.Select(ReadModified).DefaultIfEmpty(DateTime.MinValue).Max()))
                    .OrderByDescending(item => item.Modified).ThenBy(item => item.Name).Select(item => item.Config).ToArray());
            }
            else sorted = GetSortedConfigs().ToArray();
            if (!_isActive || generation != _sortGeneration) return;
            ConfigsView.Synchronize(sorted.Cast<object>());
            OnPropertyChanged(nameof(HasProjects));
        }

        private static DateTime ReadModified(string path)
        {
            try { return Directory.GetLastWriteTimeUtc(path); }
            catch { return DateTime.MinValue; }
        }

        public BackupConfig? FindParentConfig(ManagedFolder folder)
        {
            if (folder == null || Configs == null)
            {
                return null;
            }

            return Configs.FirstOrDefault(c => c.SourceFolders.Contains(folder));
        }

        public async Task BackupFolderAsync(ManagedFolder folder, string comment)
        {
            var parentConfig = FindParentConfig(folder);
            if (parentConfig == null)
            {
                return;
            }

            await BackupService.BackupFolderAsync(
                parentConfig,
                folder,
                BackupInvocationOptions.ForManual(comment));
        }

        public async Task BackupAllFoldersAsync(BackupConfig config, string comment)
        {
            if (config?.SourceFolders == null || config.SourceFolders.Count == 0)
            {
                return;
            }

            await BackupService.BackupConfigAsync(
                config,
                BackupInvocationOptions.ForManual(comment));
        }

        public void TryOpenDestination(BackupConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.DestinationPath))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(config.DestinationPath))
                {
                    Directory.CreateDirectory(config.DestinationPath);
                }

                if (!ShellPathService.TryOpenPath(config.DestinationPath, out var openError))
                {
                    LogService.LogError($"Failed to open destination: {openError}");
                    _interactions.NotifyError(openError ?? I18n.GetString("Common_Failed"));
                }
            }
            catch (Exception ex)
            {
                LogService.LogError($"Failed to open destination: {ex.Message}");
                _interactions.NotifyError(ex.Message);
            }
        }

        private void HookConfigsChanged()
        {
            UnhookConfigsChanged();
            _subscribedConfigs = Configs;
            if (_subscribedConfigs is not null)
                _subscribedConfigs.CollectionChanged += OnConfigsChanged;
        }

        private void UnhookConfigsChanged()
        {
            if (_subscribedConfigs is not null)
            {
                _subscribedConfigs.CollectionChanged -= OnConfigsChanged;
                _subscribedConfigs = null;
            }
        }

        private void OnConfigsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();
        private void OnConfigurationSaved() => ScheduleRefresh();

        private void ScheduleRefresh() => EnqueueOnUiThread(() =>
        {
            if (!_isActive || _refreshScheduled) return;
            _refreshScheduled = true;
            TaskObserver.Observe(RefreshNextTurnAsync(), nameof(HomePageViewModel));
        });

        private async Task RefreshNextTurnAsync()
        {
            await Task.Yield();
            _refreshScheduled = false;
            if (!_isActive) return;
            RefreshFavorites();
            await RefreshConfigsAsync();
        }

        private System.Collections.Generic.IEnumerable<BackupConfig> GetSortedConfigs()
        {
            if (Configs == null)
            {
                yield break;
            }

            var mode = CurrentSortMode;
            var list = Configs.ToList();

            var query = mode switch
            {
                "NameDesc" => list.OrderByDescending(c => c.Name),
                "LastBackupDesc" => list
                    .OrderByDescending(GetConfigLastBackupLocalTimeOrMin)
                    .ThenBy(c => c.Name),
                "LastModifiedDesc" => list
                    .OrderByDescending(GetConfigLastSourceModifiedUtcOrMin)
                    .ThenBy(c => c.Name),
                _ => list.OrderBy(c => c.Name)
            };

            foreach (var cfg in query)
            {
                yield return cfg;
            }
        }

        private static DateTime GetConfigLastBackupLocalTimeOrMin(BackupConfig config)
        {
            if (config?.SourceFolders == null)
            {
                return DateTime.MinValue;
            }

            DateTime? max = null;
            foreach (var folder in config.SourceFolders)
            {
                var t = TryParseBackupLocalTime(folder?.LastBackupTime);
                if (t.HasValue && (!max.HasValue || t.Value > max.Value))
                {
                    max = t;
                }
            }

            return max ?? DateTime.MinValue;
        }

        private static DateTime GetConfigLastSourceModifiedUtcOrMin(BackupConfig config)
        {
            if (config?.SourceFolders == null)
            {
                return DateTime.MinValue;
            }

            DateTime? maxUtc = null;
            foreach (var folder in config.SourceFolders)
            {
                var path = folder?.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    var t = Directory.GetLastWriteTimeUtc(path);
                    if (t == DateTime.MinValue || t == DateTime.MaxValue)
                    {
                        continue;
                    }

                    if (!maxUtc.HasValue || t > maxUtc.Value)
                    {
                        maxUtc = t;
                    }
                }
                catch
                {
                }
            }

            return maxUtc ?? DateTime.MinValue;
        }

        private static DateTime? TryParseBackupLocalTime(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // 兼容旧格式与本地化格式，避免排序因为解析失败全部落到最末尾。
            if (DateTime.TryParseExact(value, "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact;
            }

            if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            return null;
        }

    }
}
