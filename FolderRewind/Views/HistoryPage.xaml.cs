using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class HistoryPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // 历史列表数据源
        public ObservableCollection<HistoryItem> FilteredHistory { get; set; } = new();

        // 快捷访问配置列表
        public ObservableCollection<BackupConfig> Configs => ConfigService.CurrentConfig?.BackupConfigs ?? new ObservableCollection<BackupConfig>();

        private GlobalSettings Settings => ConfigService.CurrentConfig?.GlobalSettings;

        private bool _isEmpty = true;
        public bool IsEmpty
        {
            get => _isEmpty;
            set { _isEmpty = value; OnPropertyChanged(nameof(IsEmpty)); }
        }

        private string _commentFilterText = string.Empty;
        public string CommentFilterText
        {
            get => _commentFilterText;
            set
            {
                _commentFilterText = value ?? string.Empty;
                ApplyCommentFilter();
            }
        }

        public bool UseHistoryStatusColors
        {
            get => Settings?.UseHistoryStatusColors ?? true;
            set
            {
                if (Settings != null)
                {
                    Settings.UseHistoryStatusColors = value;
                    ConfigService.Save();
                }

                UpdateTimelineVisuals(FilteredHistory);
                OnPropertyChanged(nameof(UseHistoryStatusColors));
            }
        }

        private int _missingCount;
        public bool HasMissing => _missingCount > 0;

        private readonly List<HistoryItem> _currentAllItems = new();

        public HistoryPage()
        {
            this.InitializeComponent();

            // 1. 确保历史服务已初始化
            HistoryService.Initialize();

            // 2. [关键修复] 显式在代码中设置数据源，防止 XAML 绑定延迟导致 ComboBox 为空
            ConfigFilter.ItemsSource = Configs;
            HistoryList.ItemsSource = FilteredHistory;

            // 初始化 Toggle 状态
            try
            {
                if (UseColorsToggle != null)
                {
                    UseColorsToggle.IsOn = UseHistoryStatusColors;
                }
            }
            catch
            {
            }
        }

        private bool _isNavigating = false;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ManagerNavigationParameter managerParam)
            {
                ApplySelectionFromNavigation(managerParam.ConfigId, managerParam.FolderPath);
                return;
            }

            if (e.Parameter is ManagedFolder folder)
            {
                ApplySelectionFromNavigation(null, folder.Path);
                return;
            }

            RestoreLastSelection();
        }

        private void ApplySelectionFromNavigation(string? configId, string? folderPath)
        {
            _isNavigating = true;

            try
            {
                BackupConfig? targetConfig = null;
                if (!string.IsNullOrWhiteSpace(configId))
                {
                    targetConfig = Configs.FirstOrDefault(c => c.Id == configId);
                }

                if (targetConfig == null && !string.IsNullOrWhiteSpace(folderPath))
                {
                    targetConfig = Configs.FirstOrDefault(c => c.SourceFolders.Any(f => f.Path == folderPath));
                }

                if (targetConfig == null)
                {
                    return;
                }

                ConfigFilter.SelectedItem = targetConfig;
                FolderFilter.ItemsSource = targetConfig.SourceFolders;

                ManagedFolder? targetFolder = null;
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    targetFolder = targetConfig.SourceFolders.FirstOrDefault(f => f.Path == folderPath);
                }

                FolderFilter.SelectedItem = targetFolder;

                if (targetFolder != null)
                {
                    RefreshHistory(targetConfig, targetFolder);
                }

                PersistHistorySelection(targetConfig, targetFolder);
            }
            finally
            {
                _isNavigating = false;
            }
        }

        private void ConfigFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 如果正在进行代码导航设置，直接跳过，不要干扰我的逻辑
            if (_isNavigating) return;

            if (ConfigFilter.SelectedItem is BackupConfig config)
            {
                FolderFilter.ItemsSource = config.SourceFolders;

                // 用户手动点击时，默认选中第一个
                if (config.SourceFolders.Count > 0)
                    FolderFilter.SelectedIndex = 0;
                else
                    FolderFilter.SelectedIndex = -1;

                PersistHistorySelection(config, null);
            }
        }

        private void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 确保两个都选中了才刷新
            if (FolderFilter.SelectedItem is ManagedFolder folder && ConfigFilter.SelectedItem is BackupConfig config)
            {
                RefreshHistory(config, folder);
                PersistHistorySelection(config, folder);
            }
        }

        private void RefreshHistory(BackupConfig config, ManagedFolder folder)
        {
            _currentAllItems.Clear();
            FilteredHistory.Clear();

            var items = HistoryService.GetHistoryForFolder(config, folder);
            foreach (var item in items)
            {
                _currentAllItems.Add(item);
            }

            ApplyCommentFilter();
        }

        private void ApplyCommentFilter()
        {
            FilteredHistory.Clear();

            IEnumerable<HistoryItem> query = _currentAllItems;
            var needle = (CommentFilterText ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(needle))
            {
                query = query.Where(i => (i.Comment ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            foreach (var item in query)
            {
                FilteredHistory.Add(item);
            }

            _missingCount = _currentAllItems.Count(i => i.IsMissing);
            OnPropertyChanged(nameof(HasMissing));

            IsEmpty = FilteredHistory.Count == 0;
            UpdateTimelineVisuals(FilteredHistory);
        }

        private static Brush TryGetThemeBrush(string key, Windows.UI.Color fallback)
        {
            try
            {
                if (Application.Current?.Resources != null && Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b)
                {
                    return b;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }

        private void UpdateTimelineVisuals(IEnumerable<HistoryItem> items)
        {
            var use = UseHistoryStatusColors;

            var offLine = TryGetThemeBrush("SystemControlForegroundBaseLowBrush", Colors.Gray);
            var offFill = TryGetThemeBrush("SystemControlBackgroundChromeMediumBrush", Colors.Transparent);
            var offBorder = TryGetThemeBrush("SystemControlForegroundBaseHighBrush", Colors.Gray);

            var ok = new SolidColorBrush(Colors.DodgerBlue);
            var bad = new SolidColorBrush(Colors.OrangeRed);
            var warn = new SolidColorBrush(Colors.Gold);
            var importantFill = new SolidColorBrush(Colors.Gold);

            foreach (var item in items)
            {
                if (!use)
                {
                    item.TimelineLineBrush = offLine;
                    item.TimelineNodeFillBrush = offFill;
                    item.TimelineNodeBorderBrush = offBorder;
                    continue;
                }

                // 优先级：缺失(OrangeRed) > 文件过小(Gold) > 正常(DodgerBlue)
                if (item.IsMissing)
                {
                    item.TimelineLineBrush = bad;
                    item.TimelineNodeBorderBrush = bad;
                }
                else if (item.IsSmallFile)
                {
                    item.TimelineLineBrush = warn;
                    item.TimelineNodeBorderBrush = warn;
                }
                else
                {
                    item.TimelineLineBrush = ok;
                    item.TimelineNodeBorderBrush = ok;
                }

                // 重要标记的备份使用填充色显示
                item.TimelineNodeFillBrush = item.IsImportant ? importantFill : offFill;
            }
        }

        /// <summary>
        /// 查看备份文件按钮点击 - 在资源管理器中打开文件
        /// </summary>
        private void OnViewClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            var config = ConfigFilter.SelectedItem as BackupConfig;
            var folder = FolderFilter.SelectedItem as ManagedFolder;
            if (config == null || folder == null) return;

            var filePath = HistoryService.GetBackupFilePath(config, folder, item);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                NotificationService.ShowWarning(I18n.GetString("History_ViewFile_PathEmpty"));
                return;
            }

            if (!File.Exists(filePath))
            {
                NotificationService.ShowWarning(I18n.Format("History_ViewFile_NotFound", Path.GetFileName(filePath)));
                return;
            }

            try
            {
                // 使用资源管理器打开并选中文件
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                NotificationService.ShowError(I18n.Format("History_ViewFile_Failed", ex.Message));
            }
        }

        /// <summary>
        /// 修改注释按钮点击
        /// </summary>
        private async void OnEditCommentClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            var inputBox = new TextBox
            {
                Text = item.Comment ?? string.Empty,
                PlaceholderText = I18n.GetString("History_EditComment_Placeholder"),
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 300
            };

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("History_EditComment_Title"),
                Content = inputBox,
                PrimaryButtonText = I18n.GetString("Common_Ok"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newComment = inputBox.Text?.Trim() ?? string.Empty;

            // 更新历史记录
            HistoryService.UpdateComment(item, newComment);

            // 触发 Message 属性更新（因为 Message 依赖 Comment）
            item.OnPropertyChanged(nameof(item.Message));

            // 刷新当前列表以确保UI更新
            var config = ConfigFilter.SelectedItem as BackupConfig;
            var folder = FolderFilter.SelectedItem as ManagedFolder;
            if (config != null && folder != null)
            {
                RefreshHistory(config, folder);
            }
        }

        /// <summary>
        /// 切换重要标记按钮点击
        /// </summary>
        private void OnToggleImportantClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            HistoryService.ToggleImportant(item);

            // 更新时间线视觉效果（重要标记影响节点填充色）
            UpdateTimelineVisuals(FilteredHistory);
        }

        // 还原按钮点击逻辑
        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            // 获取点击按钮所在的数据行
            if (sender is Button btn && btn.DataContext is HistoryItem item)
            {
                var config = ConfigFilter.SelectedItem as BackupConfig;
                var folder = FolderFilter.SelectedItem as ManagedFolder;

                if (config == null || folder == null) return;

                // 如果是加密配置，先要求输入密码
                if (config.IsEncrypted)
                {
                    bool passwordVerified = await PromptAndVerifyPasswordAsync(config);
                    if (!passwordVerified) return;
                }

                // 弹出确认对话框
                var dialog = new ContentDialog
                {
                    Title = I18n.GetString("History_RestoreConfirm_Title"),
                    Content = new TextBlock
                    {
                        Text = I18n.Format("History_RestoreConfirm_Content", item.TimeDisplay, item.Comment ?? string.Empty),
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = I18n.GetString("History_RestoreConfirm_Primary"),
                    SecondaryButtonText = I18n.GetString("History_RestoreConfirm_Secondary"),
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot // 必须设置 XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    await BackupService.RestoreBackupAsync(config, folder, item, BackupService.RestoreMode.Clean);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    await BackupService.RestoreBackupAsync(config, folder, item, BackupService.RestoreMode.Overwrite);
                }
            }
        }

        /// <summary>
        /// 弹出密码验证对话框，验证用户输入的密码是否正确。
        /// </summary>
        private async System.Threading.Tasks.Task<bool> PromptAndVerifyPasswordAsync(BackupConfig config)
        {
            var passwordBox = new PasswordBox
            {
                PlaceholderText = I18n.GetString("Encryption_EnterPasswordPlaceholder")
            };

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Encryption_RestorePasswordTitle"),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = I18n.GetString("Encryption_RestorePasswordDesc"),
                            TextWrapping = TextWrapping.Wrap
                        },
                        passwordBox
                    }
                },
                PrimaryButtonText = I18n.GetString("Common_Ok"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return false;

            if (!EncryptionService.VerifyPassword(config.Id, passwordBox.Password))
            {
                var failDialog = new ContentDialog
                {
                    Title = I18n.GetString("Encryption_WrongPasswordTitle"),
                    Content = I18n.GetString("Encryption_WrongPasswordDesc"),
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(failDialog);
                await failDialog.ShowAsync();
                return false;
            }

            return true;
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            var config = ConfigFilter.SelectedItem as BackupConfig;
            var folder = FolderFilter.SelectedItem as ManagedFolder;
            if (config == null || folder == null) return;

            // 如果是重要备份，先额外警告
            if (item.IsImportant)
            {
                var warnDialog = new ContentDialog
                {
                    Title = I18n.GetString("History_DeleteImportant_Title"),
                    Content = new TextBlock
                    {
                        Text = I18n.GetString("History_DeleteImportant_Content"),
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = I18n.GetString("History_DeleteImportant_Continue"),
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var warnResult = await warnDialog.ShowAsync();
                if (warnResult != ContentDialogResult.Primary) return;
            }

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("History_DeleteConfirm_Title"),
                Content = new TextBlock
                {
                    Text = I18n.Format("History_DeleteConfirm_Content", item.FileName),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = I18n.GetString("History_DeleteConfirm_DeleteRecord"),
                SecondaryButtonText = I18n.GetString("History_DeleteConfirm_DeleteBoth"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                return;
            }

            bool deleteFile = result == ContentDialogResult.Secondary;
            if (deleteFile)
            {
                try
                {
                    var backupFilePath = HistoryService.GetBackupFilePath(config, folder, item);
                    if (!string.IsNullOrWhiteSpace(backupFilePath) && File.Exists(backupFilePath))
                    {
                        File.Delete(backupFilePath);
                    }
                }
                catch
                {
                }
            }

            try
            {
                HistoryService.RemoveEntry(item);
            }
            catch
            {
            }

            FilteredHistory.Remove(item);
            IsEmpty = FilteredHistory.Count == 0;
        }

        private void CommentFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                CommentFilterText = tb.Text;
            }
        }

        private void UseColorsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                UseHistoryStatusColors = ts.IsOn;
            }
        }

        private async void OnClearMissingClick(object sender, RoutedEventArgs e)
        {
            var config = ConfigFilter.SelectedItem as BackupConfig;
            var folder = FolderFilter.SelectedItem as ManagedFolder;
            if (config == null || folder == null) return;

            var missingCount = _currentAllItems.Count(i => i.IsMissing);
            if (missingCount <= 0) return;

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("History_ClearMissingConfirm_Title"),
                Content = new TextBlock
                {
                    Text = I18n.Format("History_ClearMissingConfirm_Content", missingCount),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = I18n.GetString("History_ClearMissingConfirm_Primary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try
            {
                HistoryService.RemoveMissingEntries(config, folder);
            }
            catch
            {
            }

            RefreshHistory(config, folder);
        }

        /// <summary>
        /// "重建历史" 按钮点击事件：通过扫描备份文件夹恢复丢失的历史记录
        /// </summary>
        private async void OnScanRecoverClick(object sender, RoutedEventArgs e)
        {
            var config = ConfigFilter.SelectedItem as BackupConfig;
            var folder = FolderFilter.SelectedItem as ManagedFolder;
            if (config == null || folder == null)
            {
                NotificationService.ShowWarning(I18n.GetString("History_ScanRecover_SelectFirst"));
                return;
            }

            // 使用 FolderPicker 让用户选择要扫描的文件夹
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            // WinUI 3 需要通过窗口句柄初始化 Picker
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var selectedFolder = await picker.PickSingleFolderAsync();
            if (selectedFolder == null) return;

            string scanPath = selectedFolder.Path;

            // 在后台线程执行扫描，避免阻塞 UI
            int recovered = await System.Threading.Tasks.Task.Run(() =>
                HistoryService.ScanAndRecoverHistory(scanPath, config, folder));

            if (recovered > 0)
            {
                NotificationService.ShowSuccess(
                    I18n.Format("History_ScanRecover_ResultSuccess", recovered.ToString()));
                RefreshHistory(config, folder);
            }
            else
            {
                NotificationService.ShowInfo(
                    I18n.GetString("History_ScanRecover_ResultNone"));
            }
        }

        private void RestoreLastSelection()
        {
            if (_isNavigating) return;

            var settings = Settings;
            if (settings == null) return;
            if (Configs.Count == 0) return;

            _isNavigating = true;
            try
            {
                var config = Configs.FirstOrDefault(c => !string.IsNullOrWhiteSpace(settings.LastHistoryConfigId) && c.Id == settings.LastHistoryConfigId)
                             ?? Configs.FirstOrDefault();

                ConfigFilter.SelectedItem = config;
                FolderFilter.ItemsSource = config?.SourceFolders;

                ManagedFolder folder = null;
                if (config != null && !string.IsNullOrWhiteSpace(settings.LastHistoryFolderPath))
                {
                    folder = config.SourceFolders.FirstOrDefault(f => f.Path == settings.LastHistoryFolderPath);
                }

                if (folder == null && config?.SourceFolders.Count > 0)
                {
                    folder = config.SourceFolders[0];
                }

                FolderFilter.SelectedItem = folder;

                if (config != null && folder != null)
                {
                    RefreshHistory(config, folder);
                }

                PersistHistorySelection(config, folder);
            }
            finally
            {
                _isNavigating = false;
            }
        }

        private void PersistHistorySelection(BackupConfig? config, ManagedFolder? folder)
        {
            var settings = Settings;
            if (settings == null) return;

            bool updated = false;

            if (config != null && settings.LastHistoryConfigId != config.Id)
            {
                settings.LastHistoryConfigId = config.Id;
                updated = true;
            }

            if (folder != null && settings.LastHistoryFolderPath != folder.Path)
            {
                settings.LastHistoryFolderPath = folder.Path;
                updated = true;
            }

            if (updated)
            {
                ConfigService.Save();
            }
        }
    }
}