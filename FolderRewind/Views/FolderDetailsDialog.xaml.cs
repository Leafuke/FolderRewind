using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml.Controls;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class FolderDetailsDialog : ContentDialog
{
    private readonly CancellationTokenSource _loadCts = new();

    public FolderDetailsDialogViewModel ViewModel { get; } = new();

    public FolderDetailsDialog()
    {
        InitializeComponent();
        DataContext = ViewModel;
        CloseButtonText = I18n.GetString("Common_Close");
        DefaultButton = ContentDialogButton.Close;
        ThemeService.ApplyThemeToDialog(this);
        Closing += (_, _) => _loadCts.Cancel();
        Closed += (_, _) => _loadCts.Dispose();
    }

    public Task InitializeAsync(BackupConfig config, ManagedFolder folder)
    {
        Title = string.IsNullOrWhiteSpace(folder.DisplayName)
            ? I18n.GetString("FolderManager_Details.Text")
            : folder.DisplayName;

        return ViewModel.LoadAsync(config, folder, _loadCts.Token);
    }
}
