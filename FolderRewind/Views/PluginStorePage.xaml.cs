using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace FolderRewind.Views
{
    public sealed partial class PluginStorePage : Page
    {
        private CancellationTokenSource? _cts;
        private bool _isViewInitialized;
        private bool _isLoading;
        private string _statusMessage = string.Empty;
        private string _releaseSummary = string.Empty;

        public PluginHostSettings PluginSettings => ConfigService.CurrentConfig.GlobalSettings.Plugins;

        public string StoreRepo
        {
            get => PluginSettings.StoreRepo;
            set
            {
                if (PluginSettings.StoreRepo != value)
                {
                    PluginSettings.StoreRepo = value ?? string.Empty;
                    ConfigService.Save();
                    TryUpdateBindings();
                }
            }
        }

        public ObservableCollection<PluginStoreAssetItem> Assets { get; } = new();

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value ?? string.Empty;
                TryUpdateBindings();
            }
        }

        public string ReleaseSummary
        {
            get => _releaseSummary;
            set
            {
                _releaseSummary = value ?? string.Empty;
                TryUpdateBindings();
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                TryUpdateBindings();
            }
        }

        public bool CanLoad => !IsLoading && PluginService.IsPluginSystemEnabled();

        public Visibility EmptyRepoHintVisibility => string.IsNullOrWhiteSpace(StoreRepo) ? Visibility.Visible : Visibility.Collapsed;

        public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusMessage) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ReleaseSummaryVisibility => string.IsNullOrWhiteSpace(ReleaseSummary) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility EmptyListVisibility => !IsLoading && !string.IsNullOrWhiteSpace(StoreRepo) && Assets.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public PluginStorePage()
        {
            this.InitializeComponent();
            _isViewInitialized = true;
            this.Loaded += OnPageLoaded;
        }

        private void TryUpdateBindings()
        {
            if (!_isViewInitialized) return;
            Bindings.Update();
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(StoreRepo))
            {
                await LoadAssetsAsync();
            }
        }

        private async void OnLoadClick(object sender, RoutedEventArgs e)
        {
            await LoadAssetsAsync();
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
                TryUpdateBindings();
            }
        }

        private async void OnInstallClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not PluginStoreAssetItem item) return;
            if (!PluginService.IsPluginSystemEnabled()) return;

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
    }
}
