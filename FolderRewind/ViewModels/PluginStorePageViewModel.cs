using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Runtime.Packaging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.ViewModels
{
    public sealed class PluginStorePageViewModel : ViewModelBase
    {
        private bool _isActive;
        private CancellationTokenSource? _cts;
        private bool _isLoading;
        private string _statusMessage = string.Empty;
        private string _releaseSummary = string.Empty;
        private bool _hasAutoLoaded;

        public PluginStorePageViewModel()
        {
            Assets.CollectionChanged += OnAssetsCollectionChanged;

            LoadCommand = new AsyncRelayCommand(LoadAssetsAsync, () => CanLoad);
            InstallCommand = new AsyncRelayCommand<PluginStoreAssetItem>(InstallAsync);
            InstallManualCommand = new AsyncRelayCommand(InstallManualAsync);
        }

        public ObservableCollection<PluginStoreAssetItem> Assets { get; } = new();

        public IAsyncRelayCommand LoadCommand { get; }

        public IAsyncRelayCommand<PluginStoreAssetItem> InstallCommand { get; }
        public IAsyncRelayCommand InstallManualCommand { get; }

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

        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility ReleaseSummaryVisibility => string.IsNullOrWhiteSpace(ReleaseSummary)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility EmptyListVisibility => !IsLoading && Assets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public async Task ActivateAsync()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            await EnsureInitialLoadAsync();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            CancelPendingOperations();
        }

        public async Task EnsureInitialLoadAsync()
        {
            if (_hasAutoLoaded)
            {
                return;
            }

            _hasAutoLoaded = true;
            if (CanLoad)
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

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsLoading = true;
            try
            {
                var result = await PluginStoreService.GetOfficialCatalogAsync(ct);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    StatusMessage = result.ErrorMessage;
                    NotificationService.ShowError(result.ErrorMessage, I18n.GetString("PluginStorePage_Title.Text"));
                    return;
                }

                ReleaseSummary = result.Summary ?? string.Empty;

                var installedIds = PluginService.InstalledPlugins
                    .Select(p => p.Id)
                    .ToList();

                foreach (var item in result.Items)
                {
                    item.IsInstalled = installedIds.Any(id => string.Equals(id, item.PluginId, StringComparison.OrdinalIgnoreCase));
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
                    NotificationService.ShowSuccess(res.Message, I18n.GetString("PluginStorePage_Title.Text"));
                }
                else
                {
                    NotificationService.ShowError(res.Message, I18n.GetString("PluginStorePage_Title.Text"));
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = rl.GetString("Common_Canceled");
            }
            catch (Exception ex)
            {
                item.Status = ex.Message;
                NotificationService.ShowError(ex.Message, I18n.GetString("PluginStorePage_Title.Text"));
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        private async Task InstallManualAsync()
        {
            var path = await MainWindowService.PickFilePathAsync(
                "Install FolderRewind plugin",
                "FolderRewind.PluginV3.ManualInstall",
                new[] { ".frplugin" });
            if (string.IsNullOrWhiteSpace(path)) return;
            IsLoading = true;
            try
            {
                var result = await PluginV3PackageService.InstallAsync(
                    path,
                    PluginInstallProvenance.Manual,
                    cancellationToken: CancellationToken.None);
                PluginService.RefreshInstalledList();
                StatusMessage = $"Installed {result.Manifest.Contract.Name.Default} {result.State.CurrentVersion}; it remains disabled until you enable it.";
                NotificationService.ShowSuccess(StatusMessage, I18n.GetString("PluginStorePage_Title.Text"));
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                NotificationService.ShowError(ex.Message, I18n.GetString("PluginStorePage_Title.Text"));
            }
            finally { IsLoading = false; }
        }

        private void OnAssetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(EmptyListVisibility));
        }
    }
}
