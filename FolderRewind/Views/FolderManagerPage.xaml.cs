using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class FolderManagerPage : Page
    {
        public FolderManagerPageViewModel ViewModel { get; } = new();

        private bool _minecraftPluginHintShown;

        private const string MineRewindDownloadUrl = "https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases";

        public FolderManagerPage()
        {
            this.InitializeComponent();

            ViewModel.PendingFolderSelectionRequested += TryApplyPendingSelection;
            Loaded += (_, __) => TryApplyPendingSelection();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ViewModel.Activate();

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
                HotkeyManager.Invoked += HotkeyManager_Invoked;
            }
            catch
            {
            }

            // 进入页面时刷新一次视图集合，避免安装包环境下首次绑定异常。
            ViewModel.RefreshConfigsView();

            if (e.Parameter is ManagerNavigationParameter managerParam)
            {
                ApplyManagerNavigation(managerParam);
                return;
            }

            if (e.Parameter is ManagedFolder folder)
            {
                var parent = ViewModel.FindConfigByFolderPath(folder.Path);
                if (parent != null)
                {
                    ViewModel.CurrentConfig = parent;
                    ViewModel.SetPendingFolderPath(folder.Path);
                    TryApplyPendingSelection();
                    return;
                }
            }

            if (e.Parameter is BackupConfig config)
            {
                var match = ViewModel.FindConfigById(config.Id);
                if (match != null)
                {
                    ViewModel.CurrentConfig = match;
                }
            }
            else
            {
                ViewModel.EnsureCurrentConfigSelectedFromSettings();
            }

            ViewModel.RefreshCurrentFoldersView();
            TryApplyPendingSelection();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            try
            {
                HotkeyManager.Invoked -= HotkeyManager_Invoked;
            }
            catch
            {
            }

            ViewModel.Deactivate();
        }

        private async void HotkeyManager_Invoked(object? sender, HotkeyInvokedEventArgs e)
        {
            if (e == null || e.HotkeyId != HotkeyManager.Action_BackupSelectedFolder)
            {
                return;
            }

            if (ViewModel.CurrentConfig == null || ViewModel.SelectedFolder == null)
            {
                return;
            }

            var baseComment = ViewModel.BackupComment;
            var comment = string.IsNullOrWhiteSpace(baseComment)
                ? "[快捷键]"
                : (baseComment.Contains("[快捷键]", StringComparison.OrdinalIgnoreCase)
                    ? baseComment
                    : $"{baseComment} [快捷键]");

            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (BackupSelectedButton != null && !BackupSelectedButton.IsEnabled)
                    {
                        return;
                    }

                    if (BackupSelectedButton != null)
                    {
                        BackupSelectedButton.IsEnabled = false;
                    }

                    await ViewModel.BackupSelectedFolderAsync(comment);
                }
                finally
                {
                    if (BackupSelectedButton != null)
                    {
                        BackupSelectedButton.IsEnabled = ViewModel.HasSelectedFolder;
                    }
                }
            });
        }

        private void ApplyManagerNavigation(ManagerNavigationParameter param)
        {
            BackupConfig? match = null;

            if (!string.IsNullOrWhiteSpace(param.ConfigId))
            {
                match = ViewModel.FindConfigById(param.ConfigId);
            }

            if (match == null && !string.IsNullOrWhiteSpace(param.FolderPath))
            {
                match = ViewModel.FindConfigByFolderPath(param.FolderPath);
            }

            if (match != null)
            {
                ViewModel.CurrentConfig = match;
            }
            else if (ViewModel.CurrentConfig == null && ViewModel.Configs.Count > 0)
            {
                ViewModel.CurrentConfig = ViewModel.Configs[0];
            }

            if (!string.IsNullOrWhiteSpace(param.FolderPath))
            {
                ViewModel.SetPendingFolderPath(param.FolderPath);
            }

            TryApplyPendingSelection();
        }

        private void TryApplyPendingSelection()
        {
            var path = ViewModel.PendingFolderPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (ViewModel.CurrentConfig?.SourceFolders == null || FolderList == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                var target = ViewModel.FindFolderInCurrentConfig(path);
                if (target == null)
                {
                    ViewModel.ClearRememberedFolderPathIfMatches(path);
                    ViewModel.ClearPendingFolderPath();
                    return;
                }

                FolderList.SelectedItem = target;
                FolderList.ScrollIntoView(target);
                ViewModel.SetSelectedFolder(target, persistSelection: true);
                ViewModel.ClearPendingFolderPath();
            });
        }

        private void ConfigSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConfigSelector.SelectedItem is BackupConfig config)
            {
                ViewModel.CurrentConfig = config;
            }
            else
            {
                ViewModel.CurrentConfig = null;
            }

            ViewModel.SetSelectedFolder(null, persistSelection: false);
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SetSelectedFolder(FolderList.SelectedItem as ManagedFolder, persistSelection: true);
        }

        private async void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentConfig == null)
            {
                return;
            }

            var folder = await PickFolderAsync();
            if (folder == null)
            {
                return;
            }

            await AddSingleFolderInternalAsync(folder);
        }

        private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder)
            {
                ViewModel.RemoveFolder(folder);
            }
        }

        private void OnFavoriteToggleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ManagedFolder folder)
            {
                ViewModel.ToggleFavorite(folder);
            }
        }

        private void OnPinToTopClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder)
            {
                ViewModel.PinFolderToTop(folder);
            }
        }

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder)
            {
                Process.Start("explorer.exe", folder.Path);
            }
        }

        private void OnOpenMiniWindowClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is ManagedFolder folder && ViewModel.CurrentConfig != null)
            {
                MiniWindowService.Open(ViewModel.CurrentConfig, folder);
            }
        }

        private void OnDescriptionEditorKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter || sender is not TextBox textBox)
            {
                return;
            }

            e.Handled = true;

            try
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            catch
            {
            }

            SaveDescriptionIfNeeded(textBox);

            try
            {
                FolderList?.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        private void OnDescriptionEditorLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                SaveDescriptionIfNeeded(textBox);
            }
        }

        private void SaveDescriptionIfNeeded(TextBox textBox)
        {
            if (textBox.DataContext is not ManagedFolder)
            {
                return;
            }

            try
            {
                ViewModel.SaveConfig();
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task AddSingleFolderInternalAsync(StorageFolder folder)
        {
            var result = ViewModel.AddFolder(folder.Path, folder.Name, out var addedFolder);
            if (result == FolderManagerPageViewModel.AddFolderResult.DuplicateDisplayName)
            {
                await ShowDuplicateDisplayNameBlockedAsync(FolderNameConflictService.ResolveDisplayName(folder.Name, folder.Path));
                return;
            }

            if (result == FolderManagerPageViewModel.AddFolderResult.Added &&
                addedFolder != null &&
                ShouldSuggestMineRewind(addedFolder.Path, addedFolder.DisplayName))
            {
                _ = ShowMineRewindSuggestionAsync();
            }
        }

        private async System.Threading.Tasks.Task ShowDuplicateDisplayNameBlockedAsync(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("FolderManager_DuplicateDisplayName_Title"),
                Content = I18n.Format("FolderManager_DuplicateDisplayName_Content", folderName, ViewModel.CurrentConfig?.Name ?? string.Empty),
                CloseButtonText = I18n.GetString("Common_Ok"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            await dialog.ShowAsync();
        }

        private async System.Threading.Tasks.Task ShowSkippedDuplicateDisplayNamesAsync(IEnumerable<string> folderNames)
        {
            var distinctNames = folderNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (distinctNames.Count == 0)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("FolderManager_DuplicateDisplayName_Title"),
                Content = I18n.Format(
                    "FolderManager_DuplicateDisplayName_BatchContent",
                    ViewModel.CurrentConfig?.Name ?? string.Empty,
                    string.Join(Environment.NewLine, distinctNames.Select(name => $"- {name}"))),
                CloseButtonText = I18n.GetString("Common_Ok"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            await dialog.ShowAsync();
        }

        private async void OnAddSingleFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder == null)
            {
                return;
            }

            await AddSingleFolderInternalAsync(folder);
        }

        private async void OnAddSubFoldersClick(object sender, RoutedEventArgs e)
        {
            var rootFolder = await PickFolderAsync();
            if (rootFolder == null)
            {
                return;
            }

            var result = ViewModel.AddSubFolders(rootFolder.Path);
            if (!result.Success)
            {
                Debug.WriteLine(result.ErrorMessage);
                return;
            }

            if (result.AddedFolders.Any(f => ShouldSuggestMineRewind(f.Path, f.DisplayName)))
            {
                _ = ShowMineRewindSuggestionAsync();
            }

            if (result.DuplicateDisplayNames.Count > 0)
            {
                await ShowSkippedDuplicateDisplayNamesAsync(result.DuplicateDisplayNames);
            }
        }

        private async void OnPluginDiscoverFoldersClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentConfig == null)
            {
                return;
            }

            PluginService.Initialize();

            var rootFolder = await PickFolderAsync();
            if (rootFolder == null)
            {
                return;
            }

            var discovered = PluginService.InvokeDiscoverManagedFolders(rootFolder.Path);
            if (discovered == null || discovered.Count == 0)
            {
                var rl = ResourceLoader.GetForViewIndependentUse();
                var dialog = new ContentDialog
                {
                    Title = rl.GetString("FolderManager_PluginDiscover_NoResultTitle"),
                    Content = rl.GetString("FolderManager_PluginDiscover_NoResultContent"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            var candidates = ViewModel.BuildPluginDiscoverCandidates(discovered);
            if (candidates.ToAdd.Count == 0)
            {
                if (candidates.DuplicateDisplayNames.Count > 0)
                {
                    await ShowSkippedDuplicateDisplayNamesAsync(candidates.DuplicateDisplayNames);
                    return;
                }

                var rl = ResourceLoader.GetForViewIndependentUse();
                var dialog = new ContentDialog
                {
                    Title = rl.GetString("FolderManager_PluginDiscover_NoNewTitle"),
                    Content = rl.GetString("FolderManager_PluginDiscover_NoNewContent"),
                    CloseButtonText = rl.GetString("Common_Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            {
                var rl = ResourceLoader.GetForViewIndependentUse();
                var confirm = new ContentDialog
                {
                    Title = rl.GetString("FolderManager_PluginDiscover_ConfirmTitle"),
                    Content = string.Format(rl.GetString("FolderManager_PluginDiscover_ConfirmContent"), candidates.ToAdd.Count),
                    PrimaryButtonText = rl.GetString("Common_Ok"),
                    CloseButtonText = rl.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                var res = await confirm.ShowAsync();
                if (res != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            ViewModel.AddDiscoveredFolders(candidates.ToAdd);

            if (candidates.ToAdd.Any(f => ShouldSuggestMineRewind(f.Path, f.DisplayName)))
            {
                _ = ShowMineRewindSuggestionAsync();
            }

            if (candidates.DuplicateDisplayNames.Count > 0)
            {
                await ShowSkippedDuplicateDisplayNamesAsync(candidates.DuplicateDisplayNames);
            }
        }

        private bool ShouldSuggestMineRewind(string path, string? name)
        {
            if (_minecraftPluginHintShown || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var folderName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name;
            var isMinecraftRoot = string.Equals(folderName, ".minecraft", StringComparison.OrdinalIgnoreCase);
            var hasLevelDat = File.Exists(Path.Combine(path, "level.dat"));

            return isMinecraftRoot || hasLevelDat;
        }

        private async System.Threading.Tasks.Task ShowMineRewindSuggestionAsync()
        {
            if (_minecraftPluginHintShown)
            {
                return;
            }

            _minecraftPluginHintShown = true;

            var rl = ResourceLoader.GetForViewIndependentUse();
            var dialog = new ContentDialog
            {
                Title = rl.GetString("FolderManager_MineRewindHint_Title"),
                Content = rl.GetString("FolderManager_MineRewindHint_Content"),
                PrimaryButtonText = rl.GetString("FolderManager_MineRewindHint_OpenDownload"),
                CloseButtonText = rl.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    await Launcher.LaunchUriAsync(new Uri(MineRewindDownloadUrl));
                }
                catch
                {
                }
            }
        }

        private async void OnChangeIconClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not ManagedFolder folder)
            {
                return;
            }

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                var destPath = Path.Combine(folder.Path, "icon.png");
                File.Copy(file.Path, destPath, overwrite: true);

                folder.CoverImagePath = string.Empty;
                folder.CoverImagePath = destPath;
                ViewModel.SaveConfig();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy icon failed: {ex.Message}");
            }
        }

        private async void OnBackupConfigClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentConfig == null)
            {
                return;
            }

            BackupConfigButton.IsEnabled = false;

            try
            {
                await ViewModel.BackupCurrentConfigAsync();
                Debug.WriteLine("配置备份完成");
            }
            finally
            {
                BackupConfigButton.IsEnabled = true;
            }
        }

        private async void OnBackupSelectedClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentConfig == null || ViewModel.SelectedFolder == null)
            {
                return;
            }

            BackupSelectedButton.IsEnabled = false;
            var comment = ViewModel.BackupComment?.Trim();

            try
            {
                await ViewModel.BackupSelectedFolderAsync(comment);
                ViewModel.BackupComment = string.Empty;
            }
            finally
            {
                BackupSelectedButton.IsEnabled = ViewModel.HasSelectedFolder;
            }
        }

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentConfig == null || ViewModel.SelectedFolder == null)
            {
                return;
            }

            var param = ManagerNavigationParameter.ForFolder(ViewModel.CurrentConfig.Id, ViewModel.SelectedFolder.Path);
            App.Shell.NavigateTo("History", param);
        }

        private async void OnConfigSettingsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.CurrentConfig == null)
            {
                return;
            }

            var dialog = new ConfigSettingsDialog(ViewModel.CurrentConfig);
            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                ViewModel.SaveConfig();
            }
        }

        private async System.Threading.Tasks.Task<StorageFolder?> PickFolderAsync()
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");

            if (App._window != null)
            {
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App._window));
            }

            return await picker.PickSingleFolderAsync();
        }
    }
}
