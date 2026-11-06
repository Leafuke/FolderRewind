using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace FolderRewind.Views;

public sealed partial class CloudSetupPage : Page
{
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => ContentLayout.Width = Math.Min(860, Math.Max(0, e.NewSize.Width - 48));
    private CloudWizardSession? _session;
    private bool _active;
    private long _navigation;
    private CloudSetupNavigationParameter _options = new();
    public CloudSetupPage() => InitializeComponent();
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        var navigation = ++_navigation;
        var options = e.Parameter as CloudSetupNavigationParameter ?? new((e.Parameter as ConfigSettingsNavigationParameter)?.ConfigId ?? "");
        _options = options;
        StartupFailure.Visibility = Visibility.Collapsed;
        try
        {
            if (options.Intent != CloudSetupIntent.Connect || options.ConfigId.Length == 0 && !options.ManageConnection)
            {
                var frame = Frame;
                var recovery = options.Intent != CloudSetupIntent.ProjectStatus;
                NavigationService.NavigateTo(recovery ? "CloudRecovery" : "CloudProject",
                    options with { Intent = recovery ? CloudSetupIntent.Recover : CloudSetupIntent.ProjectStatus });
                if (frame.BackStack.Count > 0 && frame.BackStack[^1].SourcePageType == typeof(CloudSetupPage)) frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
                return;
            }
            _session = CloudWizardSessions.Get(options); var vm = _session.ViewModel;
            vm.PropertyChanged += OnChanged;
            if (!_session.Initialized)
            {
                var result = await vm.InitializeWithResultAsync(options.ConfigId, manageConnection: options.ManageConnection, profileId: options.ProfileId);
                if (!_active || navigation != _navigation) return;
                if (result == CloudSetupInitializationState.Canceled) return;
                if (result == CloudSetupInitializationState.TargetUnavailable) { ShowStartupFailure(vm.Message); return; }
                if (options.ProfileId is not null)
                {
                    if (options.Reauthenticate || vm.NeedsReauthentication)
                    { if (!vm.BeginReauthentication()) { ShowStartupFailure(vm.Message); return; } }
                    else { vm.ConnectionMethod = 2; vm.WizardStep = 4; }
                }
                else vm.ApplyPreset(options.PresetProviderId);
                _session.Initialized = true;
            }
            if (_active && navigation == _navigation) { vm.ActivateEnvironment(); Editor.Attach(vm); Editor.Visibility = Visibility.Visible; RefreshResult(); }
        }
        catch (Exception error)
        {
            RcloneCredentialDiagnosticReporter.Report(error, nameof(CloudSetupPage));
            if (_active && navigation == _navigation) ShowStartupFailure(I18n.GetString("CloudConnection_InitializationFailed"));
        }
    }
    private void ShowStartupFailure(string message)
    { Editor.Visibility = ResultPanel.Visibility = Visibility.Collapsed; StartupError.Message = message; StartupFailure.Visibility = Visibility.Visible; }
    private void OnStartNew(object sender, RoutedEventArgs e)
    {
        var session = _session; _session = null;
        if (session is not null) { session.ViewModel.PropertyChanged -= OnChanged; CloudWizardSessions.End(session); }
        NavigationService.NavigateTo("CloudSetup", new CloudSetupNavigationParameter(_options.ConfigId, ManageConnection: _options.ManageConnection));
    }
    private void OnStartupBack(object sender, RoutedEventArgs e)
    { if (Frame.CanGoBack) Frame.GoBack(); else NavigationService.NavigateTo("CloudConnections"); }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _active = false; _navigation++;
        if (_session is not null)
        {
            _session.ViewModel.PropertyChanged -= OnChanged; _session.ViewModel.SuspendChecks();
            if (!_session.Initialized) { var session = _session; _session = null; CloudWizardSessions.End(session); }
        }
        base.OnNavigatedFrom(e);
    }
    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (_active) RefreshResult(); }
    private async void OnConfirmed(object? sender, EventArgs e)
    {
        if (_session is null || !_session.ViewModel.CanEdit) return;
        var vm = _session.ViewModel;
        if (vm.SaveImpactMessage.Length != 0 && !await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudSetup_DialogTitle"), vm.SaveImpactMessage, I18n.GetString("Common_Confirm"), XamlRoot)) return;
        var actionsReady = false;
        await vm.RunCheckAsync(async token =>
        {
            if (vm.RequestFirstUpload && vm.SelectedVersionIndex < 0) throw new InvalidOperationException(I18n.GetString("CloudSetup_SelectVersion"));
            await vm.SaveAsync(token);
            if (vm.LastSaveResult?.Success != true) return;
            vm.WizardStep = 5;
            if (vm.IsProject && vm.RequestAutomaticUpload) await vm.SetAutomaticUploadAsync(true, token);
            actionsReady = true;
        });
        if (actionsReady && vm.IsProject && vm.RequestFirstUpload) await vm.UploadAsync();
        if (_active) { Editor.Refresh(); RefreshResult(); }
    }
    private void RefreshResult()
    {
        if (_session is null) return;
        var vm = _session.ViewModel;
        ResultPanel.Visibility = vm.WizardStep == 5 ? Visibility.Visible : Visibility.Collapsed;
        ResultText.Text = vm.CurrentConnectionSummary + "\n" + vm.RemoteRoot + "\n"
            + (vm.IsProject ? vm.AutomaticSummary + "\n" + vm.CopySummary + "\n" + vm.ManualJobStatus + "\n" : "") + vm.Message;
    }
    private void OnProject(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var id = _session.ViewModel.ProjectId;
        if (id.Length == 0) NavigationService.NavigateTo("CloudConnections");
        else NavigationService.NavigateTo("CloudProject", new CloudSetupNavigationParameter(id, Intent: CloudSetupIntent.ProjectStatus));
    }
    private void OnFinish(object sender, RoutedEventArgs e) { var session = _session; OnProject(sender, e); if (session is not null) CloudWizardSessions.End(session); }
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var session = _session; _session = null; CloudWizardSessions.End(session);
        if (Frame.CanGoBack) Frame.GoBack(); else NavigationService.NavigateTo("Home");
    }
}
