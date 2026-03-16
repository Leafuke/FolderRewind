using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderRewind.Views
{
    public sealed partial class HistoryPage : Page
    {
        public HistoryPageViewModel ViewModel { get; } = new();

        private bool _isNavigating;

        public HistoryPage()
        {
            this.InitializeComponent();

            ViewModel.Initialize();

            // 历史页在早期版本中遇到过首次导航时绑定晚于控件创建的问题，
            // 这里保留一次显式赋值，确保下拉框与列表首次进入可见。
            ConfigFilter.ItemsSource = ViewModel.Configs;
            HistoryList.ItemsSource = ViewModel.FilteredHistory;
            UseColorsToggle.IsOn = ViewModel.UseHistoryStatusColors;
        }

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
                if (!ViewModel.TryResolveSelection(configId, folderPath, out var targetConfig, out var targetFolder) || targetConfig == null)
                {
                    return;
                }

                ConfigFilter.SelectedItem = targetConfig;
                FolderFilter.ItemsSource = targetConfig.SourceFolders;
                FolderFilter.SelectedItem = targetFolder;

                ViewModel.SetCurrentSelection(
                    targetConfig,
                    targetFolder,
                    refreshHistoryIfFolder: targetFolder != null,
                    persistSelection: true);
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
                ViewModel.SetCurrentSelection(config, null, refreshHistoryIfFolder: false, persistSelection: true);
                FolderFilter.ItemsSource = config.SourceFolders;

                // 用户手动点击时，默认选中第一个
                if (config.SourceFolders.Count > 0)
                    FolderFilter.SelectedIndex = 0;
                else
                    FolderFilter.SelectedIndex = -1;
            }
        }

        private void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 确保两个都选中了才刷新
            if (FolderFilter.SelectedItem is ManagedFolder folder && ConfigFilter.SelectedItem is BackupConfig config)
            {
                ViewModel.SetCurrentSelection(config, folder, refreshHistoryIfFolder: true, persistSelection: true);
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

            if (!TryGetSelectedContext(persistSelection: false, out _, out _))
            {
                return;
            }

            if (!ViewModel.TryRevealBackupFile(item, out var errorMessage))
            {
                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    NotificationService.ShowWarning(errorMessage);
                }
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

            ViewModel.UpdateComment(item, newComment);
            ViewModel.RefreshCurrentHistory();
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

            ViewModel.ToggleImportant(item);
        }

        // 还原按钮点击逻辑
        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            // 获取点击按钮所在的数据行
            if (sender is Button btn && btn.DataContext is HistoryItem item)
            {
                if (!TryGetSelectedContext(persistSelection: false, out var config, out var folder))
                {
                    return;
                }

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

            if (!TryGetSelectedContext(persistSelection: false, out var config, out var folder))
            {
                return;
            }

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
                ThemeService.ApplyThemeToDialog(warnDialog);

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
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                return;
            }

            bool deleteFile = result == ContentDialogResult.Secondary;
            var deleteResult = await BackupService.DeleteBackupAsync(config, folder, item, deleteFile);
            if (!deleteResult.Success)
            {
                NotificationService.ShowError(string.IsNullOrWhiteSpace(deleteResult.Message)
                    ? I18n.GetString("BackupService_Task_Failed")
                    : deleteResult.Message);
                return;
            }

            ViewModel.RefreshCurrentHistory();
        }

        private void CommentFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                ViewModel.CommentFilterText = tb.Text;
            }
        }

        private void UseColorsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.UseHistoryStatusColors = ts.IsOn;
            }
        }

        private async void OnClearMissingClick(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedContext(persistSelection: false, out _, out _))
            {
                return;
            }

            var missingCount = ViewModel.GetMissingCount();
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

            ViewModel.ClearMissingEntries();
            ViewModel.RefreshCurrentHistory();
        }

        /// <summary>
        /// "重建历史" 按钮点击事件：通过扫描备份文件夹恢复丢失的历史记录
        /// </summary>
        private async void OnScanRecoverClick(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedContext(persistSelection: false, out _, out _))
            {
                NotificationService.ShowWarning(I18n.GetString("History_ScanRecover_SelectFirst"));
                return;
            }

            // 使用 FolderPicker 让用户选择要扫描的文件夹
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            // WinUI 3 需要通过窗口句柄初始化 Picker
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            var selectedFolder = await picker.PickSingleFolderAsync();
            if (selectedFolder == null) return;

            string scanPath = selectedFolder.Path;

            // 在后台线程执行扫描，避免阻塞 UI
            int recovered = await Task.Run(() => ViewModel.ScanAndRecoverHistory(scanPath));

            if (recovered > 0)
            {
                NotificationService.ShowSuccess(
                    I18n.Format("History_ScanRecover_ResultSuccess", recovered.ToString()));
                ViewModel.RefreshCurrentHistory();
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

            if (!ViewModel.TryResolveLastSelection(out var config, out var folder) || config == null)
            {
                return;
            }

            _isNavigating = true;
            try
            {
                ConfigFilter.SelectedItem = config;
                FolderFilter.ItemsSource = config?.SourceFolders;
                FolderFilter.SelectedItem = folder;

                ViewModel.SetCurrentSelection(
                    config,
                    folder,
                    refreshHistoryIfFolder: folder != null,
                    persistSelection: true);
            }
            finally
            {
                _isNavigating = false;
            }
        }

        private bool TryGetSelectedContext(bool persistSelection, out BackupConfig config, out ManagedFolder folder)
        {
            config = ConfigFilter.SelectedItem as BackupConfig ?? null!;
            folder = FolderFilter.SelectedItem as ManagedFolder ?? null!;
            if (config == null || folder == null)
            {
                return false;
            }

            // 统一从筛选器同步当前上下文，避免后续按钮操作拿到旧选择。
            ViewModel.SetCurrentSelection(config, folder, refreshHistoryIfFolder: false, persistSelection: persistSelection);
            return true;
        }
    }
}