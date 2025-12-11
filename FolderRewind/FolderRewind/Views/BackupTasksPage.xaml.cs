using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;

namespace FolderRewind.Views
{
    public sealed partial class BackupTasksPage : Page
    {
        public ObservableCollection<BackupTask> Tasks { get; set; } = new();
        private DispatcherTimer _timer;

        public BackupTasksPage()
        {
            this.InitializeComponent();
            LoadMockTasks();
            StartSimulation();
        }

        private void LoadMockTasks()
        {
            Tasks.Add(new BackupTask { FolderName = "Work_Project_V2", Progress = 45, Status = "正在上传...", Speed = "12.5 MB/s", IsPaused = false });
            Tasks.Add(new BackupTask { FolderName = "Photos_2024", Progress = 0, Status = "等待中", Speed = "-", IsPaused = false });
        }

        // 模拟进度更新
        private void StartSimulation()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (s, e) =>
            {
                foreach (var task in Tasks)
                {
                    if (!task.IsPaused && task.Progress < 100)
                    {
                        task.Progress += 2;
                        if (task.Progress >= 100)
                        {
                            task.Progress = 100;
                            task.Status = "完成";
                            task.Speed = "";
                        }
                    }
                }
            };
            _timer.Start();
        }

        // 暂停/继续按钮点击
        private void OnTaskControlClick(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is BackupTask task)
            {
                task.IsPaused = !task.IsPaused;
                task.Status = task.IsPaused ? "已暂停" : "正在上传...";
                task.Speed = task.IsPaused ? "0 KB/s" : "10.2 MB/s";
                // 触发 PropertyChanged (需在 Model 中实现)
            }
        }
    }
}