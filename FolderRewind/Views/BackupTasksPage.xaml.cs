using FolderRewind.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FolderRewind.Views
{
    public sealed partial class BackupTasksPage : Page
    {
        public BackupTasksPageViewModel ViewModel { get; } = new();

        public BackupTasksPage()
        {
            this.InitializeComponent();
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
            ViewModel.Activate();
        }

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
