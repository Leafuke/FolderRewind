using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings
{
    public sealed partial class DataManagementControl : UserControl
    {
        private enum DataTransferLocation
        {
            Local,
            Cloud
        }

        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public DataManagementControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
        }

        private async void OnExportConfigClick(object sender, RoutedEventArgs e)
        {
            var location = await PromptDataTransferLocationAsync(
                I18n.GetString("Settings_ExportConfigMode_Title"),
                I18n.GetString("Settings_ExportConfigMode_Description"));
            if (location == null)
            {
                return;
            }

            if (location == DataTransferLocation.Cloud)
            {
                var remoteBasePath = await PromptCloudRemoteBasePathAsync(
                    I18n.GetString("Settings_ExportConfigToCloud_Title"),
                    I18n.GetString("Settings_ExportConfigToCloud_Description"));
                if (string.IsNullOrWhiteSpace(remoteBasePath))
                {
                    return;
                }

                var cloudResult = await CloudSyncService.ExportConfigToCloudAsync(remoteBasePath);
                ShowInfoBar(cloudResult.Message, cloudResult.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                return;
            }

            var filePath = await MainWindowService.PickSaveFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.DataManagement.ExportConfig",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["JSON"] = new ReadOnlyCollection<string>(new[] { ".json" })
                },
                "FolderRewind_config",
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            bool ok = ConfigService.ExportConfig(filePath);
            if (ok)
                ShowInfoBar(I18n.GetString("Settings_ExportConfigSuccess"), InfoBarSeverity.Success);
            else
                ShowInfoBar(I18n.GetString("Settings_ExportConfigFailed"), InfoBarSeverity.Error);
        }

        private async void OnImportConfigClick(object sender, RoutedEventArgs e)
        {
            var location = await PromptDataTransferLocationAsync(
                I18n.GetString("Settings_ImportConfigMode_Title"),
                I18n.GetString("Settings_ImportConfigMode_Description"));
            if (location == null)
            {
                return;
            }

            var confirm = new ContentDialog
            {
                Title = I18n.GetString("Settings_ImportConfigConfirmTitle"),
                Content = new TextBlock { Text = I18n.GetString("Settings_ImportConfigConfirmContent"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (location == DataTransferLocation.Cloud)
            {
                var remoteBasePath = await PromptCloudRemoteBasePathAsync(
                    I18n.GetString("Settings_ImportConfigFromCloud_Title"),
                    I18n.GetString("Settings_ImportConfigFromCloud_Description"));
                if (string.IsNullOrWhiteSpace(remoteBasePath))
                {
                    return;
                }

                var cloudResult = await CloudSyncService.ImportConfigFromCloudAsync(remoteBasePath);
                ShowInfoBar(cloudResult.Message, cloudResult.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                return;
            }

            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.DataManagement.ImportConfig",
                new[] { ".json" },
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            bool ok = ConfigService.ImportConfig(filePath);
            if (ok)
            {
                ShowInfoBar(I18n.GetString("Settings_ImportConfigSuccess"), InfoBarSeverity.Success);
            }
            else
            {
                ShowInfoBar(I18n.GetString("Settings_ImportConfigFailed"), InfoBarSeverity.Error);
            }
        }

        private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var templates = TemplateService.GetTemplates()
                    .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (templates.Count == 0)
                {
                    ShowInfoBar(I18n.GetString("Settings_Template_Export_NoTemplates"), InfoBarSeverity.Warning);
                    return;
                }

                var templateCombo = new ComboBox
                {
                    Header = I18n.GetString("Settings_Template_SelectToExport"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                foreach (var template in templates)
                {
                    templateCombo.Items.Add(new ComboBoxItem { Content = template.Name, Tag = template });
                }
                templateCombo.SelectedIndex = 0;

                var chooseDialog = new ContentDialog
                {
                    Title = I18n.GetString("Settings_Template_SelectToExport"),
                    Content = templateCombo,
                    PrimaryButtonText = I18n.GetString("Common_Confirm"),
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(chooseDialog);

                if (await chooseDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                var selectedTemplate = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as ConfigTemplate;
                if (selectedTemplate == null)
                {
                    ShowInfoBar(I18n.GetString("Template_Export_TemplateNotFound"), InfoBarSeverity.Error);
                    return;
                }

                var filePath = await MainWindowService.PickSaveFilePathAsync(
                    string.Empty,
                    "FolderRewind.Settings.DataManagement.ExportTemplate",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["FolderRewind Template"] = new ReadOnlyCollection<string>(new[] { TemplateService.ShareFileExtension })
                    },
                    $"FolderRewind_template_{SanitizeFileName(selectedTemplate.Name)}",
                    MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
                if (string.IsNullOrWhiteSpace(filePath)) return;

                var ok = TemplateService.ExportTemplate(selectedTemplate.Id, filePath, out var message);
                ShowInfoBar(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[DataManagementControl] Export template failed: {ex.Message}", nameof(DataManagementControl), ex);
                ShowInfoBar(I18n.Format("Template_Export_Failed", ex.Message), InfoBarSeverity.Error);
            }
        }

        private async void OnImportTemplateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var filePath = await MainWindowService.PickFilePathAsync(
                    string.Empty,
                    "FolderRewind.Settings.DataManagement.ImportTemplate",
                    new[] { ".json" },
                    MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
                if (string.IsNullOrWhiteSpace(filePath)) return;

                if (!File.Exists(filePath))
                {
                    ShowInfoBar(I18n.GetString("Template_Import_FileNotFound"), InfoBarSeverity.Error);
                    LogService.LogWarning($"[DataManagementControl] Import template file not found: {filePath}", nameof(DataManagementControl));
                    return;
                }

                var inspection = TemplateService.InspectImportTemplate(filePath);
                if (!inspection.Success)
                {
                    ShowInfoBar(inspection.Message, InfoBarSeverity.Error);
                    return;
                }

                var strategy = TemplateService.TemplateImportConflictStrategy.KeepBoth;
                if (inspection.HasConflict)
                {
                    var conflictDialog = new ContentDialog
                    {
                        Title = I18n.GetString("Template_Import_ConflictTitle"),
                        Content = I18n.Format(
                            "Template_Import_ConflictContent",
                            inspection.Template?.Name ?? string.Empty,
                            inspection.ConflictTemplateName),
                        PrimaryButtonText = I18n.GetString("Template_Import_ConflictReplace"),
                        SecondaryButtonText = I18n.GetString("Template_Import_ConflictKeepBoth"),
                        CloseButtonText = I18n.GetString("Common_Cancel"),
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };
                    ThemeService.ApplyThemeToDialog(conflictDialog);

                    var conflictResult = await conflictDialog.ShowAsync();
                    if (conflictResult == ContentDialogResult.None)
                    {
                        return;
                    }

                    strategy = conflictResult == ContentDialogResult.Primary
                        ? TemplateService.TemplateImportConflictStrategy.ReplaceExisting
                        : TemplateService.TemplateImportConflictStrategy.KeepBoth;
                }

                var ok = TemplateService.ImportTemplate(filePath, strategy, out var message);
                ShowInfoBar(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[DataManagementControl] Import template failed: {ex.Message}", nameof(DataManagementControl), ex);
                ShowInfoBar(I18n.Format("Template_Import_Failed", ex.Message), InfoBarSeverity.Error);
            }
        }

        private async void OnManageTemplatesClick(object sender, RoutedEventArgs e)
        {
            var dialog = new TemplateManagerDialog
            {
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }

        private async void OnBrowseOfficialTemplatesClick(object sender, RoutedEventArgs e)
        {
            var item = await OfficialTemplateDialogService.PickTemplateAsync(
                this.XamlRoot,
                I18n.GetString("OfficialTemplates_BrowseTitle"));
            if (item == null)
            {
                return;
            }

            await ImportOfficialTemplateAsync(item);
        }

        private async void OnUseTemplateShareCodeClick(object sender, RoutedEventArgs e)
        {
            var item = await OfficialTemplateDialogService.PromptShareCodeAsync(this.XamlRoot);
            if (item == null)
            {
                return;
            }

            await ImportOfficialTemplateAsync(item);
        }

        private async void OnPrepareTemplateSubmissionClick(object sender, RoutedEventArgs e)
        {
            await TemplateSubmissionWorkflowService.RunAsync(this.XamlRoot);
        }

        private async Task ImportOfficialTemplateAsync(RemoteTemplateIndexItem item)
        {
            var importResult = await OfficialTemplateImportService.ImportTemplateAsync(this.XamlRoot, item);
            if (importResult.Canceled)
            {
                return;
            }

            ShowInfoBar(
                importResult.Message,
                importResult.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }

        private async void OnExportHistoryClick(object sender, RoutedEventArgs e)
        {
            var location = await PromptDataTransferLocationAsync(
                I18n.GetString("Settings_ExportHistoryMode_Title"),
                I18n.GetString("Settings_ExportHistoryMode_Description"));
            if (location == null)
            {
                return;
            }

            if (location == DataTransferLocation.Cloud)
            {
                var remoteBasePath = await PromptCloudRemoteBasePathAsync(
                    I18n.GetString("Settings_ExportHistoryToCloud_Title"),
                    I18n.GetString("Settings_ExportHistoryToCloud_Description"));
                if (string.IsNullOrWhiteSpace(remoteBasePath))
                {
                    return;
                }

                var cloudResult = await CloudSyncService.ExportHistoryToCloudAsync(remoteBasePath);
                ShowInfoBar(cloudResult.Message, cloudResult.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                return;
            }

            var filePath = await MainWindowService.PickSaveFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.DataManagement.ExportHistory",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["JSON"] = new ReadOnlyCollection<string>(new[] { ".json" })
                },
                "FolderRewind_history",
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            bool ok = HistoryService.ExportHistory(filePath);
            if (ok)
                ShowInfoBar(I18n.GetString("Settings_ExportHistorySuccess"), InfoBarSeverity.Success);
            else
                ShowInfoBar(I18n.GetString("Settings_ExportHistoryFailed"), InfoBarSeverity.Error);
        }

        private async void OnImportHistoryClick(object sender, RoutedEventArgs e)
        {
            var location = await PromptDataTransferLocationAsync(
                I18n.GetString("Settings_ImportHistoryMode_Title"),
                I18n.GetString("Settings_ImportHistoryMode_Description"));
            if (location == null)
            {
                return;
            }

            var confirm = new ContentDialog
            {
                Title = I18n.GetString("Settings_ImportHistoryConfirmTitle"),
                Content = new TextBlock { Text = I18n.GetString("Settings_ImportHistoryConfirmContent"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = I18n.GetString("Settings_ImportHistoryMerge"),
                SecondaryButtonText = I18n.GetString("Settings_ImportHistoryReplace"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            var result = await confirm.ShowAsync();
            if (result == ContentDialogResult.None) return;

            bool merge = (result == ContentDialogResult.Primary);

            if (location == DataTransferLocation.Cloud)
            {
                var remoteBasePath = await PromptCloudRemoteBasePathAsync(
                    I18n.GetString("Settings_ImportHistoryFromCloud_Title"),
                    I18n.GetString("Settings_ImportHistoryFromCloud_Description"));
                if (string.IsNullOrWhiteSpace(remoteBasePath))
                {
                    return;
                }

                var cloudResult = await CloudSyncService.ImportHistoryFromCloudAsync(remoteBasePath, merge);
                ShowInfoBar(cloudResult.Message, cloudResult.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
                return;
            }

            var filePath = await MainWindowService.PickFilePathAsync(
                string.Empty,
                "FolderRewind.Settings.DataManagement.ImportHistory",
                new[] { ".json" },
                MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(filePath)) return;

            var (ok, count) = HistoryService.ImportHistory(filePath, merge);
            if (ok)
                ShowInfoBar(I18n.Format("Settings_ImportHistorySuccess", count.ToString()), InfoBarSeverity.Success);
            else
                ShowInfoBar(I18n.GetString("Settings_ImportHistoryFailed"), InfoBarSeverity.Error);
        }

        private async Task<DataTransferLocation?> PromptDataTransferLocationAsync(string title, string description)
        {
            var localRadio = new RadioButton
            {
                Content = I18n.GetString("Settings_DataTransferMode_Local"),
                IsChecked = true
            };

            var cloudRadio = new RadioButton
            {
                Content = I18n.GetString("Settings_DataTransferMode_Cloud")
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = description,
                            TextWrapping = TextWrapping.Wrap
                        },
                        localRadio,
                        cloudRadio
                    }
                },
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return cloudRadio.IsChecked == true ? DataTransferLocation.Cloud : DataTransferLocation.Local;
        }

        private async Task<string?> PromptCloudRemoteBasePathAsync(string title, string description)
        {
            var input = new TextBox
            {
                Header = I18n.GetString("Settings_CloudRemoteBasePath_Label"),
                PlaceholderText = I18n.GetString("Settings_CloudRemoteBasePath_Placeholder"),
                Text = ConfigService.GetRecommendedDefaultCloudRemoteBasePath(),
                TextWrapping = TextWrapping.NoWrap
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(input);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Confirm"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            return input.Text?.Trim();
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "template";
            }

            var sanitized = name;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized;
        }

        private async void ShowInfoBar(string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Content = message,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    XamlRoot = this.XamlRoot
                };
                ThemeService.ApplyThemeToDialog(dialog);
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[DataManagementControl] ShowInfoBar dialog failed: {ex.Message}", nameof(DataManagementControl));
            }
        }
    }
}
