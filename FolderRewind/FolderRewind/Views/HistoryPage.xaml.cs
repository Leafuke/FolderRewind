using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace FolderRewind.Views
{
    public sealed partial class HistoryPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<HistoryItem> FilteredHistory { get; set; } = new();

        private bool _isEmpty = true;
        public bool IsEmpty
        {
            get => _isEmpty;
            set { _isEmpty = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty))); }
        }

        public HistoryPage()
        {
            this.InitializeComponent();
            ConfigFilter.ItemsSource = MockDataService.AllConfigs;
            HistoryList.ItemsSource = FilteredHistory;
        }

        private void ConfigFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConfigFilter.SelectedItem is BackupConfig config)
            {
                FolderFilter.ItemsSource = config.Folders;
                FolderFilter.SelectedIndex = -1;
                FilteredHistory.Clear();
                IsEmpty = true;
            }
        }

        private void FolderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FolderFilter.SelectedItem is ManagedFolder folder)
            {
                LoadHistoryForFolder(folder);
            }
        }

        private void LoadHistoryForFolder(ManagedFolder folder)
        {
            FilteredHistory.Clear();
            // 模拟数据：实际应根据 folder.Id 查询数据库
            FilteredHistory.Add(new HistoryItem { Time = "10:30", Date = "今天", Message = $"[{folder.DisplayName}] 自动备份", CommitId = "a1b2c3" });
            FilteredHistory.Add(new HistoryItem { Time = "昨天", Date = "12/10", Message = "手动全量备份", CommitId = "d4e5f6" });
            IsEmpty = false;
        }
    }
}