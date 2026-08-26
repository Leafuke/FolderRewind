using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.Services.Plugins.V3;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Plugin.Abstractions;
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
        private string? _pendingEnablePluginId;
        private bool _canEnableManualNow;

        public PluginStorePageViewModel()
        {
            Assets.CollectionChanged += OnAssetsCollectionChanged;

            LoadCommand = new AsyncRelayCommand(LoadAssetsAsync, () => CanLoad);
            InstallCommand = new AsyncRelayCommand<PluginStoreAssetItem>(InstallAsync);
            EnableCommand = new AsyncRelayCommand<PluginStoreAssetItem>(EnableInstalledAsync);
            EnableManualCommand = new AsyncRelayCommand(EnableManualAsync);
            InstallManualCommand = new AsyncRelayCommand(InstallManualAsync);
        }

        public ObservableCollection<PluginStoreAssetItem> Assets { get; } = new();

        public IAsyncRelayCommand LoadCommand { get; }

        public IAsyncRelayCommand<PluginStoreAssetItem> InstallCommand { get; }
        public IAsyncRelayCommand<PluginStoreAssetItem> EnableCommand { get; }
        public IAsyncRelayCommand EnableManualCommand { get; }
        public IAsyncRelayCommand InstallManualCommand { get; }

        public bool CanEnableManualNow
        {
            get => _canEnableManualNow;
            private set => SetProperty(ref _canEnableManualNow, value);
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

                OnPropertyChanged(nameof(HasStatus));
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

                OnPropertyChanged(nameof(HasReleaseSummary));
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
                OnPropertyChanged(nameof(IsEmptyList));
                LoadCommand.NotifyCanExecuteChanged();
            }
        }

        public bool CanLoad => !IsLoading && PluginService.IsPluginSystemEnabled();

        public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

        public bool HasReleaseSummary => !string.IsNullOrWhiteSpace(ReleaseSummary);

        public bool IsEmptyList => !IsLoading && Assets.Count == 0;

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
            item.CanEnableNow = false;
            item.RequiresRestart = false;
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
                    item.CanEnableNow = res.CanEnableNow;
                    item.RequiresRestart = res.RequiresRestart;
                    if (res.RequiresRestart)
                    {
                        NotificationService.ShowWarning(res.Message, I18n.GetString("PluginStorePage_Title.Text"));
                    }
                    else
                    {
                        NotificationService.ShowSuccess(res.Message, I18n.GetString("PluginStorePage_Title.Text"));
                    }
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

        private async Task EnableInstalledAsync(PluginStoreAssetItem? item)
        {
            if (item is null || !item.CanEnableNow) return;
            var rl = ResourceLoader.GetForViewIndependentUse();
            item.IsBusy = true;
            try
            {
                var result = await PluginV3PackageService.SetEnabledAsync(
                    new PluginId(item.PluginId),
                    enabled: true,
                    CancellationToken.None);
                if (result.Success)
                {
                    item.CanEnableNow = false;
                    item.RequiresRestart = result.RequiresRestart;
                    PluginService.RefreshInstalledList();
                    item.Status = result.RequiresRestart
                        ? I18n.GetString("Plugins_InstallOutcomeRequiresRestartShort")
                        : I18n.GetString("Plugins_EnableSucceeded");
                    if (result.RequiresRestart)
                        NotificationService.ShowWarning(item.Status, I18n.GetString("PluginStorePage_Title.Text"));
                    else
                        NotificationService.ShowSuccess(item.Status, I18n.GetString("PluginStorePage_Title.Text"));
                }
                else
                {
                    item.Status = PluginV3PackageService.FormatRuntimeDiagnostics(result.Diagnostics);
                    NotificationService.ShowError(item.Status, I18n.GetString("PluginStorePage_Title.Text"));
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
            CanEnableManualNow = false;
            _pendingEnablePluginId = null;
            try
            {
                var result = await PluginStoreService.InstallManualAsync(path, CancellationToken.None);
                PluginService.RefreshInstalledList();
                StatusMessage = result.Message;
                CanEnableManualNow = result.CanEnableNow;
                _pendingEnablePluginId = result.Operation?.RuntimeAfterOperation.PluginId.Value;
                if (!result.Success)
                    NotificationService.ShowError(StatusMessage, I18n.GetString("PluginStorePage_Title.Text"));
                else if (result.RequiresRestart)
                    NotificationService.ShowWarning(StatusMessage, I18n.GetString("PluginStorePage_Title.Text"));
                else if (!result.CanEnableNow)
                    NotificationService.ShowSuccess(StatusMessage, I18n.GetString("PluginStorePage_Title.Text"));
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                NotificationService.ShowError(ex.Message, I18n.GetString("PluginStorePage_Title.Text"));
            }
            finally { IsLoading = false; }
        }

        private async Task EnableManualAsync()
        {
            if (!CanEnableManualNow || string.IsNullOrWhiteSpace(_pendingEnablePluginId)) return;
            var rl = ResourceLoader.GetForViewIndependentUse();
            try
            {
                var result = await PluginV3PackageService.SetEnabledAsync(
                    new PluginId(_pendingEnablePluginId),
                    enabled: true,
                    CancellationToken.None);
                if (!result.Success)
                {
                    NotificationService.ShowError(
                        PluginV3PackageService.FormatRuntimeDiagnostics(result.Diagnostics),
                        I18n.GetString("PluginStorePage_Title.Text"));
                    return;
                }

                CanEnableManualNow = false;
                _pendingEnablePluginId = null;
                PluginService.RefreshInstalledList();
                var message = result.RequiresRestart
                    ? I18n.GetString("Plugins_InstallOutcomeRequiresRestartShort")
                    : I18n.GetString("Plugins_EnableSucceeded");
                StatusMessage = message;
                if (result.RequiresRestart)
                    NotificationService.ShowWarning(message, I18n.GetString("PluginStorePage_Title.Text"));
                else
                    NotificationService.ShowSuccess(message, I18n.GetString("PluginStorePage_Title.Text"));
            }
            catch (OperationCanceledException)
            {
                StatusMessage = rl.GetString("Common_Canceled");
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                NotificationService.ShowError(ex.Message, I18n.GetString("PluginStorePage_Title.Text"));
            }
        }

        private void OnAssetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsEmptyList));
        }
    }
}
