using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Controls;

public sealed partial class CloudConnectionEditor : UserControl
{
    public CloudSetupViewModel ViewModel { get; private set; } = null!;
    public string UrlLabel => I18n.GetString("CloudSetup_Url.Header");
    public string UserLabel => I18n.GetString("CloudSetup_User.Header");
    public string PasswordLabel => I18n.GetString("CloudSetup_Password.Header");
    public string ConfigLabel => I18n.GetString("CloudSetup_ConfigPath.Header");
    public string RemoteLabel => I18n.GetString("CloudSetup_Remote.Header");
    public string LocationLabel => I18n.GetString("CloudSetup_RemoteRoot.Header");
    public event EventHandler? Confirmed;
    private string _shownFailure = "";
    public CloudConnectionEditor()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (ViewModel is not null) { ViewModel.PropertyChanged -= OnChanged; ViewModel.PropertyChanged += OnChanged; Refresh(); } };
        Unloaded += (_, _) => { Password.Password = ""; InitialPassword.Password = ""; if (ViewModel is not null) ViewModel.PropertyChanged -= OnChanged; };
    }
    public void Attach(CloudSetupViewModel viewModel)
    { if (ViewModel is not null) ViewModel.PropertyChanged -= OnChanged; ViewModel = viewModel; ViewModel.PropertyChanged += OnChanged; Refresh(); }
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public void Refresh()
    {
        if (ViewModel is null) return;
        var step = ViewModel.WizardStep; var method = ViewModel.ConnectionMethod;
        StepTitle.Text = I18n.GetString("CloudWizard_Step" + step);
        ChoicePanel.Visibility = Show(step == 0); SavedChoice.Visibility = Show(ViewModel.HasProfiles);
        EnvironmentPanel.Visibility = Show(step == 1 && (method != 3 || !File.Exists(ViewModel.Executable))); LocalOpenListPanel.Visibility = Show(method == 0);
        SavedPanel.Visibility = Show(step == 1 && method == 3);
        AccessPanel.Visibility = Show(step == 2); WebDavPanel.Visibility = Show(method < 2); RclonePanel.Visibility = Show(method == 2);
        OpenListAccountHelp.Visibility = ManagementButton.Visibility = Show(method == 0);
        LocationPanel.Visibility = Show(step == 3); SuggestFolder.Visibility = Show(!ViewModel.RecoveryMode && method == 0);
        ConfirmPanel.Visibility = Show(step == 4); ProfileFields.Visibility = Show(!ViewModel.RecoveryMode && ViewModel.CanEditProfile);
        UploadChoices.Visibility = Show(!ViewModel.RecoveryMode && ViewModel.IsProject);
        NoVersions.Visibility = Show(ViewModel.Versions.Count == 0);
        Advanced.Visibility = Show(step is 2 or 3 && method == 2);
        BackButton.Visibility = Show(step > 0 && step < 5); BackButton.IsEnabled = ViewModel.CanEditConnection;
        NextButton.Visibility = Show(step > 0 && step < 5); NextButton.IsEnabled = ViewModel.CanEdit;
        NextButton.Content = I18n.GetString(step == 3 ? "CloudWizard_VerifyNext" : step == 4 ? "CloudWizard_Confirm" : "CloudWizard_Next");
        VerificationHelp.Text = I18n.GetString(ViewModel.RecoveryMode ? "CloudWizard_ReadOnlyCheck" : "CloudWizard_WriteCheck");
        Status.Message = ViewModel.Message; Status.IsOpen = ViewModel.HasMessage;
        Status.Severity = (InfoBarSeverity)ViewModel.MessageSeverity;
        RepairButton.Content = I18n.GetString(ViewModel.FailureKind == "Tool" ? "CloudSetup_PickExecutable.Content" : "CloudRepair_Action.Content");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RepairButton, (string)RepairButton.Content);
        Bindings.Update();
        if (ViewModel.IsBusy) _shownFailure = "";
        else if (ViewModel.HasRepair && Status.IsOpen && _shownFailure != ViewModel.Message)
        {
            _shownFailure = ViewModel.Message;
            DispatcherQueue.TryEnqueue(() => Status.StartBringIntoView());
        }
    }
    private async Task Run(Func<CancellationToken, Task> action) { await ViewModel.RunCheckAsync(action); Refresh(); }
    private void OnChoose(object sender, RoutedEventArgs e) { ViewModel.StartMethod(int.Parse((string)((Button)sender).Tag)); Refresh(); }
    private void OnBack(object sender, RoutedEventArgs e) { ViewModel.GoBackWizard(); Refresh(); }
    private async void OnNext(object sender, RoutedEventArgs e)
    {
        if (ViewModel.WizardStep == 4) { Confirmed?.Invoke(this, EventArgs.Empty); return; }
        await Run(async token =>
        {
            switch (ViewModel.WizardStep)
            {
                case 1:
                    ViewModel.RequireEnvironmentReady();
                    if (ViewModel.ConnectionMethod == 3)
                    {
                        if (ViewModel.SelectedProfileIndex < 0) throw new InvalidOperationException(I18n.GetString("CloudConnections_SelectFirst"));
                        ViewModel.RequireAccessConfiguration(); ViewModel.WizardStep = 3;
                    }
                    else ViewModel.WizardStep = 2;
                    break;
                case 2:
                    if (ViewModel.ConnectionMethod < 2)
                    {
                        await ViewModel.CreateWebDavAsync(ViewModel.WebDavUrl, ViewModel.WebDavUser, Password.Password, token, AllowInsecure.IsChecked == true);
                        Password.Password = "";
                    }
                    else ViewModel.RequireAccessConfiguration();
                    ViewModel.WizardStep = 3;
                    break;
                case 3:
                    if (ViewModel.RecoveryMode) await ViewModel.BrowseAsync(token);
                    else { await ViewModel.VerifyWriteAsync(token); if (!ViewModel.HasCurrentWriteEvidence) return; }
                    if (ViewModel.IsProject && !ViewModel.RecoveryMode) await ViewModel.LoadVersionsAsync(token);
                    ViewModel.AcceptLocation();
                    break;
            }
        });
    }
    private async void OnPrepareRclone(object s, RoutedEventArgs e) => await Run(t => ViewModel.PrepareToolsAsync(false, t));
    private async void OnPrepareOpenList(object s, RoutedEventArgs e) => await Run(t => ViewModel.PrepareToolsAsync(true, t));
    private async void OnUseLocal(object s, RoutedEventArgs e) => await Run(t => { ViewModel.UseLocalOpenList(); return Task.CompletedTask; });
    private void OnUseExternal(object s, RoutedEventArgs e) { ViewModel.UseExternalOpenList(); Refresh(); }
    private async void OnInitialize(object s, RoutedEventArgs e)
    {
        var password = InitialPassword.Password;
        try { await Run(t => ViewModel.InitializeOpenListAsync(password, double.IsNaN(Port.Value) ? 0 : (int)Port.Value, t)); }
        finally { InitialPassword.Password = ""; }
    }
    private async void OnAuthorizeStart(object s, RoutedEventArgs e)
    {
        var draft = ViewModel.LocalOpenListDraft;
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("OpenList_AuthorizationTitle"),
            I18n.Format("OpenList_AuthorizationDescription", draft.ExecutablePath, draft.WorkingDirectory, draft.DataDirectory, draft.ConfigFilePath, draft.ServiceBaseUri),
            I18n.GetString("Common_Confirm"), XamlRoot)) await Run(ViewModel.AuthorizeAndStartOpenListAsync);
    }
    private void OnEnvironmentSettings(object s, RoutedEventArgs e) => NavigationService.NavigateTo("Settings", NavigationService.SettingsOpenListTarget);
    private async void OnPickExecutable(object s, RoutedEventArgs e) => await Run(async t =>
    { var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.CloudSetup.Executable", [".exe"], MainWindowService.SuggestedPickerLocation.ComputerFolder); if (path is not null) ViewModel.Executable = path; });
    private async void OnPickConfig(object s, RoutedEventArgs e) => await Run(async t =>
    { var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.CloudSetup.Config", [".conf", "*"], MainWindowService.SuggestedPickerLocation.ComputerFolder); if (path is not null) { ViewModel.ConfigPath = path; ViewModel.LoadRemotes(); } });
    private async void OnDetectConfig(object s, RoutedEventArgs e) => await Run(ViewModel.DetectConfigurationAsync);
    private async void OnListRemotes(object s, RoutedEventArgs e) => await Run(t => { ViewModel.LoadRemotes(); return Task.CompletedTask; });
    private async void OnReuseCredentials(object s, RoutedEventArgs e) => await Run(t => { ViewModel.ReuseManagedCredentials(); return Task.CompletedTask; });
    private async void OnImportNew(object s, RoutedEventArgs e)
    {
        if (await AppDialogService.Default.ConfirmAsync(I18n.GetString("CloudSetup_DialogTitle"), I18n.GetString("CloudWizard_ImportHelp.Text"),
            I18n.GetString("Common_Confirm"), XamlRoot)) await Run(ViewModel.ImportManagedAsync);
    }
    private async void OnBrowse(object s, RoutedEventArgs e) => await Run(ViewModel.BrowseAsync);
    private async void OnBrowseMore(object s, RoutedEventArgs e) => await Run(ViewModel.BrowseMoreAsync);
    private async void OnParent(object s, RoutedEventArgs e) { if (!ViewModel.CanEdit) return; ViewModel.RemoteRoot = CloudRemotePath.Parent(ViewModel.RemoteRoot); await Run(ViewModel.BrowseAsync); }
    private async void OnDirectory(object s, ItemClickEventArgs e) { if (!ViewModel.CanEdit) return; ViewModel.EnterDirectory((string)e.ClickedItem); await Run(ViewModel.BrowseAsync); }
    private void OnSuggestFolder(object s, RoutedEventArgs e) { ViewModel.RemoteRoot = CloudRemotePath.Append(ViewModel.RemoteRoot, "FolderRewind"); Refresh(); }
    private void OnManagement(object s, RoutedEventArgs e)
    {
        try
        {
            var uri = RcloneConnectionService.ValidateWebDavUrl(ViewModel.WebDavUrl, AllowInsecure.IsChecked == true);
            var builder = new UriBuilder(uri); if (builder.Path.TrimEnd('/').EndsWith("/dav", StringComparison.OrdinalIgnoreCase)) builder.Path = builder.Path.TrimEnd('/')[..^4] + "/";
            ShellPathService.TryOpenPath(builder.Uri.AbsoluteUri, out _);
        }
        catch (Exception ex) { ViewModel.Message = CloudCommandSecurity.Redact(ex.Message); Refresh(); }
    }
    private void OnRepair(object s, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit) return;
        if (ViewModel.FailureKind == "Tool") { ViewModel.WizardStep = 1; Refresh(); OnPickExecutable(s, e); }
        else if (ViewModel.FailureKind == "Offline" && ViewModel.DependsOnOpenList) OnEnvironmentSettings(s, e);
        else { ViewModel.WizardStep = ViewModel.ConnectionMethod == 3 ? 1 : 2; Refresh(); }
    }
    private void OnCancelCheck(object s, RoutedEventArgs e) => ViewModel.SuspendChecks();
}
