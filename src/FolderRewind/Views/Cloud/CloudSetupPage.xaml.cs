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
    public CloudSetupPage() => InitializeComponent();
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e); _active = true;
        var options = e.Parameter as CloudSetupNavigationParameter ?? new((e.Parameter as ConfigSettingsNavigationParameter)?.ConfigId ?? "");
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
            await vm.InitializeAsync(options.ConfigId, manageConnection: options.ManageConnection, profileId: options.ProfileId);
            if (options.ProfileId is not null)
            {
                if (options.Reauthenticate || vm.NeedsReauthentication) vm.BeginReauthentication();
                else { vm.ConnectionMethod = 2; vm.WizardStep = 4; }
            }
            else vm.ApplyPreset(options.PresetProviderId);
            _session.Initialized = true;
        }
        if (_active) { vm.ActivateEnvironment(); Editor.Attach(vm); Editor.Visibility = Visibility.Visible; RefreshResult(); }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { _active = false; if (_session is not null) { _session.ViewModel.PropertyChanged -= OnChanged; _session.ViewModel.SuspendChecks(); } base.OnNavigatedFrom(e); }
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
