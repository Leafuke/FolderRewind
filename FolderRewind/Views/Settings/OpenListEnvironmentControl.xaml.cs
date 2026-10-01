using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Collections.ObjectModel;

namespace FolderRewind.Views.Settings;

public sealed partial class OpenListEnvironmentControl : UserControl
{
    private CancellationTokenSource _lifetime = new();
    private bool _busy;
    public ObservableCollection<OnboardingDiagnosticItem> Diagnostics { get; } = [];
    private static string Signature(OpenListRuntimeSettings settings) => string.Join("\0", settings.ExecutablePath,
        settings.WorkingDirectory, settings.DataDirectory, settings.ConfigFilePath, settings.ServiceBaseUri);
    private void OnDraftChanged(object sender, TextChangedEventArgs e) => Diagnostics.Clear();
    public void ShowRepairTarget() { EnvironmentExpander.IsExpanded = true; EnvironmentExpander.StartBringIntoView(); }
    public OpenListEnvironmentControl()
    {
        InitializeComponent(); Loaded += OnLoaded; Unloaded += (_, _) => _lifetime.Cancel();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        var settings = ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime ?? new();
        Executable.Text = settings.ExecutablePath; Working.Text = settings.WorkingDirectory; Data.Text = settings.DataDirectory;
        Config.Text = settings.ConfigFilePath; ServiceUri.Text = settings.ServiceBaseUri;
    }
    private OpenListRuntimeSettings Draft() => new() { ExecutablePath = Executable.Text.Trim(), WorkingDirectory = Working.Text.Trim(), DataDirectory = Data.Text.Trim(), ConfigFilePath = Config.Text.Trim(), ServiceBaseUri = ServiceUri.Text.Trim() };
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy) return; _busy = true; IsEnabled = false;
        try { await OpenListRuntimeService.SaveAsync(Draft()); if (!_lifetime.IsCancellationRequested) Result.Text = I18n.GetString("OpenList_SavedManualOnly"); }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) Result.Text = ex.Message; }
        finally { _busy = false; IsEnabled = true; }
    }
    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        if (_busy) return; _busy = true; IsEnabled = false;
        try
        {
            var draft = Draft();
            var result = await OpenListRuntimeService.CheckAsync(draft, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested && Signature(draft) == Signature(Draft()))
            {
                Result.Text = result.Message;
                Diagnostics.Clear(); Diagnostics.Add(new(result, new("", "", Signature(draft), result.CheckedAtUtc)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) Result.Text = ex.Message; }
        finally { _busy = false; IsEnabled = true; }
    }
    private async void OnPickExecutable(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.OpenList.Executable", [".exe"], MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (!_lifetime.IsCancellationRequested && path is not null) Executable.Text = path;
    }
    private void OnOpenManagement(object sender, RoutedEventArgs e)
    {
        try { var uri = OpenListRuntimeService.Validate(Draft()); ShellPathService.TryOpenPath(uri.AbsoluteUri, out _); }
        catch (Exception ex) { Result.Text = ex.Message; }
    }
    private void OnRepair(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: OnboardingDiagnosticItem item }) return;
        if (!Diagnostics.Contains(item) || !OnboardingRepairPolicy.IsCurrent(item.Context, "", "", Signature(Draft())))
        { Result.Text = I18n.GetString("SettingsProject_Stale"); Diagnostics.Clear(); return; }
        switch (item.Target)
        {
            case OnboardingRepairTarget.OpenListEnvironment: Executable.Focus(FocusState.Programmatic); break;
            case OnboardingRepairTarget.CloudConnection: NavigationService.NavigateTo("CloudSetup"); break;
        }
    }
    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try { await OnboardingDiagnosticsInteraction.ExportAsync(Diagnostics, XamlRoot, _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) Result.Text = CloudCommandSecurity.Redact(ex.Message); }
    }
}
