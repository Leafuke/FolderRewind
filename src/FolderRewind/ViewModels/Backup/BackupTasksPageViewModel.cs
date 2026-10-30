using FolderRewind.Models;
using FolderRewind.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace FolderRewind.ViewModels
{
    public sealed class BackupTasksPageViewModel : ViewModelBase
    {
        private bool _isActive;
        private bool _isEmpty = true;
        private int _taskCount;
        private string? _operationId, _taskId;
        public bool IsFiltered => _operationId is not null || _taskId is not null;

        public void SetNotificationFilter(string? operationId, string? taskId)
        {
            _operationId = operationId; _taskId = taskId;
            OnPropertyChanged(nameof(IsFiltered));
            RefreshTasksView();
        }

        // 绑定视图（避免 MSIX + Trim 下 WinRT 对自定义泛型集合投影异常）
        public BatchObservableCollection<object> TasksView { get; } = new();

        public int TaskCount
        {
            get => _taskCount;
            private set { if (SetProperty(ref _taskCount, value)) OnPropertyChanged(nameof(TaskSummary)); }
        }
        public string TaskSummary => TaskCount == 0 ? I18n.GetString("BackupTasks_EmptySummary") : I18n.Format("BackupTasks_Count", TaskCount);

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
            var tasks = BackupService.ActiveTasks.Where(t => _taskId is not null ? t.TaskId == _taskId
                : _operationId is null || t.OperationId == _operationId).ToArray();
            TasksView.Synchronize(tasks.Cast<object>());

            TaskCount = tasks.Length;
            IsEmpty = TaskCount == 0;
        }
    }
}
