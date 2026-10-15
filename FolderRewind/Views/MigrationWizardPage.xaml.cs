using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class MigrationWizardPage : Page
{
    public MigrationWizardViewModel ViewModel { get; } = new();
    public MigrationWizardPage()
    {
        InitializeComponent();
        ViewModel.PickPackage += extensions => MainWindowService.PickFilePathAsync("", "FolderRewind.Migration.Wizard.Import", extensions,
            MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
        ViewModel.PickExport += extension => MainWindowService.PickSaveFilePathAsync("", "FolderRewind.Migration.Wizard.Export",
            new Dictionary<string, IReadOnlyList<string>> { ["FolderRewind"] = [extension] }, extension == ".json" ? "FolderRewind_config" : "FolderRewind_migration",
            MainWindowService.SuggestedPickerLocation.DocumentsLibrary);
        ViewModel.Restart += async () => { Program.RestartOnExit = true; try { await App.ExitApplicationAsync(); } catch { Program.RestartOnExit = false; throw; } };
    }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try { await ViewModel.InitializeAsync(e.Parameter as MigrationWizardRequest ?? new(MigrationWizardMode.Import)); }
        catch (Exception error) { NotificationService.ShowError(I18n.GetString("MigrationWizard_ActionFailed")); LogService.LogError("Could not open migration wizard.", nameof(MigrationWizardPage), error); }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { TaskObserver.Observe(ViewModel.SaveDraftAsync(), nameof(MigrationWizardPage)); OriginalPassword.Password = ""; ViewModel.Dispose(); base.OnNavigatedFrom(e); }
    private async void OnPrimary(object sender, RoutedEventArgs e) => await ViewModel.PrimaryAsync();
    private async void OnChoosePackage(object sender, RoutedEventArgs e) => await ViewModel.ChoosePackageAsync();
    private async void OnLoadSnapshots(object sender, RoutedEventArgs e) => await ViewModel.LoadSnapshotsAsync();
    private async void OnHandleProject(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id } && ViewModel.Projects.FirstOrDefault(p => p.Id == id) is { } project) await ViewModel.SelectProjectAsync(project); }
    private async void OnChooseArchive(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string id } && ViewModel.Archives.FirstOrDefault(a => a.Id == id) is { } archive) await ViewModel.AddArchiveAsync(archive); }
    private async void OnVerifyPassword(object sender, RoutedEventArgs e)
    { var password = OriginalPassword.Password; OriginalPassword.Password = ""; await ViewModel.VerifyPasswordAsync(password); }
    private async void OnAttachPackage(object sender, RoutedEventArgs e) => await ViewModel.AttachPackageAsync();
    private async void OnRetainMissing(object sender, RoutedEventArgs e) => await ViewModel.RetainMissingAsync();
    private async void OnBrowseSource(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MigrationWizardSourceItem source }) return;
        var path = await PickDirectoryAsync(); if (path is not null) source.Path = path;
    }
    private async void OnBrowseBackupRoot(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || ViewModel.Projects.FirstOrDefault(p => p.Id == id) is not { } project) return;
        var path = await PickDirectoryAsync(); if (path is not null) project.BackupRoot = path;
    }
    private async void OnBrowseIssueRoot(object sender, RoutedEventArgs e)
    { var path = await PickDirectoryAsync(); if (path is not null) ViewModel.SelectedBackupRoot = path; }
    private static async Task<string?> PickDirectoryAsync()
    {
        try { return await MainWindowService.PickFolderPathAsync("", "FolderRewind.Migration.Wizard.Path"); }
        catch (Exception error) { NotificationService.ShowError(I18n.GetString("MigrationWizard_ActionFailed")); LogService.LogError("Could not select migration directory.", nameof(MigrationWizardPage), error); return null; }
    }
    private void OnApplyMapping(object sender, RoutedEventArgs e) => ViewModel.ApplyRootMapping();
    private void OnBack(object sender, RoutedEventArgs e) => ViewModel.GoBack();
    private void OnCancel(object sender, RoutedEventArgs e) => ViewModel.Cancel();
    private async void OnLeave(object sender, RoutedEventArgs e) { await ViewModel.SaveDraftAsync(); NavigationService.NavigateTo("Settings"); }
    private void OnManageConnections(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("CloudConnections");
    private void OnReconcile(object sender, RoutedEventArgs e) => ViewModel.OpenReconciliation();
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotVisible(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
