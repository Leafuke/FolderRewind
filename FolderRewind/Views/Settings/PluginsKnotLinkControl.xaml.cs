using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;
using PickerViewMode = Windows.Storage.Pickers.PickerViewMode;

namespace FolderRewind.Views.Settings
{
    public sealed partial class PluginsKnotLinkControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public PluginsKnotLinkControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        private async void OnOpenPluginStoreClick(object sender, RoutedEventArgs e)
        {
            if (!PluginService.IsPluginSystemEnabled()) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var dialog = new ContentDialog
            {
                Title = rl.GetString("Plugins_StoreDialogTitle"),
                CloseButtonText = rl.GetString("Common_Close"),
                XamlRoot = this.XamlRoot,
                DefaultButton = ContentDialogButton.Close,
                Content = new Frame()
            };

            if (dialog.Content is Frame frame)
            {
                frame.Navigate(typeof(PluginStorePage));
            }

            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnManualInstallPluginClick(object sender, RoutedEventArgs e)
        {
            if (!PluginService.IsPluginSystemEnabled()) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.Plugins.ManualInstall",
                new[] { ".frplugin" },
                MainWindowService.SuggestedPickerLocation.Downloads,
                viewMode: PickerViewMode.List);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            (bool Success, string Message) res;
            try
            {
                var installed = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.InstallAsync(
                    filePath,
                    FolderRewind.Plugin.Runtime.Packaging.PluginInstallProvenance.Manual);
                res = (true, FolderRewind.Services.Plugins.V3.PluginV3PackageService.FormatInstallOutcome(installed));
            }
            catch (Exception ex) { res = (false, ex.Message); }

            var msg = new ContentDialog
            {
                Title = res.Success ? rl.GetString("Common_Done") : rl.GetString("Common_Failed"),
                Content = res.Message,
                CloseButtonText = rl.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(msg);
            await msg.ShowAsync();

            PluginService.RefreshInstalledList();
        }

        private void OnOpenPluginFolderClick(object sender, RoutedEventArgs e)
        {
            PluginService.OpenPluginFolder();
        }

        private async void OnPluginsExpanderExpanded(object? sender, object e)
        {
            await ViewModel.EnsurePluginsRefreshedAsync();
        }

        private void OnRefreshPluginsClick(object sender, RoutedEventArgs e)
        {
            PluginService.RefreshRuntimeUi();
        }

