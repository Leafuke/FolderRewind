using FolderRewind.History.Merge;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace FolderRewind.Views;

public sealed partial class MergePage : Page
{
    public MergePageViewModel ViewModel { get; } = new();
    public MergePage() { InitializeComponent(); DataContext = ViewModel; }
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MergeNavigationParameter parameter)
            await ViewModel.ExecuteAsync(() => ViewModel.InitializeAsync(parameter));
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Detach(); base.OnNavigatedFrom(e); }
    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else if (ViewModel.Navigation is { } n) NavigationService.NavigateTo("History", n.ReturnContext);
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FilePanel is null) return;
        ListColumn.Width = new GridLength(e.NewSize.Width < 720 ? 180 : ViewModel.ViewState.ListWidth);
    }
    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => ViewModel.Notify();
    private async void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    { if (SessionPicker.SelectedItem is MergeSessionChoice s) await ViewModel.SelectSessionAsync(s.Id); }
    private async void OnAnalyze(object sender, RoutedEventArgs e) => await ViewModel.AnalyzeAsync();
    private async void OnOurs(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Ours);
    private async void OnTheirs(object sender, RoutedEventArgs e) => await ViewModel.AdoptAsync(MergeResolutionChoice.Theirs);
    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Operations?.Tracker.Stop();
    private async void OnDownload(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.PrepareReplicasAsync); }
    private async void OnRecompute(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.RecomputeAsync); }
    private async void OnRecover(object sender, RoutedEventArgs e) { if (ViewModel.Operations is { } o) await ViewModel.ExecuteAsync(o.ResumeAsync); }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanImport || ViewModel.SelectedChange is not { } row || ViewModel.Operations is not { } o) return;
        await ViewModel.ExecuteAsync(async () =>
        {
            var path = await MainWindowService.PickFilePathAsync(I18n.GetString("Merge_Manual"), "merge-manual", ["*"]);
            if (path is not null) await o.ImportAsync(row.Conflict, path);
        });
    }
    private async void OnAbandon(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Operations is not { } o || !ViewModel.CanAbandon) return;
        var dialog = new ContentDialog { Title = I18n.GetString("Merge_Abandon"), Content = I18n.GetString("MergeWorkspace_AbandonConfirm"),
            PrimaryButtonText = I18n.GetString("Merge_Abandon"), CloseButtonText = I18n.GetString("Merge_Close"), DefaultButton = ContentDialogButton.Close };
        await ViewModel.ExecuteAsync(async () =>
        {
            if (await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot) == ContentDialogResult.Primary) await o.AbandonAsync();
        });
    }
}
