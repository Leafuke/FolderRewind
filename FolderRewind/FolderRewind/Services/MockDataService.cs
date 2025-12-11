using FolderRewind.Models;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.Services
{
    public static class MockDataService
    {
        // 全局唯一的配置列表
        public static ObservableCollection<BackupConfig> AllConfigs { get; } = new();

        // 获取所有被收藏的文件夹（跨配置聚合）
        public static ObservableCollection<ManagedFolder> GetFavorites()
        {
            var favorites = new ObservableCollection<ManagedFolder>();
            foreach (var config in AllConfigs)
            {
                foreach (var folder in config.Folders)
                {
                    if (folder.IsFavorite) favorites.Add(folder);
                }
            }
            return favorites;
        }

        // 初始化一些测试数据（可选，为了防止一片空白）
        public static void Initialize()
        {
            if (AllConfigs.Count > 0) return;

            var c1 = new BackupConfig { Name = "工作项目", SummaryText = "默认配置", IconGlyph = "\uE82D" };
            c1.Folders.Add(new ManagedFolder { DisplayName = "演示文件夹 A", FullPath = @"C:\Demo\A", IsFavorite = true });

            AllConfigs.Add(c1);
        }
    }
}