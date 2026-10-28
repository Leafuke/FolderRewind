using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class CloudProjectPage : Page
{
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => ContentLayout.Width = Math.Min(860, Math.Max(0, e.NewSize.Width - 48));
    public CloudSetupViewModel ViewModel { get; } = new();
    private bool _refreshing, _active;
    public InfoBarSeverity Severity(int value) => (InfoBarSeverity)value;
    public CloudProjectPage() => InitializeComponent();
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        var id = (e.Parameter as CloudSetupNavigationParameter)?.ConfigId ?? (e.Parameter as ConfigSettingsNavigationParameter)?.ConfigId ?? "";
        ViewModel.PropertyChanged += OnChanged;
        ViewModel.RequestedJobId = (e.Parameter as CloudSetupNavigationParameter)?.JobId;
        await ViewModel.InitializeAsync(id); if (_active) Refresh();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { _active = false; ViewModel.PropertyChanged -= OnChanged; ViewModel.Dispose(); base.OnNavigatedFrom(e); }
    private void OnChanged(object? s, PropertyChangedEventArgs e) { if (_active) Refresh(); }
    private void Refresh()
    {
        _refreshing = true;
        ProjectSummary.Text = I18n.Format("CloudStatus_ProjectConnection", ViewModel.ProjectName, ViewModel.CurrentConnectionSummary, ViewModel.RemoteRoot, ViewModel.AutomaticSummary);
        Automatic.IsOn = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(p => p.Id == ViewModel.ProjectId)?.Cloud.Enabled == true;
        Automatic.IsEnabled = ViewModel.CanEdit; Bindings.Update(); _refreshing = false;
    }
    private async Task Run(Func<CancellationToken, Task> action) { await ViewModel.RunCheckAsync(action); if (_active) Refresh(); }
    private void OnConnect(object s, RoutedEventArgs e) => NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(ViewModel.ProjectId));
    private void OnEdit(object s, RoutedEventArgs e)
    {
        var id = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(p => p.Id == ViewModel.ProjectId)?.Cloud.ConnectionProfileId;
        if (id is not null) NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(ViewModel.ProjectId, ManageConnection: true, ProfileId: id));
    }
    private async void OnAutomatic(object s, RoutedEventArgs e) { if (!_refreshing && _active) { var enabled = Automatic.IsOn; await Run(t => ViewModel.SetAutomaticUploadAsync(enabled, t)); } }
    private async void OnLoadVersions(object s, RoutedEventArgs e) => await Run(ViewModel.LoadVersionsAsync);
    private async void OnRefresh(object s, RoutedEventArgs e) => await Run(ViewModel.RefreshJobsAsync);
    private async void OnRetry(object s, RoutedEventArgs e) => await Run(ViewModel.RetryJobsAsync);
    private async void OnCancelJob(object s, RoutedEventArgs e) => await Run(t => ViewModel.CancelSelectedJobAsync(false, t));
    private async void OnUpload(object s, RoutedEventArgs e)
    {
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudSetup_DialogTitle"), I18n.Format("CloudSetup_UploadConfirm", ViewModel.RemoteRoot), I18n.GetString("Common_Confirm"), XamlRoot)) await ViewModel.UploadAsync();
    }
    private async void OnAbandon(object s, RoutedEventArgs e)
    {
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudJob_AbandonTitle"), I18n.GetString("CloudJob_AbandonDescription"), I18n.GetString("Common_Confirm"), XamlRoot)) await Run(t => ViewModel.CancelSelectedJobAsync(true, t));
    }
    private async void OnInitializeMissing(object s, RoutedEventArgs e)
    {
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudJob_AbandonTitle"), I18n.GetString("CloudJob_InitializeMissingDescription"), I18n.GetString("Common_Confirm"), XamlRoot)) await Run(ViewModel.InitializeMissingAdmissionAsync);
    }
    private void OnRecovery(object s, RoutedEventArgs e) => NavigationService.NavigateTo("CloudRecovery", new CloudSetupNavigationParameter(ViewModel.ProjectId, Intent: CloudSetupIntent.Recover));
}
