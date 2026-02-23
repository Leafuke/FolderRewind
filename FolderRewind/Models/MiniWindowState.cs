namespace FolderRewind.Models
{
    /// <summary>
    /// Mini 窗口运行时状态
    /// </summary>
    public enum MiniWindowVisualState
    {
        /// <summary>默认（无变更），使用系统主题色丝带</summary>
        Normal = 0,
        /// <summary>检测到文件夹内容有变更，丝带变为警示色</summary>
        Changed = 1,
        /// <summary>正在执行备份，丝带脉冲动画</summary>
        BackingUp = 2,
        /// <summary>备份完成（短暂），丝带绿色</summary>
        BackupDone = 3,
        /// <summary>备份失败，丝带红色</summary>
        BackupFailed = 4,
    }

    /// <summary>
    /// 描述一个 Mini 窗口实例的关联数据
    /// </summary>
    public class MiniWindowContext
    {
        /// <summary>关联的备份配置</summary>
        public BackupConfig Config { get; set; }

        /// <summary>关联的目标文件夹</summary>
        public ManagedFolder Folder { get; set; }

        /// <summary>输入框展开方向：Left 或 Right</summary>
        public MiniExpandDirection ExpandDirection { get; set; } = MiniExpandDirection.Right;
    }

    public enum MiniExpandDirection
    {
        Left = 0,
        Right = 1,
    }
}
