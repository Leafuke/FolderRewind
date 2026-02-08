using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FolderRewind.Views
{
    public sealed partial class BackupTasksPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<BackupTask> ViewModel => Services.BackupService.ActiveTasks;

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> TasksView { get; } = new();

        private bool _isEmpty = true;
        public bool IsEmpty
        {
            get => _isEmpty;
            private set
            {
                if (_isEmpty == value) return;
                _isEmpty = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
            }
        }

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

            if (ViewModel != null)
            {
                // 先移除再添加，确保不会重复订阅
                ViewModel.CollectionChanged -= OnTasksChanged;
                ViewModel.CollectionChanged += OnTasksChanged;
            }

            // 每次进入页面时强制刷新视图，保证内容与数据源同步
            RefreshTasksView();
        }

        /// <summary>
        /// 离开页面时取消订阅，防止内存泄漏。
        /// </summary>
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (ViewModel != null)
                ViewModel.CollectionChanged -= OnTasksChanged;
        }

        private void RefreshTasksView()
        {
            TasksView.Clear();
            if (ViewModel == null)
            {
                IsEmpty = true;
                return;
            }
            foreach (var task in ViewModel)
            {
                TasksView.Add(task);
            }
            IsEmpty = ViewModel.Count == 0;
        }

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                RefreshTasksView();
            });
        }
    }
}
