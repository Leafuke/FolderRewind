using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings;

public sealed partial class DataManagementControl
{
    private async void OnExportDataClick(object sender, RoutedEventArgs e) => await ExportDataAsync(DataMigrationScope.ConfigurationAndHistory);
    private async void OnImportDataClick(object sender, RoutedEventArgs e) => await ImportDataAsync(false);

    private async Task ExportDataAsync(DataMigrationScope initialScope)
    {
        if (_migrationBusy) return;
        var token = StartMigration(); string? temporary = null;
        try
        {
            var configs = await MigrationBatchRecoveryService.CaptureAsync();
            if (configs.Length == 0) { ShowInfoBar(I18n.GetString("Migration_NoProjects"), InfoBarSeverity.Warning); return; }
            var vm = new MigrationReviewViewModel { ExportSelection = true, Description = I18n.GetString("Migration_ExportDescription") };
            vm.ModeLabels.Add(I18n.GetString("Migration_ExportAll")); vm.ModeLabels.Add(I18n.GetString("Migration_ExportSelected"));
            foreach (var config in configs) vm.Add(new() { Id = config.Id, Name = config.Name, Details = config.Id, Value = config });
            var scope = new ComboBox { Header = I18n.GetString("Migration_ContentScope"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var key in new[] { "Migration_ScopeBoth", "Migration_ScopeConfig", "Migration_ScopeHistory", "Migration_ScopeMetadata" }) scope.Items.Add(I18n.GetString(key));
            scope.SelectedIndex = (int)initialScope; AutomationProperties.SetAutomationId(scope, "MigrationDataScope");
            var payloads = new CheckBox { Content = I18n.GetString("Migration_IncludeArchives"), IsChecked = false };
            AutomationProperties.SetAutomationId(payloads, "MigrationIncludeArchives");
            scope.SelectionChanged += (_, _) => { payloads.IsEnabled = scope.SelectedIndex is 0 or 2; if (!payloads.IsEnabled) payloads.IsChecked = false; };
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(scope); panel.Children.Add(payloads); panel.Children.Add(new MigrationReviewControl(vm));
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_ExportTitle"), panel, I18n.GetString("Migration_Next"))) return;
            var selected = vm.SelectedItems.Select(i => new HistoryConfigId(i.Id)).ToArray(); if (selected.Length == 0) return;
            var location = await PromptDataTransferLocationAsync(I18n.GetString("Migration_ExportTitle"), I18n.GetString("Migration_LocationDescription"));
            if (location is null) return;
            var range = (DataMigrationScope)scope.SelectedIndex; var extension = range == DataMigrationScope.Configuration ? ".json" : ".frhistory";
            CloudSettings? connection = null; string? path;
            if (location == DataTransferLocation.Cloud)
            {
                connection = await PromptCloudConnectionAsync(I18n.GetString("Migration_ExportTitle"), I18n.GetString("Migration_CloudSnapshotDescription")); if (connection is null) return;
                temporary = Path.Combine(Path.GetTempPath(), "FolderRewind-migration-" + Guid.NewGuid().ToString("N") + extension); path = temporary;
            }
            else path = await MainWindowService.PickSaveFilePathAsync("", "FolderRewind.Migration.Export", new Dictionary<string, IReadOnlyList<string>> { ["FolderRewind"] = [extension] },
                range == DataMigrationScope.Configuration ? "FolderRewind_config" : "FolderRewind_migration", MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(path)) return;
            await DataMigrationService.ExportAsync(path, selected, range, payloads.IsChecked == true, token);
            if (connection is not null) await CloudSyncService.ExportMigrationSnapshotAsync(connection, path, token);
            ShowInfoBar(I18n.Format("Migration_ExportDone", selected.Length) + "\n" + I18n.GetString(range == DataMigrationScope.MetadataOnly ? "Migration_MetadataOnly"
                : payloads.IsChecked == true ? "Migration_EmbeddedExported" : "Migration_ExternalExported"), InfoBarSeverity.Success);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); SetMigrationBusy(false); }
    }

