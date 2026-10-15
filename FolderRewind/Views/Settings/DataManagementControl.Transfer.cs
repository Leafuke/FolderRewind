using FolderRewind.Services;
using Microsoft.UI.Xaml;

namespace FolderRewind.Views.Settings;

public sealed partial class DataManagementControl
{
    private static void OpenMigration(MigrationWizardRequest request) => NavigationService.NavigateTo("Migration", request);
    private void OnExportDataClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Export));
    private void OnImportDataClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Import));
    private void OnContinueMigrationClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Continue));
    private void OnExportHistoryClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Export, DataMigrationScope.History));
    private void OnImportHistoryClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Import, DataMigrationScope.History));
    private void OnImportConfigClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Import, DataMigrationScope.Configuration));
    private void OnExportMetadataClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Export, DataMigrationScope.MetadataOnly));
    private void OnImportMetadataClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.Import, DataMigrationScope.MetadataOnly));
    private void OnReadOriginalClick(object sender, RoutedEventArgs e) => OpenMigration(new(MigrationWizardMode.OriginalSettings, DataMigrationScope.Configuration));
}