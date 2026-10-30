using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views.Settings;

public sealed partial class OpenListEnvironmentControl : UserControl
{
    public OpenListEnvironmentViewModel ViewModel { get; } = new();
    private CancellationTokenSource _lifetime = new();
    private bool _bringIntoViewPending;
    public OpenListEnvironmentControl()
    {
        InitializeComponent();
        Loaded += (_, _) => ActivatePresentation();
        Unloaded += (_, _) => DeactivatePresentation();
    }
    internal void ActivatePresentation()
    {
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        ViewModel.Activate();
        Bindings.Update();
        if (_bringIntoViewPending)
        { _bringIntoViewPending = false; DispatcherQueue.TryEnqueue(() => { if (IsLoaded) EnvironmentExpander.StartBringIntoView(); }); }
    }
    internal void DeactivatePresentation()
    {
        _lifetime.Cancel();
        ViewModel.Deactivate();
        Bindings.StopTracking();
    }
    public void ShowRepairTarget()
    {
        EnvironmentExpander.IsExpanded = true;
        if (IsLoaded) EnvironmentExpander.StartBringIntoView(); else _bringIntoViewPending = true;
    }
    private void OnEnvironmentCardSizeChanged(object sender, SizeChangedEventArgs e) => SettingsCardLayout.Apply(sender, e);
    private void OnActionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ManualGuide is null || sender is not Grid grid) return;
        var narrow = e.NewSize.Width < 520;
        Grid.SetColumn(ManualGuide, narrow ? 0 : 1); Grid.SetRow(ManualGuide, narrow ? 1 : 0);
        Grid.SetColumnSpan(ManualGuide, narrow ? 3 : 1); grid.RowSpacing = narrow ? 8 : 0;
    }
    private Task<bool> ConfirmAuthorizationAsync(OpenListRuntimeSettings draft)
        => AppDialogService.Default.ConfirmAsync(I18n.GetString("OpenList_AuthorizationTitle"),
            I18n.Format("OpenList_AuthorizationDescription", draft.ExecutablePath, draft.WorkingDirectory, draft.DataDirectory, draft.ConfigFilePath, draft.ServiceBaseUri),
            I18n.GetString("Common_Confirm"), XamlRoot);
    private async void OnSave(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync(ConfirmAuthorizationAsync, _lifetime.Token);
    private async void OnStart(object sender, RoutedEventArgs e) => await ViewModel.StartAsync(ConfirmAuthorizationAsync, _lifetime.Token);
    private async void OnCheck(object sender, RoutedEventArgs e) => await ViewModel.CheckAsync(_lifetime.Token);
    private async void OnReload(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasEdits || await AppDialogService.Default.ConfirmAsync(I18n.GetString("OpenList_Reload.Content"),
            I18n.GetString("OpenList_ReloadConfirm"), I18n.GetString("Common_Confirm"), XamlRoot)) ViewModel.Reload();
    }
    private async void OnPickExecutable(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.OpenList.Executable", [".exe"], MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (!_lifetime.IsCancellationRequested && path is not null) ViewModel.ExecutablePath = path;
    }
    private async void OnPickConfiguration(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.OpenList.Configuration", [".json"], MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (!_lifetime.IsCancellationRequested && path is not null) await ViewModel.InspectConfigurationAsync(path, _lifetime.Token);
    }
    private void OnOpenManagement(object sender, RoutedEventArgs e)
    {
        try { ShellPathService.TryOpenPath(RcloneConnectionService.ValidateWebDavUrl(ViewModel.ServiceBaseUri.Trim()).AbsoluteUri, out _); }
        catch (System.ArgumentException ex) { NotificationService.ShowWarning(CloudCommandSecurity.Redact(ex.Message)); }
    }
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanStop && await AppDialogService.Default.ConfirmAsync(I18n.GetString("OpenList_StopTitle"),
            I18n.GetString("OpenList_StopDescription"), I18n.GetString("Common_Confirm"), XamlRoot)) await ViewModel.StopAsync(_lifetime.Token);
    }
}
