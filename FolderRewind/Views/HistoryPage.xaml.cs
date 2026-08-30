using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Threading.Tasks;

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
            RunHistoryList.ItemsSource = ViewModel.FilteredRuns;
            BranchFilter.ItemsSource = ViewModel.Branches;
            UseColorsToggleMenuItem.IsChecked = ViewModel.UseHistoryStatusColors;
            HistoryViewSelector.SelectedItem = ViewModel.IsGroupedRunView
                ? RunHistoryViewItem
                : SourceHistoryViewItem;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ManagerNavigationParameter managerParam)
            {
                await ApplySelectionFromNavigationAsync(managerParam.ConfigId, managerParam.FolderPath);
                return;
            }

            if (e.Parameter is ManagedFolder folder)
            {
                await ApplySelectionFromNavigationAsync(null, folder.Path);
                return;
            }

            await RestoreLastSelectionAsync();
        }

        private async Task ApplySelectionFromNavigationAsync(string? configId, string? folderPath)
        {
            _isNavigating = true;

            try
            {
                if (!ViewModel.TryResolveSelection(configId, folderPath, out var targetConfig, out var targetFolder) || targetConfig == null)
                {
                    return;
                }

                ConfigFilter.SelectedItem = targetConfig;
                ConfigureFolderFilter(targetConfig, targetFolder);

                _ = await TrySetCurrentSelectionAsync(
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

        private async void ConfigFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isNavigating)
            {
                return;
            }

            if (ConfigFilter.SelectedItem is BackupConfig config)
            {
                _isNavigating = true;
                try
                {
                    ConfigureFolderFilter(config, null);
                    var folder = FolderFilter.SelectedItem as ManagedFolder;
                    _ = await TrySetCurrentSelectionAsync(
                        config,
                        folder,
                        refreshHistoryIfFolder: folder is not null,
                        persistSelection: true);
                }
                finally
                {
                    _isNavigating = false;
                }
            }
        }

        private async void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isNavigating) return;
            if (ViewModel.IsGroupedRunView) return;
            if (FolderFilter.SelectedItem is ManagedFolder folder
                && ConfigFilter.SelectedItem is BackupConfig config)
            {
                _isNavigating = true;
                try
                {
                    _ = await TrySetCurrentSelectionAsync(
                        config,
                        folder,
                        refreshHistoryIfFolder: true,
                        persistSelection: true);
                }
                finally
                {
                    _isNavigating = false;
                }
            }
        }

        private void OnViewClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
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
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
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
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var newComment = inputBox.Text?.Trim() ?? string.Empty;

            ViewModel.UpdateComment(item, newComment);
            ViewModel.RefreshCurrentHistory();
        }

        private void OnToggleImportantClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
            {
                return;
            }

            ViewModel.ToggleImportant(item);
        }

        private void OnHistoryViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is not string tag) return;
            ViewModel.SetHistoryViewMode(string.Equals(tag, "Run", StringComparison.OrdinalIgnoreCase)
                ? HistoryViewMode.ByRun
                : HistoryViewMode.PerSource);
            if (ConfigFilter.SelectedItem is BackupConfig config)
            {
                ViewModel.TryGetCurrentSelection(out _, out var currentFolder);
                ConfigureFolderFilter(config, currentFolder);
            }
        }

        private async void OnCreateBranchClick(object sender, RoutedEventArgs e)
        {
            var name = await PromptBranchNameAsync(I18n.GetString("History_Branch_CreateTitle"), string.Empty);
            if (name is null) return;
            if (!await ViewModel.CreateBranchAtLatestCheckpointAsync(name))
                NotificationService.ShowWarning(I18n.GetString("History_Branch_NoCheckpoint"));
            else
                ViewModel.RefreshCurrentHistory();
        }

        private async void OnRenameBranchClick(object sender, RoutedEventArgs e)
        {
            if (BranchFilter.SelectedItem is not BranchViewItem branch || !branch.CanRename) return;
            var name = await PromptBranchNameAsync(I18n.GetString("History_Branch_RenameTitle"), branch.Name);
            if (name is not null && await ViewModel.RenameBranchAsync(branch, name))
                ViewModel.RefreshCurrentHistory();
        }

        private async void OnDeleteBranchClick(object sender, RoutedEventArgs e)
        {
            if (BranchFilter.SelectedItem is BranchViewItem branch && branch.CanDelete)
            {
                if (await ViewModel.DeleteBranchAsync(branch)) ViewModel.RefreshCurrentHistory();
            }
        }

        private async void OnCheckoutBranchClick(object sender, RoutedEventArgs e)
        {
            if (BranchFilter.SelectedItem is not BranchViewItem branch || !branch.CanCheckout) return;
            BranchUpdateId? selected = branch.IsMultiTip ? await PromptBranchTipAsync(branch) : branch.Tips.Single().UpdateId;
            if (selected is null) return;
            var confirm = new ContentDialog
            {
                Title = I18n.GetString("History_Branch_CheckoutTitle"),
                Content = new TextBlock
                {
                    Text = I18n.GetString("History_Branch_CheckoutContent"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = I18n.GetString("History_Branch_CheckoutPrimary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            if (!await ViewModel.CheckoutBranchTipAsync(branch, selected.Value))
                NotificationService.ShowWarning(I18n.GetString("History_NativeAction_NotAvailable"));
            else
                ViewModel.RefreshCurrentHistory();
        }

        private async Task<string?> PromptBranchNameAsync(string title, string initial)
        {
            var input = new TextBox { Text = initial, MinWidth = 260 };
            var dialog = new ContentDialog { Title = title, Content = input, PrimaryButtonText = I18n.GetString("Common_Ok"), CloseButtonText = I18n.GetString("Common_Cancel"), XamlRoot = XamlRoot };
            ThemeService.ApplyThemeToDialog(dialog);
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
        }

        private async Task<BranchUpdateId?> PromptBranchTipAsync(BranchViewItem branch)
        {
            var choices = new ComboBox { ItemsSource = branch.Tips, DisplayMemberPath = "UpdateId", MinWidth = 300, SelectedIndex = 0 };
            var dialog = new ContentDialog { Title = I18n.GetString("History_Branch_SelectTipTitle"), Content = choices, PrimaryButtonText = I18n.GetString("Common_Ok"), CloseButtonText = I18n.GetString("Common_Cancel"), XamlRoot = XamlRoot };
            ThemeService.ApplyThemeToDialog(dialog);
            return await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is BranchUpdate tip ? tip.UpdateId : null;
        }

        private async void OnEditRunCommentClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not BackupRunViewItem item) return;
            var inputBox = new TextBox
            {
                Text = item.Comment,
                PlaceholderText = I18n.GetString("History_EditComment_Placeholder"),
                MinWidth = 300
            };
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("History_EditComment_Title"),
                Content = inputBox,
                PrimaryButtonText = I18n.GetString("Common_Ok"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.UpdateRunComment(item, inputBox.Text?.Trim() ?? string.Empty);
        }

        private void OnToggleRunImportantClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: BackupRunViewItem item })
                ViewModel.ToggleRunImportant(item);
        }

        private async void OnRestoreRunClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: BackupRunViewItem item }
                || !ViewModel.TryGetCurrentConfig(out var config) || config == null) return;
            if (config.IsEncrypted && !await PromptAndVerifyPasswordAsync(config)) return;
            var mode = await PromptRunRestoreModeAsync(item);
            if (mode == null) return;
            var result = await ViewModel.RestoreRunAsync(item, mode.Value);
            if (result == null) return;
            if (!result.Succeeded)
            {
                NotificationService.ShowWarning(string.IsNullOrWhiteSpace(result.Diagnostic)
                    ? I18n.GetString("History_NativeAction_NotAvailable")
                    : result.Diagnostic);
                return;
            }
            var succeeded = result.AppliedSources.Count;
            var failed = Math.Max(0, item.Sources.Count - succeeded);
            if (failed == 0)
                NotificationService.ShowSuccess(I18n.Format("History_Run_RestoreSummary", succeeded, failed));
            else
                NotificationService.ShowWarning(I18n.Format("History_Run_RestoreSummary", succeeded, failed));
        }

        private async Task<BackupService.RestoreMode?> PromptRunRestoreModeAsync(BackupRunViewItem item)
        {
            var partial = item.HasPartialBackup;
            var dialog = new ContentDialog
            {
                Title = partial ? I18n.GetString("History_PartialRestore_Title") : I18n.GetString("History_Run_RestoreTitle"),
                Content = new TextBlock
                {
                    Text = partial
                        ? I18n.GetString("History_PartialRestore_Content")
                        : I18n.GetString("History_Run_RestoreContent"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = partial
                    ? I18n.GetString("History_PartialRestore_Primary")
                    : I18n.GetString("History_RestoreConfirm_Primary"),
                SecondaryButtonText = partial ? string.Empty : I18n.GetString("History_RestoreConfirm_Secondary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                return partial ? BackupService.RestoreMode.Overwrite : BackupService.RestoreMode.Clean;
            return result == ContentDialogResult.Secondary ? BackupService.RestoreMode.Overwrite : null;
        }

        private async void OnDeleteRunClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: BackupRunViewItem item }) return;
            if (item.IsImportant && !await ConfirmDeleteImportantAsync()) return;
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("History_Run_DeleteTitle"),
                Content = I18n.GetString("History_Run_DeleteContent"),
                PrimaryButtonText = I18n.GetString("Common_Delete"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (!await ViewModel.DeleteRunAsync(item))
                NotificationService.ShowError(I18n.GetString("History_Run_DeleteFailed"));
        }

        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
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

            var result = await ViewModel.RestoreVersionAsync(item, restoreMode.Value);
            if (result is null || !result.Succeeded)
            {
                NotificationService.ShowWarning(string.IsNullOrWhiteSpace(result?.Diagnostic)
                    ? I18n.GetString("History_NativeAction_NotAvailable")
                    : result.Diagnostic);
            }
        }

        private async Task<BackupService.RestoreMode?> PromptRestoreModeAsync(NativeHistoryVersionViewItem item)
        {
            bool isPartialBackup = item.IsPartialBackup;
            var dialog = new ContentDialog
            {
                Title = isPartialBackup
                    ? I18n.GetString("History_PartialRestore_Title")
                    : I18n.GetString("History_RestoreConfirm_Title"),
                Content = new TextBlock
                {
                    Text = isPartialBackup
                        ? I18n.GetString("History_PartialRestore_Content")
                        : I18n.Format("History_RestoreConfirm_Content", item.TimeDisplay, item.Comment ?? string.Empty),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = isPartialBackup
                    ? I18n.GetString("History_PartialRestore_Primary")
                    : I18n.GetString("History_RestoreConfirm_Primary"),
                SecondaryButtonText = isPartialBackup
                    ? string.Empty
                    : I18n.GetString("History_RestoreConfirm_Secondary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                return isPartialBackup
                    ? BackupService.RestoreMode.Overwrite
                    : BackupService.RestoreMode.Clean;
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
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
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

            var deleteResult = await ViewModel.DeleteVersionAsync(item, deleteMode.Value);
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

        private async Task<BackupDeleteMode?> PromptDeleteModeAsync(NativeHistoryVersionViewItem item)
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
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
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
            if (sender is not Button btn || btn.DataContext is not NativeHistoryVersionViewItem item)
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
            if (ConfigFilter.SelectedItem is not BackupConfig config)
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
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try
            {
                await ViewModel.ClearMissingEntriesAsync();
            }
            catch (Exception ex)
            {
                LogService.LogError($"[HistoryPage] Local replica cleanup failed: {ex.Message}", nameof(HistoryPage), ex);
                NotificationService.ShowError(ex.Message);
                return;
            }
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

            int recovered;
            try
            {
                recovered = await ViewModel.ScanAndRecoverHistoryAsync(scanPath);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[HistoryPage] Native archive recovery failed: {ex.Message}", nameof(HistoryPage), ex);
                NotificationService.ShowError(ex.Message);
                return;
            }

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

        private Task<string?> PickScanRecoverFolderPathAsync()
        {
            return MainWindowService.PickFolderPathAsync(
                string.Empty,
                "FolderRewind.History.ScanRecover",
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
        }

        private async Task RestoreLastSelectionAsync()
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
                ConfigureFolderFilter(config, folder);

                _ = await TrySetCurrentSelectionAsync(
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

            if (!ViewModel.TryGetCurrentSelection(out var currentConfig, out var currentFolder)
                || currentConfig is null
                || currentFolder is null
                || !string.Equals(currentConfig.Id, config.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentFolder.Id, folder.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (persistSelection)
                ViewModel.SetCurrentSelection(config, folder, refreshHistoryIfFolder: false, persistSelection: true);
            return true;
        }

        private async Task<bool> TrySetCurrentSelectionAsync(
            BackupConfig config,
            ManagedFolder? folder,
            bool refreshHistoryIfFolder,
            bool persistSelection)
        {
            ConfigFilter.IsEnabled = false;
            FolderFilter.IsEnabled = false;
            try
            {
                _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
                ViewModel.SetCurrentSelection(config, folder, refreshHistoryIfFolder, persistSelection);
                return true;
            }
            catch (Exception ex)
            {
                ViewModel.ClearCurrentSelection();
                var message = I18n.Format("History_NativeInitializationFailed", config.Name, ex.Message);
                LogService.LogError(message, nameof(HistoryPage), ex);
                NotificationService.ShowError(message);
                return false;
            }
            finally
            {
                ConfigFilter.IsEnabled = true;
                FolderFilter.IsEnabled = !ViewModel.IsGroupedRunView;
            }
        }

        private void ConfigureFolderFilter(BackupConfig config, ManagedFolder? preferredFolder)
        {
            var grouped = ViewModel.IsGroupedRunView;
            FolderFilter.IsEnabled = !grouped;
            FolderFilter.PlaceholderText = grouped ? I18n.GetString("History_Run_AllSources") : string.Empty;
            FolderFilter.ItemsSource = grouped ? null : config.SourceFolders;
            FolderFilter.SelectedItem = grouped ? null : preferredFolder;
            if (!grouped && preferredFolder == null)
                FolderFilter.SelectedIndex = config.SourceFolders.Count > 0 ? 0 : -1;
            ScanRecoverMenuItem.IsEnabled = !grouped;
        }
    }
}
