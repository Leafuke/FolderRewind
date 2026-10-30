namespace FolderRewind.Models
{
    public enum BackupMode
    {
        Full = 0,    // 全量备份：每次生成独立完整包
        Smart = 1,   // 正式版旧数值 Incremental=1，语义保持为 Smart
        Rolling = 2  // 正式版旧数值 Overwrite=2，改为不可变 copy-on-write
    }

}
