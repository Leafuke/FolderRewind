using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;

namespace FolderRewind.Views;

public sealed partial class MinecraftIntegrationPage : Page
{
    private BackupConfig? _config;
    private ManagedFolder? _source;
    private CancellationTokenSource _lifetime = new();
    private bool _checking;
    public ObservableCollection<OnboardingDiagnosticItem> HostChecks { get; } = [];
    public ObservableCollection<OnboardingDiagnosticItem> GameChecks { get; } = [];
    public ObservableCollection<OnboardingDiagnosticItem> Diagnostics { get; } = [];
    public MinecraftIntegrationPage() { InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        var request = e.Parameter as ManagerNavigationParameter;
        _config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == request?.ConfigId);
        _source = _config?.SourceFolders.FirstOrDefault(f => f.Path == request?.FolderPath);
        WorldPicker.ItemsSource = _config?.SourceFolders.Select(f => f.DisplayName).ToArray();
        WorldPicker.SelectedIndex = _source is null ? -1 : _config!.SourceFolders.IndexOf(_source);
        SourceContext.Text = _config?.Name ?? I18n.GetString("MinecraftCheck_SelectSource");
        WorldCard.Description = _source?.Path ?? string.Empty;
        CheckButton.IsEnabled = RecheckButton.IsEnabled = _source is not null;
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { _lifetime.Cancel(); base.OnNavigatedFrom(e); }
    private void OnWorldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_checking || _config is null || WorldPicker.SelectedIndex < 0 || WorldPicker.SelectedIndex >= _config.SourceFolders.Count) return;
        _source = _config.SourceFolders[WorldPicker.SelectedIndex];
        WorldCard.Description = _source.Path;
        CheckButton.IsEnabled = RecheckButton.IsEnabled = true;
        CheckResults.Visibility = Visibility.Visible;
        CheckResults.Text = I18n.GetString("MinecraftCheck_SelectionChanged");
        Diagnostics.Clear(); HostChecks.Clear(); GameChecks.Clear();
    }
    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        if (_checking || _config is null || _source is null) return;
        _checking = true;
        CheckButton.IsEnabled = RecheckButton.IsEnabled = false;
        CheckResults.Visibility = Visibility.Visible;
        WorldPicker.IsEnabled = false;
        try
        {
            var context = new OnboardingRepairContext(_config.Id, _source.Id, NativeHistoryConfigLease.Signature(_config), DateTimeOffset.UtcNow);
            var results = await MinecraftIntegrationCheckService.CheckAsync(_config, _source, _lifetime.Token);
            if (!_lifetime.IsCancellationRequested)
            {
                Diagnostics.Clear(); HostChecks.Clear(); GameChecks.Clear();
                foreach (var result in results)
                {
                    var item = new OnboardingDiagnosticItem(result, context);
                    Diagnostics.Add(item);
                    if (result.Code is "minecraft.game-response" or "minecraft.hot-backup" or "minecraft.hot-restore" or "minecraft.game-schedule") GameChecks.Add(item);
                    else HostChecks.Add(item);
                }
                CheckResults.Text = I18n.GetString("MinecraftCheck_ResultsReady");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) CheckResults.Text = ex.Message; }
        finally { _checking = false; WorldPicker.IsEnabled = true; CheckButton.IsEnabled = RecheckButton.IsEnabled = _source is not null; }
    }
    public Visibility HasItems(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    private void OnMinecraftPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MinecraftLayout is not null) MinecraftLayout.Width = Math.Min(960, Math.Max(0, e.NewSize.Width - 48));
    }
    private void OnComponentCardSizeChanged(object sender, SizeChangedEventArgs e) => SettingsCardLayout.Apply(sender, e);
    private void OnSettings(object sender, RoutedEventArgs e) => NavigationService.NavigateTo("Settings", NavigationService.SettingsMinecraftPresetTarget);
    private void OnRepair(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OnboardingDiagnosticItem item } || _config is null || _source is null) return;
        var current = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == item.Context.ConfigId);
        if (current is null || !current.SourceFolders.Any(s => s.Id == item.Context.SourceId)
            || !OnboardingRepairPolicy.IsCurrent(item.Context, current.Id, _source.Id, NativeHistoryConfigLease.Signature(current)))
        { CheckResults.Text = I18n.GetString("SettingsProject_Stale"); Diagnostics.Clear(); HostChecks.Clear(); GameChecks.Clear(); return; }
        switch (item.Target)
        {
            case OnboardingRepairTarget.Plugins: NavigationService.NavigateTo("Settings", NavigationService.SettingsPluginsTarget); break;
            case OnboardingRepairTarget.KnotLink: NavigationService.NavigateTo("Settings", NavigationService.SettingsKnotLinkTarget); break;
            case OnboardingRepairTarget.ProjectContent: NavigationService.NavigateTo("Manager", ManagerNavigationParameter.ForFolder(current.Id, _source.Path)); break;
            case OnboardingRepairTarget.MinecraftManualCheck: CheckResults.Text = I18n.GetString("MinecraftCheck_ManualInstructions"); break;
        }
    }
    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        if (Diagnostics.Count == 0) return;
        var summary = OnboardingRepairPolicy.ExportSummary(Diagnostics.Select(i => (i.Check.Code, i.Check.State.ToString(), i.Check.CheckedAtUtc)));
        if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Diagnostics_ExportTitle"), I18n.GetString("Diagnostics_ExportFields") + "\n\n" + summary,
            I18n.GetString("Common_Confirm"), XamlRoot)) return;
        var path = await MainWindowService.PickSaveFilePathAsync("", "FolderRewind.Onboarding.Diagnostics",
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>> { ["Text"] = new[] { ".txt" } }, "FolderRewind-checks");
        if (_lifetime.IsCancellationRequested || string.IsNullOrWhiteSpace(path)) return;
        try { await File.WriteAllTextAsync(path, summary, _lifetime.Token); CheckResults.Text = I18n.GetString("Diagnostics_Exported"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CheckResults.Text = CloudCommandSecurity.Redact(ex.Message); }
    }
    private async void OnModsDirectory(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFolderPathAsync(I18n.GetString("MinecraftCheck_ModsPicker"), "FolderRewind.Minecraft.Mods", MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (!_lifetime.IsCancellationRequested && path is not null) ShellPathService.TryOpenPath(path, out _);
    }
}
