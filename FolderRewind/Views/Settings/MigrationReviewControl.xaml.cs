using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Views.Settings;

public sealed partial class MigrationReviewControl : UserControl
{
    public MigrationReviewViewModel ViewModel { get; }
    public MigrationReviewControl(MigrationReviewViewModel viewModel)
    { ViewModel = viewModel; InitializeComponent(); }
    public static Visibility ToVisibility(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    private void OnSelectAll(object sender, RoutedEventArgs e) => ViewModel.SelectAll(true);
    private void OnClearSelection(object sender, RoutedEventArgs e) => ViewModel.SelectAll(false);
}
