using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging; // 需要引用

namespace FolderRewind.Models
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class BackupConfig : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        // 移除具体的路径显示，改为摘要

        public string SummaryText { get; set; } = "3个文件夹 · 每周备份";
        public string IconGlyph { get; set; } = "\uE8B7";
        public ObservableCollection<ManagedFolder> Folders { get; } = new();
    }

    public class ManagedFolder : ObservableObject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string DisplayName { get; set; }
        public string FullPath { get; set; }
        public string Description { get; set; } = "暂无描述"; // 新增描述
        public string ImagePath { get; set; } // 图片路径
        public bool IsFavorite { get; set; } // 是否收藏
        public string StatusText { get; set; } = "已同步";
        public string LastBackupTime { get; set; } = "2小时前";

        // 实际开发中需绑定BitmapImage，这里简化处理
        public string PlaceholderIcon => string.IsNullOrEmpty(ImagePath) ? "\uE8B7" : "";
    }

    // 新增：备份任务模型
    public class BackupTask : ObservableObject
    {
        public string FolderName { get; set; }
        public double Progress { get; set; } // 0-100
        public string Speed { get; set; } // e.g., "5.2 MB/s"
        public string Status { get; set; } // "正在上传", "压缩中"
        public bool IsPaused { get; set; }
    }

    // 新增：历史记录模型
    public class HistoryItem : ObservableObject
    {
        public string CommitId { get; set; } // e.g. "a1b2c3d"
        public string Message { get; set; } // "自动备份：文档更新"
        public string Time { get; set; }
        public string Date { get; set; }
        public bool IsRestorePoint { get; set; } // 是否是关键节点
    }
}