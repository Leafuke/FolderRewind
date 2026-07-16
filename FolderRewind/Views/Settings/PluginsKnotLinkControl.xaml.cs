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
using Windows.ApplicationModel.Resources;
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

        private void OnPluginsEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandlePluginsEnabledToggled(ts.IsOn);
            }
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
                new[] { ".zip" },
                MainWindowService.SuggestedPickerLocation.Downloads,
                viewMode: PickerViewMode.List);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            var res = await PluginService.InstallFromZipAsync(filePath);

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
            PluginService.RefreshAndLoadEnabled();
        }

        private void OnPluginEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch ts) return;
            if (ts.DataContext is not InstalledPluginInfo plugin) return;

            ViewModel.HandlePluginEnabledToggled(plugin.Id, ts.IsOn);
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

            var result = PluginService.Uninstall(plugin.Id);

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

            var rl = ResourceLoader.GetForViewIndependentUse();

            var defs = PluginService.GetSettingsDefinitions(plugin.Id);
            if (defs == null || defs.Count == 0)
            {
                var noSettings = new ContentDialog
                {
                    Title = rl.GetString("Plugins_SettingsTitle"),
                    Content = rl.GetString("Plugins_NoSettings"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(noSettings);
                await noSettings.ShowAsync();
                return;
            }

            var current = PluginService.GetPluginSettings(plugin.Id);

            var panel = new StackPanel { Spacing = 12 };
            var validation = new TextBlock
            {
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(validation);

            var getters = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in defs)
            {
                var key = def.Key ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key)) continue;

                current.TryGetValue(key, out var curVal);
                var initial = curVal ?? def.DefaultValue ?? string.Empty;

                var header = new TextBlock { Text = def.DisplayName ?? key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                panel.Children.Add(header);

                if (!string.IsNullOrWhiteSpace(def.Description))
                {
                    panel.Children.Add(new TextBlock { Text = def.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                }

                switch (def.Type)
                {
                    case PluginSettingType.Boolean:
                        {
                            var toggle = new ToggleSwitch { IsOn = string.Equals(initial, "true", StringComparison.OrdinalIgnoreCase) };
                            panel.Children.Add(toggle);
                            getters[key] = () => toggle.IsOn ? "true" : "false";
                            break;
                        }
                    case PluginSettingType.Integer:
                        {
                            int.TryParse(initial, out var intVal);
                            var nb = new NumberBox { Value = intVal, Minimum = int.MinValue, Maximum = int.MaxValue, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                            panel.Children.Add(nb);
                            getters[key] = () => ((int)Math.Round(nb.Value)).ToString();
                            break;
                        }
                    case PluginSettingType.Path:
                    case PluginSettingType.String:
                    default:
                        {
                            var tb = new TextBox { Text = initial, PlaceholderText = def.IsRequired ? I18n.GetString("Common_Required") : string.Empty };
                            panel.Children.Add(tb);
                            getters[key] = () => tb.Text ?? string.Empty;
                            break;
                        }
                    case PluginSettingType.MultilineString:
                        {
                            var tb = new TextBox
                            {
                                Text = initial,
                                PlaceholderText = def.IsRequired ? I18n.GetString("Common_Required") : string.Empty,
                                TextWrapping = TextWrapping.Wrap,
                                AcceptsReturn = true,
                                MinHeight = 120,
                                MaxHeight = 260
                            };
                            panel.Children.Add(tb);
                            getters[key] = () => tb.Text ?? string.Empty;
                            break;
                        }
                }

                panel.Children.Add(new TextBlock { Text = string.Empty, Height = 8 });
            }

            var scroll = new ScrollViewer { Content = panel, MaxHeight = 560 };

            var dialog = new ContentDialog
            {
                Title = I18n.Format("Plugins_SettingsDialogTitle", plugin.Name),
                Content = scroll,
                PrimaryButtonText = I18n.GetString("Common_Save"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.Primary) return;

                foreach (var def in defs)
                {
                    if (!def.IsRequired) continue;
                    if (string.IsNullOrWhiteSpace(def.Key)) continue;
                    if (!getters.TryGetValue(def.Key, out var get)) continue;
                    var v = get();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        validation.Text = I18n.Format("Plugins_SettingsMissingRequired", def.DisplayName ?? def.Key);
                        args.Cancel = true;
                        return;
                    }
                }

                validation.Text = string.Empty;
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs)
            {
                if (string.IsNullOrWhiteSpace(def.Key)) continue;
                if (!getters.TryGetValue(def.Key, out var get)) continue;
                newValues[def.Key] = get();
            }

            var saveResult = PluginService.SavePluginSettings(plugin.Id, newValues);
            PluginService.TryReinitialize(plugin.Id);
            await PluginService.TryRunConfigAugmentationForSettingsChangeAsync(
                plugin.Id,
                saveResult.PreviousSettings,
                saveResult.CurrentSettings);
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
                var response = await KnotLinkService.QueryAsync($"SEND {message}", 5000);

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
