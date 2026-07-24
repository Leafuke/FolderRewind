using FolderRewind.Models;
using System;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace FolderRewind.Services
{
    /// <summary>
    /// 备份配置运行时克隆辅助：用于临时注入过滤器/范围等一次性参数，避免把运行时计算结果写回用户配置。
    /// </summary>
    internal static class BackupConfigCloneService
    {
        public static BackupConfig CloneForRuntimeMutation(BackupConfig source, string errorMessage, bool ensureBackupScope = false)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var json = JsonSerializer.Serialize(source, AppJsonContext.Default.BackupConfig);
            var clone = JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupConfig)
                ?? throw new InvalidOperationException(errorMessage);

            clone.Archive ??= new ArchiveSettings();
            clone.Filters ??= new FilterSettings();
            clone.Filters.Blacklist ??= new ObservableCollection<string>();
            clone.Filters.BackupWhitelist ??= new ObservableCollection<string>();
            clone.Filters.RestoreWhitelist ??= new ObservableCollection<string>();

            if (ensureBackupScope)
            {
                clone.BackupScope ??= new BackupScopeSettings();
            }

            return clone;
        }
    }
}
