using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using System;
using System.Linq;
using System.Globalization;

namespace FolderRewind.Views;

public sealed partial class FolderManagerPage
{
    private async void OnViewTimings(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentConfig is not { } config) return;
        var summary = BackupTimingService.GetRecent(config.Id).LastOrDefault();
        var text = summary is null ? I18n.GetString("Timing_NoSummary") : I18n.Format("Timing_Summary", summary.Total.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture))
            + "\n" + string.Join("\n", summary.Phases.Select(p => I18n.GetString("Timing_" + p.Code) + ": " + p.Elapsed.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture)));
        await AppDialogService.Default.ShowMessageAsync(I18n.GetString("Timing_Title"), text, XamlRoot);
    }
    private void OnCloudSetup(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentConfig is { } config) NavigationService.NavigateTo("CloudSetup", new ConfigSettingsNavigationParameter(config.Id));
    }
    private void OnMinecraftIntegration(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentConfig is { } config) NavigationService.NavigateTo("MinecraftIntegration", new ManagerNavigationParameter { ConfigId = config.Id, FolderPath = ViewModel.SelectedFolder?.Path });
    }
}
