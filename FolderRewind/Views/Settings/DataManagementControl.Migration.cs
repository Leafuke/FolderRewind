using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
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
    private void SetMigrationBusy(bool busy)
    {
        _migrationBusy = busy;
        ImportConfigButton.IsEnabled = ExportHistoryButton.IsEnabled = ImportHistoryButton.IsEnabled = SetupProjectButton.IsEnabled = !busy;
        CancelOperationButton.IsEnabled = busy;
        if (!busy) { _migrationCancellation?.Dispose(); _migrationCancellation = null; }
        RefreshMigrationStatus(showStatus: false);
    }
    private void RefreshMigrationStatus(bool showStatus = true)
    {
        try
        {
            var result = ConfigService.PendingImport;
            var pending = result.Status == ConfigImportStatus.StagedRequiresRestart;
            RestartImportButton.IsEnabled = CancelPendingButton.IsEnabled = pending && !_migrationBusy;
            if (!showStatus) return;
            if (pending) ShowInfoBar(I18n.GetString("Migration_ConfigPrepared"), InfoBarSeverity.Warning);
            else if (result.Status == ConfigImportStatus.Applied && ConfigService.CurrentConfig.BackupConfigs.Any(c =>
                MigrationSetupStateStore.IsPending(ConfigService.ConfigDirectory, c.Id)))
                ShowInfoBar(I18n.GetString("Migration_ConfigApplied"), InfoBarSeverity.Warning);
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
        return await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot) == ContentDialogResult.Primary;
    }
    private static ListView CreateMigrationList(string id, bool multiple = false)
    {
        var list = new ListView { Height = 320, SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(list, id);
        AutomationProperties.SetName(list, I18n.GetString("Migration_Projects"));
        return list;
    }
    private async Task<ConfigImportResult> PrepareConfigImportWithPreviewAsync(string path)
    {
        var candidate = await ConfigService.PreviewConfigImportAsync(path);
        var list = CreateMigrationList("MigrationConfigPreview");
        foreach (var project in candidate.Config.BackupConfigs)
            list.Items.Add(I18n.Format("Migration_ConfigProject", project.Name, project.Id, project.SourceFolders.Count,
                string.Join(Environment.NewLine, project.SourceFolders.Select(f => f.Path)), project.DestinationPath));
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = I18n.GetString("Migration_ConfigPreview"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(list);
        if (!await ShowMigrationDialogAsync(I18n.GetString("Settings_ImportConfigConfirmTitle"), panel, I18n.GetString("Migration_Prepare")))
            return new(ConfigImportStatus.Canceled);
        return await ConfigService.StageConfigImportAsync(path, _migrationCancellation?.Token ?? default, candidate.Sha256);
    }
    private async void OnImportConfigClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        SetMigrationBusy(true); _migrationCancellation = new();
        try
        {
            var location = await PromptDataTransferLocationAsync(I18n.GetString("Settings_ImportConfigMode_Title"), I18n.GetString("Settings_ImportConfigMode_Description"));
            if (location is null) return;
            if (location == DataTransferLocation.Cloud)
            {
                var connection = await PromptCloudConnectionAsync(I18n.GetString("Settings_ImportConfigFromCloud_Title"), I18n.GetString("Settings_ImportConfigFromCloud_Description"));
                if (connection is null) return;
                var result = await CloudSyncService.ImportConfigFromCloudAsync(connection, path =>
                    UiDispatcherService.RunOnUiAsync(() => PrepareConfigImportWithPreviewAsync(path)));
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

    private async void OnExportHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        SetMigrationBusy(true); _migrationCancellation = new();
        try
        {
            var configs = ConfigService.CurrentConfig.BackupConfigs.ToArray();
            if (configs.Length == 0) { ShowInfoBar(I18n.GetString("Migration_NoProjects"), InfoBarSeverity.Warning); return; }
            var list = CreateMigrationList("MigrationExportProjects", true);
            foreach (var config in configs)
            {
                var item = new ListViewItem { Content = config.Name + "\n" + config.Id, Tag = config.Id };
                list.Items.Add(item); list.SelectedItems.Add(item);
            }
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = I18n.GetString("Migration_MetadataOnly"), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(list);
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_ExportTitle"), panel, I18n.GetString("Common_Confirm"))) return;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToHashSet();
            if (selected.Count == 0) return;
            var path = await MainWindowService.PickSaveFilePathAsync("", "FolderRewind.Settings.DataManagement.ExportHistory",
                new Dictionary<string, IReadOnlyList<string>> { [I18n.GetString("FilePicker_FolderRewindHistory")] = new ReadOnlyCollection<string>([".frhistory"]) },
                "FolderRewind_history", MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(path)) return;
            var projects = new List<HistoryTransferExportProject>();
            foreach (var config in configs.Where(c => selected.Contains(c.Id)))
            {
                var id = new HistoryConfigId(config.Id);
                var paths = HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, id);
                if (config.HistoryRepositoryBinding is not null && !File.Exists(paths.DescriptorPath))
                    throw new InvalidDataException(I18n.Format("Migration_ProjectBlocked", config.Name, I18n.GetString("Migration_HistoryMissing")));
                var runtime = File.Exists(paths.DescriptorPath) ? await NativeHistoryCoreGateway.EnsureReadyAsync(config, _migrationCancellation.Token) : null;
                projects.Add(new(id, config.Name, config.SourceFolders.Select(f => new HistoryTransferSource(
                    new SourceId(Guid.Parse(f.Id)), f.DisplayName, f.Path)).ToImmutableArray(), runtime));
            }
            await new HistoryRepositoryBatchTransferService().ExportAsync(projects, path, AppRuntimeInfo.GetApplicationVersion()?.ToString() ?? "unknown", _migrationCancellation.Token);
            ShowInfoBar(I18n.Format("Migration_ExportDone", projects.Count) + "\n" + I18n.GetString("Migration_MetadataOnly"), InfoBarSeverity.Success);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
    private async void OnImportHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        SetMigrationBusy(true); _migrationCancellation = new();
        try
        {
            var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.Settings.DataManagement.ImportHistory", [".frhistory"], MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(path)) return;
            var configIds = ConfigService.CurrentConfig.BackupConfigs.Select(c => new HistoryConfigId(c.Id)).ToHashSet();
            var service = new HistoryRepositoryBatchTransferService(id => NativeHistoryCoreGateway.TryGetRuntime(id, out var runtime) ? runtime : null, configIds.Contains);
            var preview = await service.PreviewAsync(path, _migrationCancellation.Token);
            var list = CreateMigrationList("MigrationImportProjects", true);
            foreach (var project in preview.Projects)
            {
                var association = I18n.GetString(configIds.Contains(project.ConfigId) ? "Migration_Associated" : "Migration_AwaitingConfig");
                var item = new ListViewItem { Content = I18n.Format("Migration_HistoryProject", project.Name, project.ConfigId,
                    project.Sources.Length, project.VersionCount, project.BranchCount, project.PackSha256.Count, association), Tag = project.ConfigId };
                list.Items.Add(item); list.SelectedItems.Add(item);
            }
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = I18n.Format("Migration_FormatPreview", preview.ApplicationVersion, preview.FormatVersion) + "\n" + I18n.GetString("Migration_MetadataOnly"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(list);
            if (!await ShowMigrationDialogAsync(I18n.GetString("Settings_ImportHistoryConfirmTitle"), panel, I18n.GetString("Settings_ImportHistoryMerge"))) return;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => (HistoryConfigId)i.Tag).ToArray();
            if (selected.Length == 0) return;
            var result = await service.ImportAsync(path, ConfigService.ConfigDirectory, selected, _migrationCancellation.Token, preview.FileSha256);
            var reports = new List<string>();
            foreach (var project in result.Projects)
            {
                if (!string.IsNullOrWhiteSpace(project.Diagnostic)) LogService.LogError($"History import {project.ConfigId}: {project.Diagnostic}", nameof(DataManagementControl));
                reports.Add(I18n.Format("Migration_ProjectResult", project.ConfigId, project.InstalledPacks, project.DuplicatePacks,
                    I18n.GetString("Migration_Commit_" + project.CommitStatus),
                    I18n.GetString(project.ImportedAsOrphanRepository ? "Migration_AwaitingConfig" : "Migration_Associated"),
                    I18n.GetString("Migration_Local_" + project.LocalStateStatus)));
                if (project.ErrorKind != HistoryTransferErrorKind.None)
                    reports.Add(I18n.GetString("Migration_Error" + project.ErrorKind));
                if (!project.ImportedAsOrphanRepository && project.CommitStatus == HistoryTransferCommitStatus.Committed)
                    try { await NativeHistoryCoreGateway.EnsureReadyAsync(project.ConfigId.Value); }
                    catch (Exception error) { LogService.LogError("Imported history runtime requires attention.", nameof(DataManagementControl), error); }
            }
            ShowInfoBar(string.Join("\n\n", reports) + "\n" + I18n.GetString("Migration_ArchivesUnverified"),
                result.Projects.All(p => p.CommitStatus == HistoryTransferCommitStatus.Committed && !p.ImportedAsOrphanRepository && p.LocalStateStatus == HistoryTransferLocalStateStatus.Ready)
                    ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }

    private async void OnSetupProjectClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy) return;
        SetMigrationBusy(true);
        try
        {
            var configs = ConfigService.CurrentConfig.BackupConfigs.ToArray();
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
            await MigrationProjectSetupService.CompleteAsync(original, draft);
            ShowInfoBar(I18n.GetString("Migration_SetupDone"), InfoBarSeverity.Success);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }
}