        private async void OnRestartSafeModeClick(object sender, RoutedEventArgs e)
        {
            var rl = ResourceLoader.GetForViewIndependentUse();
            var confirm = new ContentDialog
            {
                Title = rl.GetString("Plugins_RestartSafeModeTitle"),
                Content = rl.GetString("Plugins_RestartSafeModeConfirm"),
                PrimaryButtonText = rl.GetString("Plugins_RestartSafeModeButton"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            if (PluginRuntimeModeService.TryStartSafeModeInstance(out var error))
            {
                Application.Current.Exit();
                return;
            }

            var failure = new ContentDialog
            {
                Title = rl.GetString("Common_Failed"),
                Content = string.Format(rl.GetString("Plugins_RestartSafeModeFailed"), error),
                CloseButtonText = rl.GetString("Common_Ok"),
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(failure);
            await failure.ShowAsync();
        }

        private async void OnPluginEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch ts) return;
            if (ts.DataContext is not InstalledPluginInfo plugin) return;
            if (plugin.IsEnabled == ts.IsOn) return;

            ts.IsEnabled = false;
            try
            {
                await ViewModel.HandlePluginEnabledToggledAsync(plugin.Id, ts.IsOn);
            }
            finally
            {
                ts.IsEnabled = true;
            }
        }

        private async void OnPluginUninstallClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            var confirm = new ContentDialog
            {
                Title = rl.GetString("Plugins_UninstallTitle"),
                Content = string.Format(rl.GetString("Plugins_UninstallConfirm"), plugin.Name, plugin.Id),
                PrimaryButtonText = rl.GetString("Plugins_UninstallButton"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            ThemeService.ApplyThemeToDialog(confirm);

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var v3Id = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
            var preview = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.UninstallAsync(
                v3Id,
                deleteData: false,
                confirmation: null);
            var result = (Success: true, Message: I18n.Format(
                "Plugins_UninstallPreservedResult",
                preview.SettingsCount,
                preview.ProviderStateLocationCount,
                preview.DataPath));

            var msg = new ContentDialog
            {
                Title = result.Success ? rl.GetString("Common_Done") : rl.GetString("Common_Failed"),
                Content = result.Message,
                CloseButtonText = rl.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(msg);
            await msg.ShowAsync();

            PluginService.RefreshInstalledList();
        }

        private async void OnPluginDeleteDataClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;
            var pluginId = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
            if (!await FolderRewind.Services.Plugins.V3.PluginV3PackageService.IsInstalledAsync(pluginId))
            {
                NotificationService.ShowWarning(I18n.GetString("Plugins_DeleteDataV3Only"));
                return;
            }

            var preview = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.PreviewUninstallAsync(pluginId);
            var confirmation = new TextBox
            {
                Header = I18n.Format("Plugins_DeleteDataConfirmationHeader", preview.RequiredConfirmation),
                PlaceholderText = preview.RequiredConfirmation
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = I18n.Format(
                    "Plugins_DeleteDataPreview",
                    preview.SettingsCount,
                    preview.ProviderStateLocationCount,
                    preview.DataPath,
                    preview.AffectedHistoryItemIds.Count),
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(confirmation);
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Plugins_DeleteDataTitle"),
                Content = content,
                PrimaryButtonText = I18n.GetString("Plugins_DeleteDataButton"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await FolderRewind.Services.Plugins.V3.PluginV3PackageService.UninstallAsync(
                    pluginId,
                    deleteData: true,
                    confirmation: confirmation.Text);
                NotificationService.ShowSuccess(I18n.GetString("Plugins_DeleteDataSuccess"));
            }
            catch (Exception ex)
            {
                NotificationService.ShowError(ex.Message);
            }
            PluginService.RefreshInstalledList();
        }

        private void OnPluginsAutoCheckUpdatesToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandlePluginsAutoCheckUpdatesToggled(ts.IsOn);
            }
        }

        private async void OnCheckPluginUpdatesClick(object sender, RoutedEventArgs e)
        {
            var rl = ResourceLoader.GetForViewIndependentUse();

            try
            {
                await PluginService.CheckAllPluginUpdatesAsync(respectAutoCheckSetting: false);

                var hasUpdates = ViewModel.InstalledPlugins.Any(p => p.HasUpdate && !string.IsNullOrWhiteSpace(p.UpdateDownloadUrl));
                var msg = new ContentDialog
                {
                    Title = rl.GetString("Common_Done"),
                    Content = hasUpdates
                        ? rl.GetString("PluginService_UpdatesAvailable")
                        : rl.GetString("PluginService_NoUpdatesAvailable"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();
            }
            catch (Exception ex)
            {
                var msg = new ContentDialog
                {
                    Title = rl.GetString("Common_Failed"),
                    Content = ex.Message,
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();
            }
        }

        private async void OnPluginUpdateClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var rl = ResourceLoader.GetForViewIndependentUse();

            if (string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
            {
                var noUrl = new ContentDialog
                {
                    Title = rl.GetString("Common_Failed"),
                    Content = rl.GetString("PluginService_NoUpdateUrl"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(noUrl);
                await noUrl.ShowAsync();
                return;
            }

            var confirm = new ContentDialog
            {
                Title = rl.GetString("Plugins_UpdateTitle"),
                Content = string.Format(rl.GetString("Plugins_UpdateConfirm"), plugin.Name, plugin.Version, plugin.LatestVersion),
                PrimaryButtonText = rl.GetString("Plugins_UpdateButton"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            btn.IsEnabled = false;
            try
            {
                var result = await PluginService.UpdatePluginFromUrlAsync(plugin);

                var msg = new ContentDialog
                {
                    Title = result.Success ? rl.GetString("Common_Done") : rl.GetString("Common_Failed"),
                    Content = result.Message,
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();

                PluginService.RefreshInstalledList();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void OnPluginSettingsClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not InstalledPluginInfo plugin) return;

            var pluginId = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
            if (!await FolderRewind.Services.Plugins.V3.PluginV3PackageService.IsInstalledAsync(pluginId))
            {
                await ShowMessageAsync(
                    I18n.GetString("Common_Failed"),
                    I18n.GetString("Plugins_NotInstalled"));
                return;
            }

            await ShowPluginV3SettingsAsync(plugin, pluginId);
        }

        private async Task ShowPluginV3SettingsAsync(
            InstalledPluginInfo plugin,
            FolderRewind.Plugin.Abstractions.PluginId pluginId)
        {
            try
            {
                var data = await FolderRewind.Services.Plugins.V3.PluginV3PackageService
                    .GetSettingsEditorDataAsync(pluginId);
                if (data == null || data.Schema.Settings.Count == 0)
                {
                    await ShowMessageAsync(
                        I18n.GetString("Plugins_SettingsTitle"),
                        I18n.GetString("Plugins_NoSettings"));
                    return;
                }

                var dialog = new PluginV3SettingsDialog(plugin.Name, data, XamlRoot);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary
                    || dialog.ResultSettings is null)
                {
                    return;
                }

                var apply = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.ApplySettingsAsync(
                    pluginId,
                    dialog.ResultSettings);
                if (!apply.Success)
                {
                    var diagnostics = apply.Validation.Issues.Select(issue => issue.Code)
                        .Concat(apply.Transition?.Diagnostics.Select(value => value.Code) ?? Array.Empty<string>());
                    await ShowMessageAsync(
                        I18n.GetString("Common_Failed"),
                        I18n.Format("Plugins_SettingsSaveFailed", string.Join(", ", diagnostics)));
                    return;
                }

                NotificationService.ShowSuccess(I18n.GetString("Plugins_SettingsSaved"));
                PluginService.RefreshInstalledList();
                await FolderRewind.Services.Plugins.V3.PluginV3DiscoveryService
                    .RunAutoCreateAsync(pluginId);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(
                    I18n.GetString("Common_Failed"),
                    I18n.Format("Plugins_SettingsLoadFailed", ex.Message));
            }
        }

        private async Task ShowMessageAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private void OnKnotLinkToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleKnotLinkToggled(ts.IsOn);
                ViewModel.RefreshKnotLinkServerInfo();
            }
        }

        private void OnKnotLinkAutoStartToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleKnotLinkAutoStartToggled(ts.IsOn);
            }
        }

        private async void OnKnotLinkRestartClick(object sender, RoutedEventArgs e)
        {
            var initialized = ViewModel.RestartKnotLinkService();

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLink_Title"),
                Content = initialized
                    ? I18n.GetString("SettingsPage_KnotLink_RestartSuccess")
                    : I18n.GetString("SettingsPage_KnotLink_RestartFailed"),
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnKnotLinkTestClick(object sender, RoutedEventArgs e)
        {
            if (!KnotLinkService.IsInitialized)
            {
                var errorDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkTest_Title"),
                    Content = I18n.GetString("SettingsPage_KnotLinkTest_NotInitialized"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(errorDialog);
                await errorDialog.ShowAsync();
                return;
            }

            KnotLinkService.BroadcastEvent(null, "test", new Dictionary<string, string?>
            {
                ["message"] = "Hello from FolderRewind!"
            });

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLinkTest_Title"),
                Content = I18n.GetString("SettingsPage_KnotLinkTest_Broadcasted"),
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnKnotLinkSendCustomClick(object sender, RoutedEventArgs e)
        {
            if (!KnotLinkService.IsInitialized)
            {
                var errorDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                    Content = I18n.GetString("SettingsPage_KnotLinkTest_NotInitialized"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(errorDialog);
                await errorDialog.ShowAsync();
                return;
            }

            var inputBox = new TextBox
            {
                PlaceholderText = I18n.GetString("SettingsPage_KnotLinkSendCustom_Placeholder"),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinWidth = 360
            };

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                Content = inputBox,
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var message = inputBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                var emptyDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_Title"),
                    Content = I18n.GetString("SettingsPage_KnotLinkSendCustom_Empty"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(emptyDialog);
                await emptyDialog.ShowAsync();
                return;
            }

            try
            {
                var response = await KnotLinkService.QueryAsync(message, 5000);

                var respDialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkSendCustom_ResultTitle"),
                    Content = response,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(respDialog);
                await respDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var respDialog = new ContentDialog
                {
                    Title = I18n.GetString("Common_Failed"),
                    Content = ex.Message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(respDialog);
                await respDialog.ShowAsync();
            }
        }

        private async void OnKnotLinkStartServerClick(object sender, RoutedEventArgs e)
        {
            var started = ViewModel.StartKnotLinkServer();
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("SettingsPage_KnotLink_Title"),
                Content = started
                    ? I18n.GetString("SettingsPage_KnotLinkServer_StartSuccess")
                    : I18n.GetString("SettingsPage_KnotLinkServer_StartFailed"),
                CloseButtonText = I18n.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnKnotLinkCheckServerUpdateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var info = await ViewModel.CheckKnotLinkServerUpdateAsync();
                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkCheckUpdate"),
                    Content = info?.HasUpdate == true
                        ? I18n.Format("SettingsPage_KnotLinkUpdateAvailable", info.LatestVersion)
                        : I18n.GetString("SettingsPage_KnotLinkUpToDate"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("SettingsPage_KnotLinkCheckUpdate"),
                    Content = I18n.Format("SettingsPage_KnotLinkUpdateServerError", ex.Message),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
            }
        }

        private async void OnKnotLinkUpdateServerClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) btn.IsEnabled = false;
            try
            {
                await ViewModel.DownloadAndRunKnotLinkInstallerAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("Common_Failed"),
                    Content = ex.Message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
            }
            finally
            {
                if (sender is Button btn2) btn2.IsEnabled = true;
            }
        }
    }
}
