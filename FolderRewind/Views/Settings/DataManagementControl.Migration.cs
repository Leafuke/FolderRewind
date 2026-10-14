using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.ViewModels;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings;

public sealed partial class DataManagementControl
{
    private CancellationTokenSource? _migrationCancellation;
    private bool _migrationBusy;
    private IReadOnlyList<MigrationProjectAssessment> _assessments = [];
    private async void OnMigrationLoaded(object sender, RoutedEventArgs e)
    {
        RefreshMigrationStatus();
        if (_migrationBusy) return;
        StartMigration();
        try { await RefreshAssessmentsAsync(); }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
    private async Task RefreshAssessmentsAsync(CancellationToken? checkToken = null)
    {
        _assessments = await MigrationBatchRecoveryService.CheckAsync(checkToken ?? _migrationCancellation?.Token ?? default);
        Migration.Apply(_assessments);
    }
    private async void OnBatchReviewClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        var migrationToken = StartMigration();
        try
        {
            await RefreshAssessmentsAsync();
            var vm = new MigrationReviewViewModel { Description = I18n.GetString("Migration_BatchDescription") };
            foreach (var assessment in _assessments.Where(a => a.Backup != MigrationCapabilityStatus.Released || a.Cloud != MigrationCapabilityStatus.Released))
                vm.Add(MigrationViewModel.ToItem(assessment));
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_BatchTitle"), new MigrationReviewControl(vm), I18n.GetString("Migration_ResumeReady"))) return;
            var requests = vm.SelectedItems.Select(item => new MigrationResumeRequest((MigrationProjectAssessment)item.Value!, item.BackupSelected, item.CloudSelected)).ToArray();
            var results = await MigrationBatchRecoveryService.ResumeAsync(requests, migrationToken);
            // A canceled batch retains completed project decisions; refreshing is read-only.
            _assessments = await MigrationBatchRecoveryService.CheckAsync(); Migration.Apply(_assessments);
            var failures = results.Where(r => r.Diagnostic.Length > 0).Select(r => (vm.Items.FirstOrDefault(item => item.Id == r.ConfigId)?.Name ?? r.ConfigId) + ": " + r.Diagnostic).ToArray();
            ShowInfoBar((migrationToken.IsCancellationRequested ? I18n.GetString("Migration_BatchCanceled") + "\n" : "")
                + I18n.Format("Migration_ResumeCount", results.Count(r => r.Backup || r.Cloud))
                + (failures.Length > 0 ? "\n" + string.Join(Environment.NewLine, failures) : ""),
                failures.Length > 0 ? InfoBarSeverity.Warning : migrationToken.IsCancellationRequested ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
    private async void OnBatchPathsClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        var migrationToken = StartMigration();
        try
        {
            await RefreshAssessmentsAsync();
            var vm = new MigrationReviewViewModel { ShowPathMapping = true, Description = I18n.GetString("Migration_BatchPathsDescription") };
            vm.SetMappingProjects(_assessments.Where(item => item.Backup != MigrationCapabilityStatus.Released).Select(item => item.Snapshot).ToArray());
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_BatchPathsTitle"), new MigrationReviewControl(vm), I18n.GetString("Migration_SavePaths"))) return;
            var results = await MigrationProjectSetupService.ApplyMappingsAsync(vm.SelectedItems.Select(item => (MigrationPathPreview)item.Value!), migrationToken);
            await RefreshAssessmentsAsync(CancellationToken.None);
            var errors = results.Where(result => result.Diagnostic.Length > 0).Select(result => (vm.Items.FirstOrDefault(item => item.Id == result.ConfigId)?.Name ?? result.ConfigId) + ": " + result.Diagnostic).ToArray();
            ShowInfoBar((migrationToken.IsCancellationRequested ? I18n.GetString("Migration_BatchCanceled") + "\n" : "")
                + I18n.Format("Migration_PathsSavedCount", results.Count(result => result.Diagnostic.Length == 0))
                + (errors.Length > 0 ? "\n" + string.Join(Environment.NewLine, errors) : ""),
                errors.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
    private async void OnReadOriginalClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        var migrationToken = StartMigration();
        try
        {
            var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.Settings.DataManagement.OriginalSettings", [".json"], MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(path)) return;
            var candidates = await MigrationOriginalSettingsService.PreviewAsync(path);
            var vm = new MigrationReviewViewModel { Description = I18n.GetString("Migration_ReadOriginalDescription") };
            foreach (var item in candidates) vm.Add(new() { Id = item.Current.Id, Name = item.Current.Name, Details = item.Current.Id, Value = item,
                Status = I18n.Format("Migration_OriginalSwitches", I18n.GetString(item.Current.Automation.AutoBackupEnabled ? "Migration_On" : "Migration_Off"),
                    I18n.GetString(item.AutoBackup ? "Migration_On" : "Migration_Off"), I18n.GetString(item.Current.Cloud.Enabled ? "Migration_On" : "Migration_Off"), I18n.GetString(item.Cloud ? "Migration_On" : "Migration_Off")) });
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_ReadOriginalTitle"), new MigrationReviewControl(vm), I18n.GetString("Migration_ReadOriginalAction"))) return;
            var errors = await MigrationOriginalSettingsService.ApplyAsync(vm.SelectedItems.Select(i => (MigrationOriginalSettings)i.Value!), migrationToken);
            await RefreshAssessmentsAsync(CancellationToken.None);
            ShowInfoBar(errors.Count > 0 ? string.Join(Environment.NewLine, errors) : I18n.GetString(migrationToken.IsCancellationRequested ? "Migration_BatchCanceled" : "Migration_OriginalReadDone"), errors.Count > 0 ? InfoBarSeverity.Warning : migrationToken.IsCancellationRequested ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
    private CancellationToken StartMigration()
    {
        var cancellation = new CancellationTokenSource();
        _migrationCancellation = cancellation;
        SetMigrationBusy(true);
        return cancellation.Token;
    }
    private void SetMigrationBusy(bool busy)
    {
        _migrationBusy = busy;
        Migration.Busy = busy;
        Migration.CanCancel = busy && _migrationCancellation is not null;
        ImportConfigButton.IsEnabled = ExportHistoryButton.IsEnabled = ImportHistoryButton.IsEnabled = !busy;
        ExportConfigButton.IsEnabled = ExportDataButton.IsEnabled = ImportDataButton.IsEnabled = !busy;
        RecoverDataButton.IsEnabled = !busy && ConfigService.PendingImport.Status != ConfigImportStatus.StagedRequiresRestart;
        if (!busy) { _migrationCancellation?.Dispose(); _migrationCancellation = null; }
        RefreshMigrationStatus(showStatus: false);
    }
    private void RefreshMigrationStatus(bool showStatus = true)
    {
        try
        {
            var result = ConfigService.PendingImport;
            var pending = result.Status == ConfigImportStatus.StagedRequiresRestart;
            Migration.PendingRestart = pending;
            Migration.Legacy = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).HasLegacySetup;
            if (!showStatus) return;
            if (pending) ShowInfoBar(I18n.GetString("Migration_ConfigPrepared"), InfoBarSeverity.Warning);
            else if (result.Status is ConfigImportStatus.RolledBack or ConfigImportStatus.Failed)
                ShowInfoBar(I18n.Format("Migration_ConfigRolledBack", CloudCommandSecurity.Redact(result.Diagnostic)), InfoBarSeverity.Warning);
        }
        catch (Exception error) { ReportMigrationError(error); }
    }
    private void ReportMigrationError(Exception error)
    {
        LogService.LogError("Data migration failed.", nameof(DataManagementControl), error);
        var category = error switch
        {
            HistoryPackCompatibilityException => "Migration_ErrorFormat",
            HistoryRepositoryException or InvalidDataException or System.Text.Json.JsonException => "Migration_ErrorData",
            UnauthorizedAccessException => "Migration_ErrorPermission",
            IOException io when (io.HResult & 0xFFFF) is 112 or 39 => "Migration_ErrorSpace",
            OperationCanceledException => "Common_Cancel",
            _ => "Common_Failed"
        };
        ShowInfoBar(I18n.GetString(category) + Environment.NewLine + CloudCommandSecurity.Redact(error.Message), InfoBarSeverity.Error);
    }
    private async Task<bool> ShowMigrationDialogAsync(string title, object content, string action)
    {
        var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = action,
            CloseButtonText = I18n.GetString("Common_Cancel"), DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        ThemeService.ApplyThemeToDialog(dialog);
        var pathReview = (content as MigrationReviewControl)?.ViewModel;
        System.ComponentModel.PropertyChangedEventHandler? update = null;
        if (pathReview?.ShowPathMapping == true)
        {
            dialog.IsPrimaryButtonEnabled = pathReview.CanSavePaths;
            update = (_, _) => dialog.IsPrimaryButtonEnabled = pathReview.CanSavePaths;
            pathReview.PropertyChanged += update;
        }
        try { return await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot) == ContentDialogResult.Primary; }
        finally { if (update is not null) pathReview!.PropertyChanged -= update; }
    }
    private static ListView CreateMigrationList(string id, bool multiple = false)
    {
        var list = new ListView { MaxHeight = 320, SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(list, id);
        AutomationProperties.SetName(list, I18n.GetString("Migration_Projects"));
        return list;
    }
    private async Task<ConfigImportResult> PrepareConfigImportWithPreviewAsync(string path)
    {
        var candidate = await ConfigService.PreviewConfigImportAsync(path);
        var vm = new MigrationReviewViewModel { PreviewOnly = true, Description = I18n.GetString("Migration_ConfigPreview") };
        vm.ModeLabels.Add(I18n.GetString("Migration_ModeLocal"));
        vm.ModeLabels.Add(I18n.GetString("Migration_ModeCross"));
        vm.SelectedMode = candidate.Config.BackupConfigs.Any(p => ConfigService.CurrentConfig.BackupConfigs.Any(c => MigrationRecoveryPolicy.SameId(c.Id, p.Id))) ? 0 : 1;
        foreach (var project in candidate.Config.BackupConfigs)
            vm.Add(new() { ShowSelection = false, Id = project.Id, Name = project.Name, Details = project.Id,
                Status = I18n.Format("Migration_ConfigPaths", project.SourceFolders.Count,
                    string.Join(Environment.NewLine, project.SourceFolders.Select(f => f.Path)), project.DestinationPath) });
        // Configuration import still replaces the complete document; the list is a preview, not project selection.
        vm.SelectAll(true);
        if (!await ShowMigrationDialogAsync(I18n.GetString("Settings_ImportConfigConfirmTitle"), new MigrationReviewControl(vm), I18n.GetString("Migration_Prepare")))
            return new(ConfigImportStatus.Canceled);
        var preview = await DataMigrationService.PreviewAsync(path, _migrationCancellation?.Token ?? default);
        if (preview.FileSha256 != candidate.Sha256) throw new IOException(I18n.GetString("Migration_CheckExpired"));
        return (await DataMigrationService.ImportAsync(preview, preview.Projects.Select(p => p.ConfigId).ToArray(),
            vm.SelectedMode == 0 ? ConfigImportMode.LocalRestore : ConfigImportMode.CrossDevice, false, _migrationCancellation?.Token ?? default)).Configuration!;
    }
    private async void OnImportConfigClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        StartMigration();
        try
        {
            var location = await PromptDataTransferLocationAsync(I18n.GetString("Settings_ImportConfigMode_Title"), I18n.GetString("Settings_ImportConfigMode_Description"));
            if (location is null) return;
            if (location == DataTransferLocation.Cloud)
            {
                var connection = await PromptCloudConnectionAsync(I18n.GetString("Settings_ImportConfigFromCloud_Title"), I18n.GetString("Settings_ImportConfigFromCloud_Description"));
                if (connection is null) return;
                var exports = await CloudSyncService.ListCloudConfigurationExportsAsync(connection, _migrationCancellation?.Token ?? default);
                var selected = await PromptCloudExportVersionAsync(exports);
                if (selected is null) return;
                var result = await CloudSyncService.ImportConfigFromCloudAsync(connection, path =>
                    UiDispatcherService.RunOnUiAsync(() => PrepareConfigImportWithPreviewAsync(path)), selected);
                ShowInfoBar(result.Message, result.Success ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
            else
            {
                var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.Settings.DataManagement.ImportConfig", [".json"], MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
                if (string.IsNullOrWhiteSpace(path)) return;
                var result = await PrepareConfigImportWithPreviewAsync(path);
                if (result.Status != ConfigImportStatus.Canceled)
                    ShowInfoBar(result.Success ? I18n.GetString("Migration_ConfigPrepared") : CloudCommandSecurity.Redact(result.Diagnostic),
                        result.Success ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            }
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
    private async void OnRestartImportClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy || ConfigService.PendingImport.Status != ConfigImportStatus.StagedRequiresRestart) return;
        try
        {
            SetMigrationBusy(true);
            Program.RestartOnExit = true;
            await App.ExitApplicationAsync();
        }
        catch (Exception error) { Program.RestartOnExit = false; ReportMigrationError(error); SetMigrationBusy(false); }
    }
    private void OnCancelPendingClick(object sender, RoutedEventArgs e)
    { ConfigService.CancelPendingImport(); RefreshMigrationStatus(); ShowInfoBar(I18n.GetString("Migration_ImportCanceled"), InfoBarSeverity.Informational); }
    private void OnCancelOperationClick(object sender, RoutedEventArgs e) => _migrationCancellation?.Cancel();

    private async void OnExportHistoryClick(object sender, RoutedEventArgs e) => await ExportDataAsync(DataMigrationScope.History);
    private async void OnImportHistoryClick(object sender, RoutedEventArgs e) => await ImportDataAsync(true);
    private async void OnSetupProjectClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        var migrationToken = StartMigration();
        try
        {
            var configs = ConfigService.CurrentConfig.BackupConfigs.Where(c => MigrationSetupStateStore.IsBackupPending(ConfigService.ConfigDirectory, c.Id)).ToArray();
            if (configs.Length == 0) { ShowInfoBar(I18n.GetString("Migration_NoProjects"), InfoBarSeverity.Warning); return; }
            var choices = new ComboBox { Header = I18n.GetString("Migration_Projects"), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(choices, "MigrationSetupProject");
            foreach (var config in configs) choices.Items.Add(new ComboBoxItem { Content = config.Name, Tag = config });
            choices.SelectedIndex = 0;
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_SetupTitle"), choices, I18n.GetString("Common_Confirm"))) return;
            var original = ConfigSnapshotMapper.Copy((BackupConfig)((ComboBoxItem)choices.SelectedItem).Tag);
            var draft = ConfigSnapshotMapper.Copy(original);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = I18n.GetString("Migration_SetupDescription"), TextWrapping = TextWrapping.Wrap });
            var list = CreateMigrationList("MigrationSourcePaths");
            list.Height = 180;
            var editors = new Dictionary<string, TextBox>();
            foreach (var source in draft.SourceFolders)
            {
                var editor = new TextBox { Header = source.DisplayName + "\n" + source.Id, Text = source.Path };
                AutomationProperties.SetAutomationId(editor, "MigrationSource_" + source.Id);
                editors.Add(source.Id, editor); list.Items.Add(editor);
            }
            var destination = new TextBox { Header = I18n.GetString("Migration_Destination"), Text = draft.DestinationPath };
            var oldRoot = new TextBox { Header = I18n.GetString("Migration_OldRoot") };
            var newRoot = new TextBox { Header = I18n.GetString("Migration_NewRoot") };
            AutomationProperties.SetAutomationId(destination, "MigrationDestination");
            AutomationProperties.SetAutomationId(oldRoot, "MigrationOldRoot");
            AutomationProperties.SetAutomationId(newRoot, "MigrationNewRoot");
            var applyRoot = new Button { Content = I18n.GetString("Migration_PreviewPaths") };
            AutomationProperties.SetAutomationId(applyRoot, "MigrationPreviewPaths");
            var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
            applyRoot.Click += (_, _) =>
            {
                try
                {
                    var preview = ConfigSnapshotMapper.Copy(original);
                    foreach (var source in preview.SourceFolders) source.Path = editors[source.Id].Text;
                    preview.DestinationPath = destination.Text;
                    MigrationProjectSetupService.Relocate(preview, oldRoot.Text, newRoot.Text);
                    foreach (var source in preview.SourceFolders) editors[source.Id].Text = source.Path;
                    destination.Text = preview.DestinationPath;
                    feedback.Text = I18n.GetString("Migration_PathPreviewReady");
                }
                catch (Exception error) { feedback.Text = CloudCommandSecurity.Redact(error.Message); }
            };
            var roots = new Grid();
            roots.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            roots.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            oldRoot.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(newRoot, 1);
            roots.Children.Add(oldRoot); roots.Children.Add(newRoot);
            content.Children.Add(roots); content.Children.Add(applyRoot);
            content.Children.Add(feedback); content.Children.Add(list); content.Children.Add(destination);
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_SetupTitle"), content, I18n.GetString("Migration_ConfirmSetup"))) return;
            foreach (var source in draft.SourceFolders) source.Path = editors[source.Id].Text;
            draft.DestinationPath = destination.Text;
            await MigrationProjectSetupService.CompleteAsync(original, draft, releaseBackup: false, cancellationToken: migrationToken);
            await RefreshAssessmentsAsync();
            ShowInfoBar(I18n.GetString("Migration_PathSaved"), InfoBarSeverity.Success);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
}
