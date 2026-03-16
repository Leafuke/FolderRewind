using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace FolderRewind.ViewModels
{
    public sealed class PluginStorePageViewModel : ViewModelBase
    {
        private CancellationTokenSource? _cts;
        private bool _isLoading;
        private string _statusMessage = string.Empty;
        private string _releaseSummary = string.Empty;
        private bool _hasAutoLoaded;

        private PluginHostSettings PluginSettings => ConfigService.CurrentConfig.GlobalSettings.Plugins;

        public PluginStorePageViewModel()
        {
            Assets.CollectionChanged += OnAssetsCollectionChanged;

            LoadCommand = new AsyncRelayCommand(LoadAssetsAsync, () => CanLoad);
            InstallCommand = new AsyncRelayCommand<PluginStoreAssetItem>(InstallAsync);
        }

        public ObservableCollection<PluginStoreAssetItem> Assets { get; } = new();

        public IAsyncRelayCommand LoadCommand { get; }

        public IAsyncRelayCommand<PluginStoreAssetItem> InstallCommand { get; }

        public string StoreRepo
        {
            get => PluginSettings.StoreRepo;
            set
            {
                var next = value ?? string.Empty;
                if (PluginSettings.StoreRepo == next)
                {
                    return;
                }

                PluginSettings.StoreRepo = next;
                ConfigService.Save();

                OnPropertyChanged();
                OnPropertyChanged(nameof(EmptyRepoHintVisibility));
                OnPropertyChanged(nameof(EmptyListVisibility));
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (!SetProperty(ref _statusMessage, value ?? string.Empty))
                {
                    return;
                }

                OnPropertyChanged(nameof(StatusVisibility));
            }
        }

        public string ReleaseSummary
        {
            get => _releaseSummary;
            private set
            {
                if (!SetProperty(ref _releaseSummary, value ?? string.Empty))
                {
                    return;
                }

                OnPropertyChanged(nameof(ReleaseSummaryVisibility));
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (!SetProperty(ref _isLoading, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanLoad));
                OnPropertyChanged(nameof(EmptyListVisibility));
                LoadCommand.NotifyCanExecuteChanged();
            }
        }

        public bool CanLoad => !IsLoading && PluginService.IsPluginSystemEnabled();

        public Visibility EmptyRepoHintVisibility => string.IsNullOrWhiteSpace(StoreRepo)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility ReleaseSummaryVisibility => string.IsNullOrWhiteSpace(ReleaseSummary)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility EmptyListVisibility => !IsLoading && !string.IsNullOrWhiteSpace(StoreRepo) && Assets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public async Task EnsureInitialLoadAsync()
        {
            if (_hasAutoLoaded)
            {
                return;
            }

            _hasAutoLoaded = true;
            if (!string.IsNullOrWhiteSpace(StoreRepo) && CanLoad)
            {
                await LoadAssetsAsync();
            }
        }

        public void CancelPendingOperations()
        {
            _cts?.Cancel();
        }

        private async Task LoadAssetsAsync()
        {
            var rl = ResourceLoader.GetForViewIndependentUse();

            Assets.Clear();
            ReleaseSummary = string.Empty;
            StatusMessage = string.Empty;

            if (!PluginService.IsPluginSystemEnabled())
            {
                StatusMessage = rl.GetString("PluginStorePage_PluginSystemDisabled");
                return;
            }

            if (!PluginStoreService.TryParseRepo(StoreRepo, out var owner, out var repo))
            {
                StatusMessage = rl.GetString("PluginStorePage_InvalidRepo");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsLoading = true;
            try
            {
                var result = await PluginStoreService.GetLatestAssetsAsync(owner, repo, ct);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    StatusMessage = result.ErrorMessage;
                    return;
                }

                ReleaseSummary = result.Summary ?? string.Empty;

                var installedIds = PluginService.InstalledPlugins
                    .Select(p => p.Id)
                    .ToList();

                foreach (var item in result.Items)
                {
                    item.IsInstalled = installedIds.Any(id => item.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
                    Assets.Add(item);
                }

                if (Assets.Count == 0)
                {
                    StatusMessage = rl.GetString("PluginStorePage_NoAssets");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = rl.GetString("Common_Canceled");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task InstallAsync(PluginStoreAssetItem? item)
        {
            if (item == null)
            {
                return;
            }

            if (!PluginService.IsPluginSystemEnabled())
            {
                return;
            }

            var rl = ResourceLoader.GetForViewIndependentUse();

            item.IsBusy = true;
            item.Status = rl.GetString("PluginStorePage_StatusDownloading");

            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                var res = await PluginStoreService.DownloadAndInstallAsync(item, ct);
                item.Status = res.Message;

                if (res.Success)
                {
                    PluginService.RefreshInstalledList();
                    item.IsInstalled = true;
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = rl.GetString("Common_Canceled");
            }
            catch (Exception ex)
            {
                item.Status = ex.Message;
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        private void OnAssetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(EmptyListVisibility));
        }
    }
}
