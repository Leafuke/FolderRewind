using FolderRewind.Models;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.Services
{
    // 这个类现在只是为了兼容现有代码，实际上直接透传 ConfigService 的数据
    public static class MockDataService
    {
        public static ObservableCollection<BackupConfig> AllConfigs => ConfigService.CurrentConfig.BackupConfigs;

        public static void Initialize()
        {
            ConfigService.Initialize();
        }

        // 获取所有配置下的所有文件夹，用于“收藏的文件夹”展示
        // 这里暂时简单的返回前几个文件夹作为示例
        public static ObservableCollection<ManagedFolder> GetFavorites()
        {
            var favs = new ObservableCollection<ManagedFolder>();
            if (AllConfigs.Count > 0)
            {
                foreach (var folder in AllConfigs[0].SourceFolders)
                {
                    favs.Add(folder);
                }
            }
            return favs;
        }
    }
}