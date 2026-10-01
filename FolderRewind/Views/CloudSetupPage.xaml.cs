using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class CloudSetupPage : Page
{
    public CloudSetupViewModel ViewModel { get; } = new();
    private bool _active;
    private readonly System.Threading.CancellationTokenSource _pageLifetime = new();
    public CloudSetupPage() { InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        try { ViewModel.Initialize((e.Parameter as ConfigSettingsNavigationParameter)?.ConfigId ?? ""); }
        catch (Exception ex) { ViewModel.Message = ex.Message; }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _active = false; _pageLifetime.Cancel(); ViewModel.Dispose(); base.OnNavigatedFrom(e); }
    private void OnOpenListEnvironment(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("Settings", NavigationService.SettingsOpenListTarget);
    private void OnRepair(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit || sender is not Button { Tag: OnboardingDiagnosticItem item }) return;
        if (!ViewModel.IsDiagnosticCurrent(item)) { ViewModel.Message = I18n.GetString("SettingsProject_Stale"); return; }
        switch (item.Target)
        {
            case OnboardingRepairTarget.CloudConnection: ConfigPathBox.StartBringIntoView(); ConfigPathBox.Focus(FocusState.Programmatic); break;
            case OnboardingRepairTarget.OpenListEnvironment: OnOpenListEnvironment(sender, e); break;
        }
    }
    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try { await OnboardingDiagnosticsInteraction.ExportAsync(ViewModel.Diagnostics, XamlRoot, _pageLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_active) ViewModel.Message = CloudCommandSecurity.Redact(ex.Message); }
    }
    private async void OnPickExecutable(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(async token =>
    {
        var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.CloudSetup.Executable", [".exe"], MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (_active && path is not null) ViewModel.Executable = path;
    });
    private async void OnPickConfig(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(async token =>
    {
        var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.CloudSetup.Config", [".conf"], MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (_active && path is not null) { ViewModel.ConfigPath = path; ViewModel.LoadRemotes(); }
    });
    private async void OnCreateWebDav(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text; var user = UserBox.Text; var password = Password.Password;
        Password.Password = "";
        var allowInsecure = AllowInsecure.IsChecked == true;
        await ViewModel.RunCheckAsync(token => ViewModel.CreateWebDavAsync(url, user, password, token, allowInsecure));
    }
    private async void OnListRemotes(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(token => { ViewModel.LoadRemotes(); return Task.CompletedTask; });
    private async void OnBrowse(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.BrowseAsync);
    private async void OnBrowseMore(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.BrowseMoreAsync);
    private void OnEnterDirectory(object sender, ItemClickEventArgs e) { if (ViewModel.CanEdit && e.ClickedItem is string name) ViewModel.EnterDirectory(name); }
    private async void OnVerifyWrite(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.VerifyWriteAsync);
    private async void OnSave(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.SaveAsync);
    private async void OnLoadVersions(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.LoadVersionsAsync);
    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudSetup_Title.Text"), I18n.Format("CloudSetup_UploadConfirm", ViewModel.RemoteRoot), I18n.GetString("Common_Confirm"), XamlRoot))
            await ViewModel.UploadAsync();
    }
    private void OnOpenManagement(object sender, RoutedEventArgs e)
    {
        try { var uri = RcloneConnectionService.ValidateWebDavUrl(ManagementUrl.Text); ShellPathService.TryOpenPath(uri.AbsoluteUri, out _); }
        catch (Exception ex) { ViewModel.Message = ex.Message; }
    }
    private void OnCancelCheck(object sender, RoutedEventArgs e) => ViewModel.CancelCheck();
    private async void OnDiscoverRepositories(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.DiscoverRepositoriesAsync);
    private async void OnAnalyzeRepository(object sender, RoutedEventArgs e) => await ViewModel.RunCheckAsync(ViewModel.AnalyzeRepositoryAsync);
    private async void OnRestoreNew(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit || ViewModel.SelectedVersionIndex < 0 || ViewModel.SelectedVersionIndex >= ViewModel.Versions.Count) return;
        try
        {
            CloudSetupViewModel.RecoveryConfirmation? confirmation = null;
            await ViewModel.RunCheckAsync(async token => confirmation = await ViewModel.PreviewRecoveryAsync(token));
            if (!_active || confirmation is null) return;
            var previewText = CloudSetupViewModel.DescribeRecovery(confirmation.Preview);
            if (!confirmation.Preview.CanPrepare)
            {
                await AppDialogService.Default.ShowMessageAsync(I18n.GetString("Export_Title"), previewText, XamlRoot);
                return;
            }
            var parent = await MainWindowService.PickFolderPathAsync(I18n.GetString("Export_Title"), "FolderRewind.CloudRecovery.Target", MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (!_active || string.IsNullOrEmpty(parent)) return;
            var target = System.IO.Path.Combine(parent, "FolderRewind-restored-" + Guid.NewGuid().ToString("N"));
            if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Export_Title"),
                previewText + "\n\n" + I18n.Format("CloudRecovery_Confirm", confirmation.Preview.Version.VersionId, target, ViewModel.RemoteRoot), I18n.GetString("Common_Confirm"), XamlRoot)) return;
            var password = RecoveryPassword.Password; RecoveryPassword.Password = "";
            if (_active) await ViewModel.RestoreAnalyzedAsync(target, string.IsNullOrEmpty(password) ? null : password, confirmation);
        }
        catch (Exception ex) { if (_active) ViewModel.Message = CloudCommandSecurity.Redact(ex.Message); }
    }
}
