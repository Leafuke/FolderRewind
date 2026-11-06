using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class CloudRecoveryPage : Page
{
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => ContentLayout.Width = Math.Min(860, Math.Max(0, e.NewSize.Width - 48));
    private CloudWizardSession? _session;
    private bool _active;
    private long _navigation;
    private CloudSetupNavigationParameter _options = new();
    public CloudSetupViewModel ViewModel { get; private set; } = null!;
    public string PasswordLabel => I18n.GetString("CloudRecovery_Password.Header");
    public CloudRecoveryPage() => InitializeComponent();
    public InfoBarSeverity Severity(int value) => (InfoBarSeverity)value;
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        var navigation = ++_navigation;
        try
        {
            var options = (e.Parameter as CloudSetupNavigationParameter ?? new()) with { Intent = CloudSetupIntent.Recover };
            _options = options; StartupFailure.Visibility = Visibility.Collapsed;
            _session = CloudWizardSessions.Get(options); ViewModel = _session.ViewModel; ViewModel.RecoveryMode = true;
            if (!_session.Initialized)
            {
                var result = await ViewModel.InitializeWithResultAsync(options.ConfigId, profileId: options.ProfileId);
                if (!_active || navigation != _navigation || result == CloudSetupInitializationState.Canceled) return;
                if (result == CloudSetupInitializationState.TargetUnavailable) { ShowStartupFailure(ViewModel.Message); return; }
                _session.Initialized = true;
            }
            if (_active && navigation == _navigation) { Editor.Attach(ViewModel); Editor.Visibility = Visibility.Visible; Bindings.Update(); if (ViewModel.WizardStep == 5) ShowRecovery(); }
        }
        catch (Exception error)
        {
            RcloneCredentialDiagnosticReporter.Report(error, nameof(CloudRecoveryPage));
            if (_active && navigation == _navigation) ShowStartupFailure(I18n.GetString("CloudConnection_InitializationFailed"));
        }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false; _navigation++; RecoveryPassword.Password = ""; _session?.ViewModel.SuspendChecks();
        if (_session is { Initialized: false } session) { _session = null; CloudWizardSessions.End(session); }
        base.OnNavigatedFrom(e);
    }
    private void ShowStartupFailure(string message)
    { Editor.Visibility = RecoveryPanel.Visibility = Visibility.Collapsed; StartupError.Message = message; StartupFailure.Visibility = Visibility.Visible; }
    private void OnStartNew(object sender, RoutedEventArgs e)
    {
        var session = _session; _session = null;
        if (session is not null) CloudWizardSessions.End(session);
        NavigationService.NavigateTo("CloudRecovery", new CloudSetupNavigationParameter(_options.ConfigId, Intent: CloudSetupIntent.Recover));
    }
    private void OnStartupBack(object sender, RoutedEventArgs e)
    { if (Frame.CanGoBack) Frame.GoBack(); else NavigationService.NavigateTo("CloudConnections"); }
    private void ShowRecovery()
    {
        ViewModel.WizardStep = 5; Editor.Visibility = Visibility.Collapsed; RecoveryPanel.Visibility = Visibility.Visible;
        Reconnect.Visibility = ViewModel.IsProject ? Visibility.Visible : Visibility.Collapsed;
        DiscoverPanel.Visibility = ViewModel.IsProject ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnConnected(object? s, EventArgs e) => ShowRecovery();
    private void OnBack(object s, RoutedEventArgs e) { ViewModel.WizardStep = 0; RecoveryPanel.Visibility = Visibility.Collapsed; Editor.Visibility = Visibility.Visible; Editor.Refresh(); }
    private async Task Run(Func<CancellationToken, Task> action) { await ViewModel.RunCheckAsync(action); if (_active) Bindings.Update(); }
    private async void OnDiscover(object s, RoutedEventArgs e) => await Run(ViewModel.DiscoverRepositoriesAsync);
    private void OnCancelOperation(object s, RoutedEventArgs e) => ViewModel.CancelCheck();
    private async void OnAnalyze(object s, RoutedEventArgs e) => await Run(ViewModel.AnalyzeRepositoryAsync);
    private async void OnReconnect(object s, RoutedEventArgs e)
    {
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudSetup_DialogTitle"), I18n.Format("CloudRecovery_ReconnectConfirm", ViewModel.RemoteRoot), I18n.GetString("Common_Confirm"), XamlRoot)) await Run(ViewModel.ReconnectProjectHistoryAsync);
    }
    private async void OnRestore(object s, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit) return;
        try
        {
            CloudSetupViewModel.RecoveryConfirmation? confirmation = null;
            await Run(async t => confirmation = await ViewModel.PreviewRecoveryAsync(t));
            if (!_active || confirmation is null) return;
            var preview = CloudSetupViewModel.DescribeRecovery(confirmation.Preview);
            if (!confirmation.Preview.CanPrepare) { await AppDialogService.Default.ShowMessageAsync(I18n.GetString("Export_Title"), preview, XamlRoot); return; }
            var parent = await MainWindowService.PickFolderPathAsync(I18n.GetString("Export_Title"), "FolderRewind.CloudRecovery.Target", MainWindowService.SuggestedPickerLocation.ComputerFolder);
            if (!_active || string.IsNullOrEmpty(parent)) return;
            var target = Path.Combine(parent, "FolderRewind-restored-" + Guid.NewGuid().ToString("N"));
            if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Export_Title"), preview + "\n\n" + I18n.Format("CloudRecovery_DestinationConfirm", target), I18n.GetString("Common_Confirm"), XamlRoot)) return;
            var password = RecoveryPassword.Password; RecoveryPassword.Password = "";
            await ViewModel.RestoreAnalyzedAsync(target, string.IsNullOrEmpty(password) ? null : password, confirmation);
        }
        catch (Exception ex) { if (_active) ViewModel.Message = CloudCommandSecurity.Redact(ex.Message); }
    }
    private void OnFinish(object s, RoutedEventArgs e)
    {
        if (_session is not null) CloudWizardSessions.End(_session);
        if (Frame.CanGoBack) Frame.GoBack(); else NavigationService.NavigateTo("Home");
    }
}
