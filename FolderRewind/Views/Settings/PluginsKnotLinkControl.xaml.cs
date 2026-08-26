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

        // SetViewModel 注入后 Bindings.Update() 会把持久化配置回填到各开关，
        // 该回填同样会触发 Toggled 事件，但并非用户操作。若不加以忽略，
        // 每次进入设置页都会借由开关事件重新执行一遍 KnotLink 连接等重逻辑。
        private bool _bindingsApplied;

        public PluginsKnotLinkControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
            // Bindings.Update() 在 UI 线程同步应用绑定，绑定回填引发的虚假 Toggled
            // 已在上面一行内触发完毕，此刻置位即可安全放行后续的用户操作事件。
            _bindingsApplied = true;
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

            var res = await PluginStoreService.InstallManualAsync(filePath);

            var msg = new ContentDialog
            {
                Title = !res.Success
                    ? rl.GetString("Common_Failed")
                    : res.RequiresRestart
                        ? rl.GetString("Notification_Warning_Title")
                        : rl.GetString("Common_Done"),
                Content = res.Message,
                PrimaryButtonText = res.CanEnableNow
                    ? rl.GetString("Plugins_EnableNowButton")
                    : rl.GetString("Common_Ok"),
                CloseButtonText = res.CanEnableNow
                    ? rl.GetString("Plugins_EnableLaterButton")
                    : string.Empty,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(msg);
            if (await msg.ShowAsync() == ContentDialogResult.Primary && res.CanEnableNow)
            {
                try
                {
                    var enabled = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.SetEnabledAsync(
                        new FolderRewind.Plugin.Abstractions.PluginId(res.Operation!.RuntimeAfterOperation.PluginId.Value),
                        enabled: true);
                    if (!enabled.Success)
                        NotificationService.ShowError(
                            FolderRewind.Services.Plugins.V3.PluginV3PackageService.FormatRuntimeDiagnostics(
                                enabled.Diagnostics));
                    else if (enabled.RequiresRestart)
                        NotificationService.ShowWarning(I18n.GetString("Plugins_RuntimeRequiresRestart"));
                }
                catch (Exception ex)
                {
                    NotificationService.ShowError(ex.Message);
                }
            }
            else if (res.Success && res.RequiresRestart)
            {
                NotificationService.ShowWarning(res.Message, rl.GetString("Plugins_StoreDialogTitle"));
            }

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
            FolderRewind.Services.Plugins.V3.PluginUninstallPreview preview;
            try
            {
                preview = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.PreviewUninstallAsync(
                    new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id));
            }
            catch (Exception ex)
            {
                NotificationService.ShowError(ex.Message);
                return;
            }

            var confirmText = string.Format(rl.GetString("Plugins_UninstallConfirm"), plugin.Name, plugin.Id);
            if (preview.AffectedHistoryItemIds.Count > 0)
            {
                confirmText += Environment.NewLine + Environment.NewLine + I18n.Format(
                    "Plugins_UninstallHistoryWarning",
                    preview.AffectedHistoryItemIds.Count);
            }

            var confirm = new ContentDialog
            {
                Title = rl.GetString("Plugins_UninstallTitle"),
                Content = confirmText,
                PrimaryButtonText = rl.GetString("Plugins_UninstallButton"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            ThemeService.ApplyThemeToDialog(confirm);

            var res = await confirm.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            (bool Success, string Message) result;
            var warning = false;
            try
            {
                var v3Id = new FolderRewind.Plugin.Abstractions.PluginId(plugin.Id);
                var uninstall = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.UninstallAsync(
                    v3Id,
                    deleteData: false,
                    confirmation: null);
                result = (
                    uninstall.Outcome is FolderRewind.Plugin.Abstractions.OperationOutcome.Success
                        or FolderRewind.Plugin.Abstractions.OperationOutcome.SuccessWithWarnings,
                    uninstall.Outcome == FolderRewind.Plugin.Abstractions.OperationOutcome.Success
                        ? I18n.Format(
                            "Plugins_UninstallPreservedResult",
                            preview.SettingsCount,
                            preview.ProviderStateLocationCount,
                            preview.DataPath)
                        : uninstall.Diagnostic);
                warning = uninstall.Outcome == FolderRewind.Plugin.Abstractions.OperationOutcome.SuccessWithWarnings;
            }
            catch (Exception ex)
            {
                LogService.LogError(ex.Message, "PluginV3Uninstall", ex);
                result = (false, ex.Message);
            }

            var msg = new ContentDialog
            {
                Title = !result.Success
                    ? rl.GetString("Common_Failed")
                    : warning
                        ? rl.GetString("Notification_Warning_Title")
                        : rl.GetString("Common_Done"),
                Content = result.Message,
                CloseButtonText = rl.GetString("Common_Ok"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(msg);
            await msg.ShowAsync();

            if (warning)
            {
                NotificationService.ShowWarning(result.Message);
            }

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
                var result = await FolderRewind.Services.Plugins.V3.PluginV3PackageService.UninstallAsync(
                    pluginId,
                    deleteData: true,
                    confirmation: confirmation.Text);
                if (result.Outcome == FolderRewind.Plugin.Abstractions.OperationOutcome.SuccessWithWarnings)
                    NotificationService.ShowWarning(result.Diagnostic);
                else
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
            if (!_bindingsApplied) return; // 绑定回填触发，非用户操作

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
                    Title = !result.Success
                        ? rl.GetString("Common_Failed")
                        : result.RequiresRestart
                            ? rl.GetString("Notification_Warning_Title")
                            : rl.GetString("Common_Done"),
                    Content = result.Message,
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(msg);
                await msg.ShowAsync();
                if (result.Success && result.RequiresRestart)
                    NotificationService.ShowWarning(result.Message, rl.GetString("Plugins_StoreDialogTitle"));

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
                    var diagnostics = apply.Validation.Issues.Select(issue => issue.Code).ToList();
                    if (apply.Transition is not null)
                    {
                        diagnostics.Add(
                            FolderRewind.Services.Plugins.V3.PluginV3PackageService.FormatRuntimeDiagnostics(
                                apply.Transition.Diagnostics));
                    }
                    await ShowMessageAsync(
                        I18n.GetString("Common_Failed"),
                        I18n.Format("Plugins_SettingsSaveFailed", string.Join(", ", diagnostics)));
                    return;
                }

                if (apply.Transition?.RequiresRestart == true)
                    NotificationService.ShowWarning(I18n.GetString("Plugins_SettingsApplyRequiresRestart"));
                else
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
            // 绑定回填写入持久化值时同样触发 Toggled，忽略以免每次进设置页都重跑一次连接。
            if (!_bindingsApplied) return;

            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleKnotLinkToggled(ts.IsOn);
                ViewModel.RefreshKnotLinkServerInfo();
            }
        }

        private void OnKnotLinkAutoStartToggled(object sender, RoutedEventArgs e)
        {
            if (!_bindingsApplied) return; // 绑定回填触发，非用户操作

            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleKnotLinkAutoStartToggled(ts.IsOn);
            }
        }

        private async void OnKnotLinkRestartClick(object sender, RoutedEventArgs e)
        {
            var initialized = await ViewModel.RestartKnotLinkServiceAsync();

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