    private async Task ImportDataAsync(bool historyOnly)
    {
        if (_migrationBusy) return;
        var token = StartMigration(); string? temporary = null;
        try
        {
            var location = await PromptDataTransferLocationAsync(I18n.GetString("Migration_ImportTitle"), I18n.GetString("Migration_LocationDescription"));
            if (location is null) return; string? path;
            if (location == DataTransferLocation.Cloud)
            {
                var connection = await PromptCloudConnectionAsync(I18n.GetString("Migration_ImportTitle"), I18n.GetString("Migration_CloudSnapshotDescription")); if (connection is null) return;
                var snapshots = (await CloudSyncService.ListMigrationSnapshotsAsync(connection, token)).Where(s => !historyOnly || s.FileName.EndsWith(".frhistory", StringComparison.Ordinal)).ToArray();
                if (snapshots.Length == 0) { ShowInfoBar(I18n.GetString("Migration_NoCloudSnapshots"), InfoBarSeverity.Informational); return; }
                var versions = new ComboBox { Header = I18n.GetString("Migration_CloudVersions"), HorizontalAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetAutomationId(versions, "MigrationCloudSnapshots");
                foreach (var snapshot in snapshots) versions.Items.Add(new ComboBoxItem { Content = snapshot.CreatedAtUtc.LocalDateTime.ToString("g") + " · " + snapshot.FileName, Tag = snapshot });
                versions.SelectedIndex = 0; if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_ImportTitle"), versions, I18n.GetString("Migration_Next"))) return;
                var chosen = (MigrationCloudSnapshot)((ComboBoxItem)versions.SelectedItem).Tag;
                temporary = Path.Combine(Path.GetTempPath(), "FolderRewind-migration-" + Guid.NewGuid().ToString("N") + Path.GetExtension(chosen.FileName)); path = temporary;
                await CloudSyncService.DownloadMigrationSnapshotAsync(connection, chosen, path, token);
            }
            else path = await MainWindowService.PickFilePathAsync("", "FolderRewind.Migration.Import", historyOnly ? [".frhistory"] : [".json", ".frhistory"], MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
            if (string.IsNullOrWhiteSpace(path)) return;
            var preview = await DataMigrationService.PreviewAsync(path, token);
            var vm = new MigrationReviewViewModel { Description = I18n.GetString("Migration_ImportDescription") };
            foreach (var project in preview.Projects) vm.Add(new() { Id = project.ConfigId.Value, Name = project.Name, Details = project.ConfigId.Value,
                Status = I18n.Format("Migration_ImportProjectCounts", project.Sources.Length, project.VersionCount, project.PackSha256.Count) });
            var mode = new ComboBox { Header = I18n.GetString("Migration_RecoveryMode"), HorizontalAlignment = HorizontalAlignment.Stretch };
            mode.Items.Add(I18n.GetString("Migration_LocalRestore")); mode.Items.Add(I18n.GetString("Migration_CrossDevice")); mode.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(mode, "MigrationRecoveryMode");
            var metadata = new CheckBox { Content = I18n.GetString("Migration_OnlyMetadata"), IsEnabled = preview.Bundle is not null, IsChecked = false };
            AutomationProperties.SetAutomationId(metadata, "MigrationMetadataOnly");
            var configuration = new CheckBox { Content = I18n.GetString("Migration_ImportConfiguration"), IsChecked = preview.ConfigurationUtf8 is not null && !historyOnly,
                IsEnabled = preview.Bundle is not null && preview.ConfigurationUtf8 is not null };
            if (preview.Bundle is null) configuration.IsChecked = true;
            AutomationProperties.SetAutomationId(configuration, "MigrationImportConfiguration");
            var oldRoot = new TextBox { Header = I18n.GetString("Migration_OldRoot"), IsEnabled = preview.ConfigurationUtf8 is not null };
            var newRoot = new TextBox { Header = I18n.GetString("Migration_NewRoot"), IsEnabled = preview.ConfigurationUtf8 is not null };
            AutomationProperties.SetAutomationId(oldRoot, "MigrationImportOldRoot"); AutomationProperties.SetAutomationId(newRoot, "MigrationImportNewRoot");
            var mapping = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var previewPaths = new Button { Content = I18n.GetString("Migration_PreviewPaths"), IsEnabled = preview.ConfigurationUtf8 is not null };
            AutomationProperties.SetAutomationId(previewPaths, "MigrationImportPreviewPaths");
            previewPaths.Click += (_, _) =>
            {
                try
                {
                    var config = ConfigService.PrepareImportedConfig(preview.ConfigurationUtf8!); var ids = vm.SelectedItems.Select(i => new HistoryConfigId(i.Id)).ToHashSet();
                    mapping.Text = string.Join("\n\n", MigrationProjectSetupService.PreviewMappings(config.BackupConfigs.Where(c => ids.Contains(new(c.Id))), oldRoot.Text, newRoot.Text)
                        .Select(p => p.Draft.Name + "\n" + string.Join("\n", p.Draft.SourceFolders.Select(f => f.Path)) + "\n" + p.Draft.DestinationPath));
                }
                catch (Exception error) { mapping.Text = CloudCommandSecurity.Redact(error.Message); }
            };
            var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(mode); panel.Children.Add(configuration); panel.Children.Add(metadata);
            panel.Children.Add(new MigrationReviewControl(vm)); panel.Children.Add(oldRoot); panel.Children.Add(newRoot); panel.Children.Add(previewPaths); panel.Children.Add(mapping);
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_ImportTitle"), new ScrollViewer { Content = panel, MaxHeight = 500 }, I18n.GetString("Migration_ImportSelected"))) return;
            var selected = vm.SelectedItems.Select(i => new HistoryConfigId(i.Id)).ToArray(); if (selected.Length == 0) return;
            var result = await DataMigrationService.ImportAsync(preview, selected, mode.SelectedIndex == 0 ? ConfigImportMode.LocalRestore : ConfigImportMode.CrossDevice,
                metadata.IsChecked == true, token, configuration.IsChecked == true ? oldRoot.Text : "", configuration.IsChecked == true ? newRoot.Text : "", configuration.IsChecked == true);
            var messages = new List<string>();
            if (result.History is not null)
                foreach (var project in result.History.Projects) messages.Add(I18n.Format("Migration_ProjectResult", project.ConfigId, project.InstalledPacks, project.DuplicatePacks,
                    I18n.GetString("Migration_Commit_" + project.CommitStatus), I18n.GetString(project.ImportedAsOrphanRepository ? "Migration_AwaitingConfig" : "Migration_Associated"), I18n.GetString("Migration_Local_" + project.LocalStateStatus)));
            if (result.Configuration is not null) messages.Add(result.Configuration.Success ? I18n.GetString("Migration_ConfigPrepared") : result.Configuration.Diagnostic);
            messages.AddRange(result.Issues.Select(i => i.ConfigId + ": " + i.Diagnostic)); messages.Add(I18n.GetString(metadata.IsChecked == true ? "Migration_MetadataOnly" : "Migration_RecoveryNext"));
            await RefreshAssessmentsAsync(CancellationToken.None); ShowInfoBar(string.Join("\n\n", messages), InfoBarSeverity.Warning);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); SetMigrationBusy(false); }
    }

    private async void OnRecoverDataClick(object sender, RoutedEventArgs e)
    {
        if (_migrationBusy || ConfigService.PendingImport.Status == ConfigImportStatus.StagedRequiresRestart) return;
        var token = StartMigration();
        try
        {
            var vm = new MigrationReviewViewModel { Description = I18n.GetString("Migration_RecoveryDescription") };
            foreach (var config in await MigrationBatchRecoveryService.CaptureAsync()) vm.Add(new() { Id = config.Id, Name = config.Name, Details = config.Id, Value = config });
            if (!await ShowMigrationDialogAsync(I18n.GetString("Migration_RecoveryTitle"), new MigrationReviewControl(vm), I18n.GetString("Migration_Next"))) return;
            var reports = new List<string>();
            foreach (var row in vm.SelectedItems.ToArray())
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var config = (BackupConfig)row.Value!;
                    if (config.IsEncrypted && !await PrepareMigrationPasswordAsync(config, token))
                    { reports.Add(config.Name + ": " + I18n.GetString("Migration_PasswordPending")); continue; }
                    var context = await DataMigrationService.PrepareRecoveryAsync(config, token); var panel = new StackPanel { Spacing = 8 };
                    panel.Children.Add(new TextBlock { Text = I18n.GetString("Migration_BindingConfirmation"), TextWrapping = TextWrapping.Wrap });
                    var files = new Dictionary<RepresentationId, ComboBox>();
                    foreach (var group in context.Replicas.Candidates.GroupBy(c => c.RepresentationId))
                    {
                        var combo = new ComboBox { Header = group.Key.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
                        AutomationProperties.SetAutomationId(combo, "MigrationReplica_" + group.Key);
                        foreach (var candidate in group) combo.Items.Add(new ComboBoxItem { Content = candidate.Path + " · " + I18n.GetString(candidate.IdentityVerified ? "Migration_HashMatched" : "Migration_BindingNeedsConfirmation"), Tag = candidate });
                        combo.SelectedIndex = group.Count() == 1 ? 0 : -1; files.Add(group.Key, combo); panel.Children.Add(combo);
                    }
                    var branches = new Dictionary<SourceId, ComboBox>();
                    foreach (var source in context.Continuation.Sources)
                    {
                        var folder = config.SourceFolders.Single(f => Guid.Parse(f.Id) == source.SourceId.Value);
                        var combo = new ComboBox { Header = folder.DisplayName, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = source.HasHistory };
                        AutomationProperties.SetAutomationId(combo, "MigrationBranch_" + source.SourceId);
                        foreach (var candidate in source.Candidates) combo.Items.Add(new ComboBoxItem { Content = candidate.Name + " · " + candidate.VersionId.ToString()[..8], Tag = candidate });
                        combo.SelectedIndex = source.SuggestedUpdateId is { } suggested ? source.Candidates.ToList().FindIndex(c => c.UpdateId == suggested) : -1;
                        branches.Add(source.SourceId, combo); panel.Children.Add(combo);
                        if (!source.HasHistory || source.RequiresReconciliation) panel.Children.Add(new TextBlock { Text = I18n.GetString(source.HasHistory ? "Migration_BranchReconciliation" : "Migration_NoHistoricalBranch"), TextWrapping = TextWrapping.Wrap });
                    }
                    var dialog = new ContentDialog { Title = config.Name, Content = new ScrollViewer { Content = panel, MaxHeight = 450 },
                        PrimaryButtonText = I18n.GetString("Migration_VerifyAndContinue"), CloseButtonText = I18n.GetString("Common_Cancel"),
                        DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
                    bool HasBranchSelection() => context.Continuation.Sources.All(s => !s.HasHistory || branches[s.SourceId].SelectedItem is ComboBoxItem);
                    dialog.IsPrimaryButtonEnabled = HasBranchSelection();
                    foreach (var branch in branches.Values) branch.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = HasBranchSelection();
                    ThemeService.ApplyThemeToDialog(dialog);
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) continue;
                    var candidates = files.Values.Where(c => c.SelectedItem is ComboBoxItem).Select(c => (HistoryReplicaRecoveryCandidate)((ComboBoxItem)c.SelectedItem).Tag).ToArray();
                    var selections = branches.Select(p => new HistoryContinuationSelection(p.Key, p.Value.SelectedItem is ComboBoxItem item ? ((HistoryContinuationCandidate)item.Tag).UpdateId : null)).ToArray();
                    var result = await DataMigrationService.CompleteRecoveryAsync(context, candidates, selections, token);
                    reports.Add(I18n.Format("Migration_RecoveryResult", config.Name, result.Replicas.RegisteredReplicas, result.Replicas.Versions.Count(v => v.Readiness == HistoryReadiness.Ready),
                        result.Replicas.Versions.Length, result.Workspace.SourceBaselines.Count(s => s.RequiresFullCapture)));
                    reports.AddRange(result.Replicas.Failures.Select(f => f.RepresentationId + ": " + f.Diagnostic));
                }
                catch (Exception error) { reports.Add(row.Name + ": " + CloudCommandSecurity.Redact(error.Message)); }
            }
            await RefreshAssessmentsAsync(CancellationToken.None); ShowInfoBar(string.Join("\n\n", reports) + "\n" + I18n.GetString("Migration_ConfirmCapabilitiesNext"), InfoBarSeverity.Informational);
        }
        catch (Exception error) { ReportMigrationError(error); }
        finally { SetMigrationBusy(false); }
    }

    private async Task<bool> PrepareMigrationPasswordAsync(BackupConfig config, CancellationToken token)
    {
        var password = new PasswordBox { Header = I18n.GetString("Migration_OriginalPassword"), PlaceholderText = I18n.GetString("Encryption_EnterPasswordPlaceholder") };
        AutomationProperties.SetAutomationId(password, "MigrationOriginalPassword");
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(new TextBlock { Text = I18n.GetString("Migration_PasswordDescription"), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(password);
        if (!await ShowMigrationDialogAsync(config.Name, panel, I18n.GetString("Migration_VerifyPassword"))) return false;
        var candidate = password.Password; password.Password = "";
        var success = await BackupPasswordPreparationService.VerifyAndStoreAsync(config, candidate, token);
        if (!success) ShowInfoBar(I18n.GetString("Migration_PasswordVerificationFailed"), InfoBarSeverity.Error);
        return success;
    }
}
