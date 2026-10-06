using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Migration;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class HistoryPage
{
    private bool _legacyReportOpen;
    private bool _legacyNoticeActive;
    private long _legacyNoticeRequestId;
    private LegacyMigrationNoticeState? _legacyNoticeState;
    private string[] _legacyAttentionKeys = [];
    private Task _legacyNoticeSaveTask = Task.CompletedTask;

    private async void OnLegacyMigrationLoaded(object sender, RoutedEventArgs e)
        => await RefreshLegacyMigrationNoticeAsync();

    private void ResetLegacyMigrationNotice()
    {
        _legacyNoticeRequestId++;
        _legacyNoticeState = null;
        _legacyAttentionKeys = [];
        LegacyMigrationNotice.IsOpen = false;
        LegacyMigrationNotice.Visibility = Visibility.Collapsed;
        HistoryMoreActionsButton.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshLegacyMigrationNoticeAsync()
    {
        if (!_legacyNoticeActive) return;
        var requestId = ++_legacyNoticeRequestId;
        try
        {
            if (!ViewModel.TryGetCurrentConfig(out var config) || config is null) return;
            var configDirectory = ConfigService.ConfigDirectory;
            var configId = new HistoryConfigId(config.Id);
            var state = new LegacyMigrationNoticeState(configDirectory, configId);
            await _legacyNoticeSaveTask;
            var result = await Task.Run(() =>
            {
                var report = new LegacyTakeoverService(configDirectory, configId).ReadReport();
                var keys = LegacyMigrationNoticeState.AttentionKeys(report);
                return (report, keys, show: state.ShouldShow(keys));
            });
            if (!_legacyNoticeActive || requestId != _legacyNoticeRequestId
                || !ViewModel.TryGetCurrentConfig(out var current) || current?.Id != config.Id) return;
            _legacyNoticeState = state;
            _legacyAttentionKeys = result.keys;
            HistoryMoreActionsButton.Visibility = LegacyMigrationNoticeState.HasReport(result.report)
                ? Visibility.Visible : Visibility.Collapsed;
            LegacyMigrationNotice.Title = I18n.GetString("LegacyMigration_ShortTitle");
            var pending = LegacyMigrationNoticeState.PendingCount(result.report);
            LegacyMigrationNotice.Message = pending > 0
                ? I18n.Format("LegacyMigration_PendingNotice", pending)
                : I18n.GetString("LegacyMigration_ReportProblemNotice");
            LegacyMigrationNotice.IsOpen = result.show;
            LegacyMigrationNotice.Visibility = result.show ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LogService.LogError("[HistoryPage] Loading migration notice failed: " + ex.Message, nameof(HistoryPage), ex);
        }
    }

    private async void OnLegacyMigrationNoticeClose(InfoBar sender, object args)
    {
        sender.Visibility = Visibility.Collapsed;
        var state = _legacyNoticeState;
        var keys = _legacyAttentionKeys;
        if (state is null) return;
        // Invalidate an in-flight refresh so it cannot reopen the notice before the write completes.
        _legacyNoticeRequestId++;
        _legacyNoticeSaveTask = SaveLegacyMigrationNoticeAsync(state, keys);
        await _legacyNoticeSaveTask;
    }

    private static async Task SaveLegacyMigrationNoticeAsync(LegacyMigrationNoticeState state, string[] keys)
    {
        try { await Task.Run(() => state.Dismiss(keys)); }
        catch (Exception ex)
        {
            LogService.LogError("[HistoryPage] Saving migration notice preference failed: " + ex.Message, nameof(HistoryPage), ex);
        }
    }

    private async void OnLegacyMigrationClick(object sender, RoutedEventArgs e)
    {
        if (_legacyReportOpen || !ViewModel.TryGetCurrentConfig(out var config) || config is null) return;
        _legacyReportOpen = true;
        try
        {
            var store = new LegacyTakeoverService(ConfigService.ConfigDirectory, new HistoryConfigId(config.Id));
            string? selectedOrigin = null;
            while (true)
            {
                var report = await NativeHistoryCoreGateway.RecheckLegacyAsync(config);
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = I18n.GetString("LegacyMigration_Notice"), TextWrapping = TextWrapping.Wrap });
                var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
                string ReportSummary() => string.Join(" · ", report.Items.GroupBy(i => i.Status)
                    .Select(g => I18n.GetString("LegacyMigration_Status_" + g.Key) + ": " + g.Count()))
                    + "\n" + report.InputStatus + " · " + report.OperationStatus + "\n" + report.Diagnostic;
                summary.Text = ReportSummary();
                panel.Children.Add(summary);
                var records = new ComboBox { Header = I18n.GetString("LegacyMigration_Record"), HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var item in report.Items) records.Items.Add(item.Original.FolderName + " / " + item.Original.FileName + " · " + I18n.GetString("LegacyMigration_Status_" + item.Status));
                var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                var sources = new ComboBox { Header = I18n.GetString("LegacyMigration_Source"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
                var sourceChoices = config.SourceFolders.Select(folder => (folder.Id, folder.DisplayName, folder.Path)).ToArray();
                sources.Items.Add(I18n.GetString("LegacyMigration_KeepSource"));
                foreach (var folder in sourceChoices) sources.Items.Add(folder.DisplayName + " — " + folder.Path);
                sources.SelectedIndex = 0;
                var location = new TextBox { Header = I18n.GetString("LegacyMigration_Path") };
                AutomationProperties.SetAutomationId(records, "LegacyMigrationRecord");
                AutomationProperties.SetAutomationId(sources, "LegacyMigrationSource");
                AutomationProperties.SetAutomationId(location, "LegacyMigrationArchivePath");
                using var verificationCancellation = new CancellationTokenSource();
                Task? verificationTask = null;
                var verify = new Button { Content = I18n.GetString("LegacyMigration_Verify"), IsEnabled = false };
                AutomationProperties.SetAutomationId(verify, "LegacyMigrationVerify");
                void SaveSelection()
                {
                    if (records.SelectedIndex < 0) return;
                    var item = report.Items[records.SelectedIndex];
                    selectedOrigin = item.OriginKey;
                    // Merge into the current report so a completed verification cannot be overwritten by the dialog's snapshot.
                    var current = store.ReadReport();
                    if (item.Status == "Unassigned" && sources.SelectedIndex > 0)
                        current.SourceSelections[item.OriginKey] = Guid.Parse(sourceChoices[sources.SelectedIndex - 1].Id);
                    if (!string.IsNullOrWhiteSpace(location.Text))
                        current.ArchiveSelections[item.OriginKey] = System.IO.Path.GetFullPath(location.Text.Trim());
                    store.SaveReport(current);
                }
                async Task VerifySelectionAsync()
                {
                    if (records.SelectedIndex < 0) return;
                    var item = report.Items[records.SelectedIndex];
                    verify.IsEnabled = false;
                    records.IsEnabled = false;
                    sources.IsEnabled = false;
                    location.IsEnabled = false;
                    try
                    {
                        SaveSelection();
                        report = await NativeHistoryCoreGateway.RecheckLegacyAsync(config);
                        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, verificationCancellation.Token);
                        var task = Task.Run(() => store.VerifyAsync(runtime, item.OriginKey,
                            new SevenZipHistoryArchiveBackend(config), verificationCancellation.Token));
                        var verified = await task;
                        report = store.ReadReport();
                        summary.Text = ReportSummary();
                        var selection = Array.FindIndex(report.Items, i => i.OriginKey == item.OriginKey);
                        if (selection >= 0)
                        {
                            records.Items[selection] = verified.Original.FolderName + " / " + verified.Original.FileName
                                + " · " + I18n.GetString("LegacyMigration_Status_" + verified.Status);
                            records.SelectedIndex = selection;
                        }
                        detail.Text = I18n.GetString("LegacyMigration_Status_" + verified.Status) + "\n" + verified.Diagnostic;
                        await ViewModel.RefreshCurrentHistoryAsync(verificationCancellation.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { detail.Text = ex.Message; }
                    finally
                    {
                        if (!verificationCancellation.IsCancellationRequested)
                        {
                            verify.IsEnabled = records.SelectedIndex >= 0 && report.Items[records.SelectedIndex].SourceId is not null;
                            records.IsEnabled = true;
                            sources.IsEnabled = records.SelectedIndex >= 0 && report.Items[records.SelectedIndex].Status == "Unassigned";
                            location.IsEnabled = true;
                        }
                    }
                }
                verify.Click += async (_, _) => { verificationTask = VerifySelectionAsync(); await verificationTask; };
                var browse = new Button { Content = I18n.GetString("LegacyMigration_Browse") };
                AutomationProperties.SetAutomationId(browse, "LegacyMigrationBrowse");
                browse.Click += async (_, _) =>
                {
                    if (verificationTask?.IsCompleted == false) return;
                    try
                    {
                        var selected = await MainWindowService.PickFilePathAsync(I18n.GetString("LegacyMigration_Browse"), "Legacy182Archive", [".7z", ".zip"]);
                        if (selected is not null) location.Text = selected;
                    }
                    catch (Exception ex) { detail.Text = ex.Message; }
                };
                records.SelectionChanged += (_, _) =>
                {
                    if (records.SelectedIndex < 0) return;
                    var item = report.Items[records.SelectedIndex];
                    selectedOrigin = item.OriginKey;
                    verify.IsEnabled = item.SourceId is not null && verificationTask?.IsCompleted != false;
                    detail.Text = item.Diagnostic + "\n" + string.Join("\n", item.Candidates);
                    sources.SelectedIndex = 0;
                    sources.IsEnabled = item.Status == "Unassigned";
                    location.Text = report.ArchiveSelections.GetValueOrDefault(item.OriginKey, "");
                };
                if (selectedOrigin is not null)
                    records.SelectedIndex = Array.FindIndex(report.Items, i => i.OriginKey == selectedOrigin);
                panel.Children.Add(records); panel.Children.Add(detail); panel.Children.Add(verify); panel.Children.Add(sources); panel.Children.Add(location); panel.Children.Add(browse);
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = I18n.GetString("LegacyMigration_Title"),
                    Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
                    PrimaryButtonText = I18n.GetString("LegacyMigration_Save"),
                    SecondaryButtonText = I18n.GetString("LegacyMigration_Recheck"), CloseButtonText = I18n.GetString("Common_Close"),
                    DefaultButton = ContentDialogButton.Close
                };
                var result = await dialog.ShowAsync();
                verificationCancellation.Cancel();
                if (verificationTask is not null)
                    try { await verificationTask; } catch (OperationCanceledException) { }
                if (result == ContentDialogResult.None) break;
                if (result == ContentDialogResult.Primary && records.SelectedIndex >= 0)
                {
                    SaveSelection();
                }
            }
        }
        catch (Exception ex) { ViewModel.ReportLoadFailure(ex.Message); }
        finally
        {
            _legacyReportOpen = false;
            await ViewModel.RefreshCurrentHistoryAsync();
            await RefreshLegacyMigrationNoticeAsync();
        }
    }
}
