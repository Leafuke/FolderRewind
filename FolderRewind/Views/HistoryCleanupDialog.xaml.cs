using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views;

public sealed partial class HistoryCleanupDialog : ContentDialog
{
    public HistoryCleanupDialogViewModel ViewModel { get; }
    private bool _closeRequested;
    public HistoryCleanupDialog(ConfigSettingsDialogViewModel settings, bool reportOnly)
    {
        ViewModel = new(settings, reportOnly);
        InitializeComponent();
        if (reportOnly) { PrimaryButtonText = ""; PolicyChoice.Visibility = Visibility.Collapsed; }
    }
    private async void OnStart(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await ViewModel.RunAsync();
        if (_closeRequested) Hide();
    }
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!ViewModel.IsRunning) return;
        args.Cancel = true;
        _closeRequested = true;
        ViewModel.Cancel();
    }
}
