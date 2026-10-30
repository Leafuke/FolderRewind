using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views;

public sealed partial class FolderRenameDialog : ContentDialog
{
    public FolderRenameDialogViewModel ViewModel { get; } = new();

    public FolderRenameDialog()
    {
        InitializeComponent();
        Title = I18n.GetString("FolderManager_RenameFolder_Title");
        PrimaryButtonText = I18n.GetString("Common_Ok");
        CloseButtonText = I18n.GetString("Common_Cancel");
        DefaultButton = ContentDialogButton.Primary;
        ThemeService.ApplyThemeToDialog(this);
    }

    public void Initialize(ManagedFolder folder, FolderRenamePreview preview)
    {
        ViewModel.Load(folder, preview);
        DataContext = ViewModel;
        Bindings.Update();
    }
}
