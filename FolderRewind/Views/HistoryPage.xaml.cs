using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

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
            if (_isNavigating)
            {
                return;
            }

            if (ConfigFilter.SelectedItem is BackupConfig config)
            {
                ViewModel.SetCurrentSelection(config, null, refreshHistoryIfFolder: false, persistSelection: true);
                FolderFilter.ItemsSource = config.SourceFolders;

                if (config.SourceFolders.Count > 0)
                {
                    FolderFilter.SelectedIndex = 0;
                }
                else
                {
                    FolderFilter.SelectedIndex = -1;
                }
            }
        }

        private void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FolderFilter.SelectedItem is ManagedFolder folder && ConfigFilter.SelectedItem is BackupConfig config)
            {
                ViewModel.SetCurrentSelection(config, folder, refreshHistoryIfFolder: true, persistSelection: true);
            }
        }

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

        private void OnToggleImportantClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            ViewModel.ToggleImportant(item);
        }

        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            if (!TryGetSelectedContext(persistSelection: false, out var config, out var folder))
            {
                return;
            }

            if (config.IsEncrypted)
            {
                var passwordVerified = await PromptAndVerifyPasswordAsync(config);
                if (!passwordVerified)
                {
                    return;
                }
            }

            var restoreMode = await PromptRestoreModeAsync(item);
            if (restoreMode == null)
            {
                return;
            }

            await BackupService.RestoreBackupAsync(config, folder, item, restoreMode.Value);
        }

        private async Task<BackupService.RestoreMode?> PromptRestoreModeAsync(HistoryItem item)
        {
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
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return BackupService.RestoreMode.Clean;
            }

            if (result == ContentDialogResult.Secondary)
            {
                return BackupService.RestoreMode.Overwrite;
            }

            return null;
        }

        private async Task<bool> PromptAndVerifyPasswordAsync(BackupConfig config)
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

            if (item.IsImportant && !await ConfirmDeleteImportantAsync())
            {
                return;
            }

            var deleteMode = await PromptDeleteModeAsync(item);
            if (deleteMode == null)
            {
                return;
            }

            var deleteResult = await ViewModel.DeleteHistoryItemAsync(item, deleteMode.Value);
            if (!deleteResult.Success)
            {
                NotificationService.ShowError(string.IsNullOrWhiteSpace(deleteResult.Message)
                    ? I18n.GetString("BackupService_Task_Failed")
                        : deleteResult.Message);
                return;
            }
        }

        private async Task<bool> ConfirmDeleteImportantAsync()
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

            var result = await warnDialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async Task<BackupDeleteMode?> PromptDeleteModeAsync(HistoryItem item)
        {
            var recordOnlyRadio = new RadioButton
            {
                Content = I18n.GetString("History_DeleteMode_RecordOnly"),
                IsChecked = !item.HasLocalFile
            };

            var localOnlyRadio = new RadioButton
            {
                Content = I18n.GetString("History_DeleteMode_LocalOnly"),
                IsEnabled = item.HasLocalFile,
                IsChecked = item.HasLocalFile
            };

            var localAndRecordRadio = new RadioButton
            {
                Content = I18n.GetString("History_DeleteMode_LocalAndRecord"),
                IsEnabled = item.HasLocalFile
            };

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("History_DeleteConfirm_Title"),
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = I18n.Format("History_DeleteConfirm_Content", item.FileName),
                            TextWrapping = TextWrapping.Wrap
                        },
                        recordOnlyRadio,
                        localOnlyRadio,
                        localAndRecordRadio
                    }
                },
                PrimaryButtonText = I18n.GetString("Common_Ok"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (localOnlyRadio.IsChecked == true)
                {
                    return BackupDeleteMode.LocalArchiveOnly;
                }

                if (localAndRecordRadio.IsChecked == true)
                {
                    return BackupDeleteMode.LocalArchiveAndRecord;
                }

                return BackupDeleteMode.RecordOnly;
            }

            return null;
        }

        private async void OnUploadToCloudClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            if (!TryGetSelectedContext(persistSelection: false, out _, out _))
            {
                return;
            }

            await ViewModel.UploadToCloudAsync(item);
        }

        private async void OnDownloadFromCloudClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not HistoryItem item)
            {
                return;
            }

            if (!TryGetSelectedContext(persistSelection: false, out _, out _))
            {
                return;
            }

            await ViewModel.DownloadFromCloudAsync(item);
        }

        private async void OnOpenCloudSyncClick(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedContext(persistSelection: false, out var config, out _))
            {
                NotificationService.ShowWarning(I18n.GetString("History_ScanRecover_SelectFirst"));
                return;
            }

            var dialog = new ConfigCloudSyncDialog(config)
            {
                XamlRoot = MainWindowService.GetXamlRoot() ?? this.XamlRoot
            };

            await TemplateDialogCoordinatorService.ShowAsync(dialog, this.XamlRoot);
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

        private async void OnScanRecoverClick(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelectedContext(persistSelection: false, out _, out _))
            {
                NotificationService.ShowWarning(I18n.GetString("History_ScanRecover_SelectFirst"));
                return;
            }

            var scanPath = await PickScanRecoverFolderPathAsync();
            if (string.IsNullOrWhiteSpace(scanPath))
            {
                return;
            }

            var recovered = await Task.Run(() => ViewModel.ScanAndRecoverHistory(scanPath));

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

        private async Task<string?> PickScanRecoverFolderPathAsync()
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            MainWindowService.InitializePicker(picker);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
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
