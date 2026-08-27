using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        /// <summary>
        /// 解析备份文件类型：优先取历史记录中的类型（可信），缺失时按文件名前缀推断（兜底）。
        /// </summary>
        private static string ResolveBackupType(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            if (file == null)
            {
                return "Full";
            }

            if (config != null && !string.IsNullOrWhiteSpace(folderName))
            {
                var historyType = HistoryService.GetBackupTypeForFile(config.Id, folderName, file.Name);
                if (!string.IsNullOrWhiteSpace(historyType))
                {
                    return historyType;
                }
            }

            return BackupArchiveTypePolicy.InferFromFileName(file.Name);
        }

        private static bool IsFullBackupFile(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            var backupType = ResolveBackupType(file, config, folderName);
            return BackupArchiveTypePolicy.IsSelfContained(backupType, file.Name);
        }

        private static bool IsIncrementalBackupFile(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            var backupType = ResolveBackupType(file, config, folderName);
            return BackupArchiveTypePolicy.IsIncremental(backupType);
        }

        /// <summary>
        /// 构建恢复链：全量目标即为单文件链；增量目标委托 <see cref="BackupChainPlanner"/>
        /// 取"最近全量 + 时间窗内增量"，目标不在窗口内时强制补入。
        /// MissingBaseFull 状态表示找不到目标之前的基准 Full（老版本历史数据），
        /// 由调用方决定是否回退兼容链路；目录枚举保持延迟，避免全量场景的无谓扫描。
        /// </summary>
        private static (RestoreChainBuildStatus Status, List<FileInfo> Chain) BuildRestoreChainWithStatus(DirectoryInfo backupDir, FileInfo targetFile, string backupType, BackupConfig? config = null, string? folderName = null)
        {
            if (!backupDir.Exists)
            {
                return (RestoreChainBuildStatus.NotFound, new List<FileInfo>());
            }

            bool isIncremental =
                BackupArchiveTypePolicy.IsIncremental(backupType) ||
                IsIncrementalBackupFile(targetFile, config, folderName);

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            // 保持延迟枚举：全量还原无需扫描目录；增量还原则由规划器只枚举一次。
            var files = backupDir.EnumerateFiles("*", enumOptions);
            var plan = BackupChainPlanner.Build(
                files,
                targetFile,
                isIncremental,
                new BackupChainPlanOptions<FileInfo>
                {
                    GetTimestamp = file => file.LastWriteTime,
                    GetIdentity = file => file.FullName,
                    IsFull = file => IsFullBackupFile(file, config, folderName),
                    IsIncremental = file => IsIncrementalBackupFile(file, config, folderName),
                    SelectBaseFull = candidates => candidates
                        .OrderByDescending(file => file.LastWriteTime)
                        .FirstOrDefault(),
                    OrderChain = candidates => candidates
                        .OrderBy(file => file.LastWriteTime)
                        .ThenBy(file => file.Name),
                    IdentityComparer = StringComparer.OrdinalIgnoreCase,
                    PrependBaseFull = true,
                    IncludeTargetInWindow = false,
                    EnsureTargetIncluded = true,
                    Deduplicate = true
                });

            if (plan.Status == BackupChainPlanStatus.MissingBaseFull)
            {
                Log(I18n.Format("BackupService_Log_NoBaseFullFoundTryIncrementOnly"), LogLevel.Warning);
                return (RestoreChainBuildStatus.MissingBaseFull, new List<FileInfo>());
            }

            var chain = plan.Items.ToList();
            if (isIncremental)
            {
                Log(I18n.Format("BackupService_Log_RestoreChainBuilt", chain.Count), LogLevel.Debug);
            }

            return (RestoreChainBuildStatus.Success, chain);
        }

        /// <summary>
        /// 构建恢复链并丢弃状态信息（仅当调用方不关心 MissingBaseFull 时使用）。
        /// </summary>
        private static List<FileInfo> BuildRestoreChain(DirectoryInfo backupDir, FileInfo targetFile, string backupType, BackupConfig? config = null, string? folderName = null)
        {
            return BuildRestoreChainWithStatus(backupDir, targetFile, backupType, config, folderName).Chain;
        }
    }
}
