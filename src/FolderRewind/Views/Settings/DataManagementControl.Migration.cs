using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings;

public sealed partial class DataManagementControl
{
    private bool _migrationLoaded;
    private async void OnMigrationLoaded(object sender, RoutedEventArgs e)
    {
        _migrationLoaded = true;
        Unloaded -= OnMigrationUnloaded; Unloaded += OnMigrationUnloaded;
        MigrationWorkflowCoordinator.Changed -= OnWorkflowChanged; MigrationWorkflowCoordinator.Changed += OnWorkflowChanged;
        await RefreshWorkflowOverviewAsync();
    }
    private void OnMigrationUnloaded(object sender, RoutedEventArgs e)
    { _migrationLoaded = false; MigrationWorkflowCoordinator.Changed -= OnWorkflowChanged; }
    private void OnWorkflowChanged() => UiDispatcherService.Enqueue(() =>
    { if (_migrationLoaded) TaskObserver.Observe(RefreshWorkflowOverviewAsync(), nameof(DataManagementControl)); });
    private async Task RefreshWorkflowOverviewAsync()
    {
        try
        {
            var pending = await MigrationWorkflowCoordinator.PendingAsync();
            if (!_migrationLoaded) return;
            Migration.HasPending = pending.Length > 0;
            Migration.Summary = I18n.GetString(pending.Length > 0 ? "MigrationWizard_OverviewPending" : "MigrationWizard_OverviewReady");
        }
        catch (Exception error)
        { if (_migrationLoaded) ShowInfoBar(CloudCommandSecurity.Redact(error.Message), InfoBarSeverity.Error); }
    }
}