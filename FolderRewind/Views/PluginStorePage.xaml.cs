using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace FolderRewind.Views
{
    public sealed partial class PluginStorePage : Page
    {
        private CancellationTokenSource? _cts;
        private bool _isLoading;

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
                    Bindings.Update();
                }
            }
        }

        public ObservableCollection<PluginStoreAssetItem> Assets { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                Bindings.Update();
            }
        }

        public bool CanLoad => !IsLoading && PluginService.IsPluginSystemEnabled();

        public Visibility EmptyRepoHintVisibility => string.IsNullOrWhiteSpace(StoreRepo) ? Visibility.Visible : Visibility.Collapsed;

        public PluginStorePage()
        {
            this.InitializeComponent();
        }

        private async void OnLoadClick(object sender, RoutedEventArgs e)
        {
            await LoadAssetsAsync();
        }

        private async Task LoadAssetsAsync()
        {
            Assets.Clear();

            if (!PluginService.IsPluginSystemEnabled())
            {
                return;
            }

            if (!PluginStoreService.TryParseRepo(StoreRepo, out var owner, out var repo))
            {
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            IsLoading = true;
            try
            {
                var items = await PluginStoreService.GetLatestAssetsAsync(owner, repo, ct);
                foreach (var item in items)
                {
                    Assets.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            finally
            {
                IsLoading = false;
                Bindings.Update();
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
