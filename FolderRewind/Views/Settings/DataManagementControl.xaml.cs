using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.History.Application;
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
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public MigrationViewModel Migration { get; } = new();

        public DataManagementControl()
        {
            this.InitializeComponent();
            Loaded += OnMigrationLoaded;
            MigrationStatusBar.Closed += (_, _) => Migration.HasReport = false;
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
        }

        private void OnExportConfigClick(object sender, RoutedEventArgs e)
            => OpenMigration(new(MigrationWizardMode.Export, DataMigrationScope.Configuration));
        private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var templates = BackupPresetService.GetTemplates()
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

                if (await AppDialogService.Default.ShowCustomAsync(chooseDialog, this.XamlRoot) != ContentDialogResult.Primary)
                {
                    return;
                }

                var selectedTemplate = (templateCombo.SelectedItem as ComboBoxItem)?.Tag as BackupPreset;
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
                        [I18n.GetString("FilePicker_FolderRewindTemplate")] = new ReadOnlyCollection<string>(new[] { BackupPresetService.ShareFileExtension })
                    },
                    $"FolderRewind_template_{SanitizeFileName(selectedTemplate.Name)}",
                    MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
                if (string.IsNullOrWhiteSpace(filePath)) return;

                var ok = BackupPresetService.ExportTemplate(selectedTemplate.Id, filePath, out var message);
                ShowInfoBar(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[DataManagementControl] Export template failed: {ex.Message}", nameof(DataManagementControl), ex);
                ShowInfoBar(I18n.Format("Template_Export_Failed", ex.Message), InfoBarSeverity.Error);
            }
        }

        private bool _isImportingTemplate;
        private async void OnImportTemplateClick(object sender, RoutedEventArgs e)
        {
            if (_isImportingTemplate) return;
            _isImportingTemplate = true;
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

                var inspection = BackupPresetService.InspectImportTemplate(filePath);
                if (!inspection.Success)
                {
                    ShowInfoBar(inspection.Message, InfoBarSeverity.Error);
                    return;
                }

                var strategy = BackupPresetService.TemplateImportConflictStrategy.KeepBoth;
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

                    var conflictResult = await AppDialogService.Default.ShowCustomAsync(conflictDialog, this.XamlRoot);
                    if (conflictResult == ContentDialogResult.None)
                    {
                        return;
                    }

                    strategy = conflictResult == ContentDialogResult.Primary
                        ? BackupPresetService.TemplateImportConflictStrategy.ReplaceExisting
                        : BackupPresetService.TemplateImportConflictStrategy.KeepBoth;
                }

                var imported = await BackupPresetService.ImportTemplateAsync(filePath, strategy);
                ShowInfoBar(imported.Message, imported.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[DataManagementControl] Import template failed: {ex.Message}", nameof(DataManagementControl), ex);
                ShowInfoBar(I18n.Format("Template_Import_Failed", ex.Message), InfoBarSeverity.Error);
            }
            finally { _isImportingTemplate = false; }
        }

        private async void OnManageTemplatesClick(object sender, RoutedEventArgs e)
        {
            var dialog = new TemplateManagerDialog
            {
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await AppDialogService.Default.ShowCustomAsync(dialog, this.XamlRoot);
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

        private void ShowInfoBar(string message, InfoBarSeverity severity)
        {
            Migration.HasReport = true;
            MigrationStatusBar.Message = message;
            MigrationStatusBar.Severity = severity;
            MigrationStatusBar.IsOpen = true;
        }
    }
}
