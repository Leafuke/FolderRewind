using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace FolderRewind.Views
{
    public sealed partial class BackupBranchPage : Page
    {
        public BackupBranchPageViewModel ViewModel { get; } = new();

        // 构造期赋初值会触发 SelectionChanged，而那时页面还没导航完成，
        // 刷新要等到 OnNavigatedTo 才做，避免首次进入读两遍。
        private bool _isInitializing = true;

        public BackupBranchPage()
        {
            this.InitializeComponent();

            ViewModel.Initialize();

            // 与历史页同样的原因：首次导航时 x:Bind 可能晚于控件创建，这里显式赋一次初值。
            ConfigFilter.ItemsSource = ViewModel.Configs;
            ConfigFilter.SelectedItem = ViewModel.SelectedConfig;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isInitializing = false;
            await ViewModel.RefreshAsync();
        }

        private async void ConfigFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedConfig = ConfigFilter.SelectedItem as BackupConfig;
            if (_isInitializing)
            {
                return;
            }

            await ViewModel.RefreshAsync();
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshAsync();
        }

        private async void OnCreateFromRunClick(object sender, RoutedEventArgs e)
        {
            var dialog = new CreateBranchDialog(ViewModel.BranchableRuns) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (dialog.SelectedRun is not { } run)
            {
                return;
            }

            await ViewModel.CreateBranchAsync(run.CheckpointId, dialog.BranchName);
        }

        private async void OnCheckoutClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBranch is not { } branch)
            {
                return;
            }

            // 先要一份计划：切换会覆盖源目录，不能点一下就动手。
            var plan = await ViewModel.PlanCheckoutAsync(branch);
            if (plan is null)
            {
                return;
            }

            if (!plan.CanExecute)
            {
                var blocked = new ContentDialog
                {
                    Title = I18n.GetString("BackupBranchPage_CheckoutBlockedTitle"),
                    Content = plan.Diagnostic,
                    CloseButtonText = I18n.GetString("Common_Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                ThemeService.ApplyThemeToDialog(blocked);
                await blocked.ShowAsync();
                return;
            }

            var content = I18n.Format("BackupBranchPage_CheckoutConfirmContent", branch.Name);
            if (plan.RequiresProtection)
            {
                // 有未收进历史的改动时必须明说：切换会覆盖它们，只是先留了退路。
                content = content + Environment.NewLine + Environment.NewLine
                    + I18n.GetString("BackupBranchPage_CheckoutProtectionNotice");
            }

            var confirm = new ContentDialog
            {
                Title = I18n.GetString("BackupBranchPage_CheckoutConfirmTitle"),
                Content = content,
                PrimaryButtonText = I18n.GetString("Common_Ok"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.CheckoutAsync(branch);
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBranch is not { } branch)
            {
                return;
            }

            var dialog = new RenameBranchDialog(branch.Name) { XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.RenameBranchAsync(branch, dialog.BranchName);
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedBranch is not { } branch)
            {
                return;
            }

            // 删除只改分支事实、不动备份数据，因此不额外说明数据后果，只讲清楚这个分支会消失。
            var confirm = new ContentDialog
            {
                Title = I18n.GetString("BackupBranchPage_DeleteConfirmTitle"),
                Content = I18n.Format("BackupBranchPage_DeleteConfirmContent", branch.Name),
                PrimaryButtonText = I18n.GetString("Common_Ok"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            ThemeService.ApplyThemeToDialog(confirm);

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.DeleteBranchAsync(branch);
        }
    }
}
