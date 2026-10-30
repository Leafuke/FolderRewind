using FolderRewind.Services;
using FolderRewind.Models;
using FolderRewind.History.Domain;
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
        ViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ViewModel.ShowPendingRestart)) UpdateRecoveryLayout(); };
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
    private async void OnContinueRemaining(object sender, RoutedEventArgs e) => await ViewModel.ContinueRemainingAsync();
    private async void OnChoosePackage(object sender, RoutedEventArgs e) => await ViewModel.ChoosePackageAsync();
    private async void OnLoadSnapshots(object sender, RoutedEventArgs e) => await ViewModel.LoadSnapshotsAsync();
    private async void OnHandleProject(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || ViewModel.Projects.FirstOrDefault(p => p.Id == id) is not { } project) return;
        if (ViewModel.SelectedProject?.Id != id) OriginalPassword.Password = "";
        await ViewModel.SelectProjectAsync(project);
        if (ViewModel.ShowProjectDetails) RecoveryDetails.Focus(FocusState.Programmatic);
    }
    private void OnBackToProjects(object sender, RoutedEventArgs e)
    {
        OriginalPassword.Password = "";
        ViewModel.ReturnToProjects();
        if (ViewModel.SelectedProject is { } project) RecoveryProjects.ScrollIntoView(project);
        RecoveryProjects.Focus(FocusState.Programmatic);
    }
    private void OnRecoverySizeChanged(object sender, SizeChangedEventArgs e)
    { ViewModel.SetRecoveryWidth(e.NewSize.Width); UpdateRecoveryLayout(); }
    private void UpdateRecoveryLayout()
    {
        if (RecoveryWorkspace is null) return;
        var wide = ViewModel.WideRecovery && !ViewModel.ShowPendingRestart;
        RecoveryWorkspace.ColumnDefinitions[0].Width = wide ? new GridLength(260) : new GridLength(1, GridUnitType.Star);
        RecoveryWorkspace.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(RecoveryDetails, wide ? 1 : 0);
    }
    private void OnFooterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 640;
        Grid.SetRow(WizardPrimary, narrow ? 1 : 0);
        Grid.SetColumn(WizardPrimary, narrow ? 0 : 3);
        Grid.SetColumnSpan(WizardPrimary, narrow ? 4 : 1);
    }
    public static InfoBarSeverity StatusSeverity(SemanticStatus status) => status switch
    {
        SemanticStatus.Error => InfoBarSeverity.Error, SemanticStatus.Warning => InfoBarSeverity.Warning,
        SemanticStatus.Success => InfoBarSeverity.Success, _ => InfoBarSeverity.Informational
    };
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
    private void OnReconnectCloud(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOperate || ViewModel.SelectedProject is not { } project) return;
        var config = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => new HistoryConfigId(project.Id).Matches(c.Id));
        if (config is null) { NotificationService.ShowError(I18n.GetString("SettingsProject_Stale")); return; }
        var resume = ViewModel.ResumeRequest;
        var frame = Frame;
        if (!NavigationService.NavigateTo("CloudRecovery", new CloudSetupNavigationParameter(config.Id, Intent: CloudSetupIntent.Recover)) || resume is null || frame is null) return;
        // Returning from connection setup must resume the persisted workflow, including a newly imported one.
        var last = frame.BackStack.Count - 1;
        if (last >= 0 && frame.BackStack[last].SourcePageType == typeof(MigrationWizardPage))
            frame.BackStack[last] = new PageStackEntry(typeof(MigrationWizardPage), resume, frame.BackStack[last].NavigationTransitionInfo);
    }
    private async void OnRecheckCloud(object sender, RoutedEventArgs e) => await ViewModel.ContinueRemainingAsync();
    private void OnReconcile(object sender, RoutedEventArgs e) => ViewModel.OpenReconciliation();
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility NotVisible(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
