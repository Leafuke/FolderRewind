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
    private async void OnLegacyMigrationLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.TryGetCurrentConfig(out var config) || config is null) return;
            var report = await NativeHistoryCoreGateway.RecheckLegacyAsync(config);
            LegacyMigrationNotice.Title = I18n.GetString("LegacyMigration_Title");
            LegacyMigrationNotice.Message = I18n.GetString("LegacyMigration_Notice");
            LegacyMigrationNotice.IsOpen = report.Items.Length > 0 || report.InputStatus == "Unreadable";
        }
        catch (Exception ex) { ViewModel.ReportLoadFailure(ex.Message); }
    }

    private async void OnLegacyMigrationClick(object sender, RoutedEventArgs e)
    {
        if (_legacyReportOpen || !ViewModel.TryGetCurrentConfig(out var config) || config is null) return;
        _legacyReportOpen = true;
        try
        {
            var store = new LegacyTakeoverService(ConfigService.ConfigDirectory, new HistoryConfigId(config.Id));
            while (true)
            {
                var report = await NativeHistoryCoreGateway.RecheckLegacyAsync(config);
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = I18n.GetString("LegacyMigration_Notice"), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = string.Join(" · ", report.Items.GroupBy(i => i.Status)
                    .Select(g => I18n.GetString("LegacyMigration_Status_" + g.Key) + ": " + g.Count())) + "\n" + report.InputStatus + " · " + report.OperationStatus + "\n" + report.Diagnostic,
                    TextWrapping = TextWrapping.Wrap });
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
                async Task VerifySelectionAsync()
                {
                    if (records.SelectedIndex < 0) return;
                    var item = report.Items[records.SelectedIndex];
                    verify.IsEnabled = false;
                    records.IsEnabled = false;
                    try
                    {
                        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, verificationCancellation.Token);
                        var task = Task.Run(() => store.VerifyAsync(runtime, item.OriginKey,
                            new SevenZipHistoryArchiveBackend(config), verificationCancellation.Token));
                        var verified = await task;
                        detail.Text = I18n.GetString("LegacyMigration_Status_" + verified.Status) + "\n" + verified.Diagnostic;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { detail.Text = ex.Message; }
                    finally
                    {
                        if (!verificationCancellation.IsCancellationRequested)
                        { verify.IsEnabled = true; records.IsEnabled = true; }
                    }
                }
                verify.Click += async (_, _) => { verificationTask = VerifySelectionAsync(); await verificationTask; };
                var browse = new Button { Content = I18n.GetString("LegacyMigration_Browse") };
                AutomationProperties.SetAutomationId(browse, "LegacyMigrationBrowse");
                browse.Click += async (_, _) =>
                {
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
                    verify.IsEnabled = item.SourceId is not null && verificationTask?.IsCompleted != false;
                    detail.Text = item.Diagnostic + "\n" + string.Join("\n", item.Candidates);
                    sources.SelectedIndex = 0;
                    sources.IsEnabled = item.Status == "Unassigned";
                    location.Text = report.ArchiveSelections.GetValueOrDefault(item.OriginKey, "");
                };
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
                    var item = report.Items[records.SelectedIndex];
                    if (item.Status == "Unassigned" && sources.SelectedIndex > 0)
                        report.SourceSelections[item.OriginKey] = Guid.Parse(sourceChoices[sources.SelectedIndex - 1].Id);
                    if (!string.IsNullOrWhiteSpace(location.Text))
                        report.ArchiveSelections[item.OriginKey] = System.IO.Path.GetFullPath(location.Text.Trim());
                    store.SaveReport(report);
                }
            }
        }
        catch (Exception ex) { ViewModel.ReportLoadFailure(ex.Message); }
        finally { _legacyReportOpen = false; }
    }
}
