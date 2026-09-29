using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{
    /// <summary>
    /// 源文件夹的备份范围模式。
    /// </summary>
    public enum BackupSourceScopeMode
    {
        /// <summary>整个目录都进入备份范围，由过滤器继续裁剪。</summary>
        All = 0,

        /// <summary>只有匹配 IncludePatterns 的条目才进入备份范围。</summary>
        Include = 1
    }

    /// <summary>
    /// 单个源文件夹允许进入备份的最大文件集合。配置过滤器和插件范围只能继续缩小它，不能扩大。
    /// 取自 1.9.x 的同名类型，去掉了该分支才有的配置变更守卫基础设施（GuardedObservableCollection 等）。
    /// </summary>
    public sealed class BackupSourceScope
    {
        public BackupSourceScopeMode Mode { get; set; }

        public ObservableCollection<string> IncludePatterns { get; set; } = new();

        [JsonIgnore]
        public bool IsPartial => Mode == BackupSourceScopeMode.Include;
    }
}
