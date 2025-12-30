using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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

            // ���ļ��ϱ仯�Ը��� IsEmpty
            if (ViewModel != null)
            {
                ViewModel.CollectionChanged += OnTasksChanged;
                IsEmpty = ViewModel.Count == 0;
                RefreshTasksView();
            }

            // ��ҳ��ж��ʱȡ�����ģ���ֹ�ڴ�й©
            this.Unloaded += (_, __) =>
            {
                if (ViewModel != null)
                    ViewModel.CollectionChanged -= OnTasksChanged;
            };
        }

        private void RefreshTasksView()
        {
            TasksView.Clear();
            if (ViewModel == null) return;
            foreach (var task in ViewModel)
            {
                TasksView.Add(task);
            }
        }

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // ֱ�Ӷ�ȡ���ϳ��Ȳ��������ԣ��� UI �̣߳�
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                IsEmpty = ViewModel == null || ViewModel.Count == 0;
                RefreshTasksView();
            });
        }
    }
}