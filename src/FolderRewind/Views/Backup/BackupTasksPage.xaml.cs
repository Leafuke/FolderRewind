using FolderRewind.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using FolderRewind.Models;
using FolderRewind.Services;
using Windows.ApplicationModel.DataTransfer;

namespace FolderRewind.Views
{
    public sealed partial class BackupTasksPage : Page
    {
        public BackupTasksPageViewModel ViewModel { get; } = new();

        public BackupTasksPage()
        {
            this.InitializeComponent();
        }

        private async void OnViewDetails(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: BackupTask task }) return;
            var text = OperationDiagnosticFormatter.TaskDetails(task);
            var details = new TextBox { IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MinWidth = 240, MaxHeight = 420, Text = text };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(details, I18n.GetString("BackupTasks_DetailsLabel"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(details, "BackupTaskDetailsText");
            var dialog = new ContentDialog { Title = I18n.Format("BackupTasks_DetailsTitle", task.FolderName), Content = details,
                PrimaryButtonText = I18n.GetString("BackupTasks_CopyDetails"), CloseButtonText = I18n.GetString("Common_Close") };
            dialog.PrimaryButtonClick += (_, args) => { args.Cancel = true; var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); };
            await AppDialogService.Default.ShowCustomAsync(dialog, XamlRoot);
        }

        /// <summary>
        /// 每次导航到此页面时重新订阅集合变更并刷新视图。
        /// 修复 NavigationCacheMode="Required" 导致的"数字更新但卡片不刷新"问题：
        /// 旧实现在 Unloaded 中取消订阅，但缓存页面再次导入时构造函数不会重新执行，
        /// 导致 CollectionChanged 订阅丢失。改用 OnNavigatedTo/OnNavigatedFrom 生命周期。
        /// </summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var target = (e.Parameter as NotificationNavigationParameter)?.Target;
            ViewModel.SetNotificationFilter(target?.OperationId, target?.TaskId);
            ViewModel.Activate();
            if (target is not null)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModel.TasksView.Count == 0) { NotificationNavigationService.Unavailable(); return; }
                    TaskList.ScrollIntoView(ViewModel.TasksView[0]);
                    TaskList.Focus(FocusState.Programmatic);
                });
        }

        private void OnShowAllTasks(object sender, RoutedEventArgs e) => ViewModel.SetNotificationFilter(null, null);

        /// <summary>
        /// 离开页面时取消订阅，防止内存泄漏。
        /// </summary>
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Deactivate();
        }
    }
}
