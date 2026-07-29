using FolderRewind.Models;
using System;

namespace FolderRewind.Services
{
    /// <summary>
    /// 备份配置运行时克隆辅助：用于临时注入过滤器/范围等一次性参数，避免把运行时计算结果写回用户配置。
    /// </summary>
    internal static class BackupConfigCloneService
    {
        public static BackupConfig CloneForRuntimeMutation(BackupConfig source, string errorMessage)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            return JsonCloneService.Clone(source, AppJsonContext.Default.BackupConfig, errorMessage);
        }
    }
}
