using FolderRewind.Models;
using FolderRewind.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace FolderRewind.ViewModels
{
    public sealed class BackupTasksPageViewModel : ViewModelBase
    {
        private bool _isActive;
        private bool _isEmpty = true;
        private int _taskCount;

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public ObservableCollection<object> TasksView { get; } = new();

        public int TaskCount
        {
            get => _taskCount;
            private set => SetProperty(ref _taskCount, value);
        }

        public bool IsEmpty
        {
            get => _isEmpty;
            private set => SetProperty(ref _isEmpty, value);
        }

        public void Activate()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            BackupService.ActiveTasks.CollectionChanged += OnTasksChanged;
            RefreshTasksView();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            BackupService.ActiveTasks.CollectionChanged -= OnTasksChanged;
        }

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            EnqueueOnUiThread(RefreshTasksView);
        }

        private void RefreshTasksView()
        {
            TasksView.Clear();
            foreach (BackupTask task in BackupService.ActiveTasks)
            {
                TasksView.Add(task);
            }

            TaskCount = BackupService.ActiveTasks.Count;
            IsEmpty = TaskCount == 0;
        }
    }
}
