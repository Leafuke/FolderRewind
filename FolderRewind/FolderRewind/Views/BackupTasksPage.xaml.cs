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

            // 订阅集合变化以更新 IsEmpty
            if (ViewModel != null)
            {
                ViewModel.CollectionChanged += OnTasksChanged;
                IsEmpty = ViewModel.Count == 0;
            }

            // 在页面卸载时取消订阅，防止内存泄漏
            this.Unloaded += (_, __) =>
            {
                if (ViewModel != null)
                    ViewModel.CollectionChanged -= OnTasksChanged;
            };
        }

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 直接读取集合长度并更新属性（在 UI 线程）
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                IsEmpty = ViewModel == null || ViewModel.Count == 0;
            });
        }
    }
}