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
            return backupType.Equals("Full", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIncrementalBackupFile(FileInfo file, BackupConfig? config = null, string? folderName = null)
        {
            var backupType = ResolveBackupType(file, config, folderName);
            return BackupArchiveTypePolicy.IsIncremental(backupType);
        }

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

        private static List<FileInfo> BuildRestoreChain(DirectoryInfo backupDir, FileInfo targetFile, string backupType, BackupConfig? config = null, string? folderName = null)
        {
            return BuildRestoreChainWithStatus(backupDir, targetFile, backupType, config, folderName).Chain;
        }
    }
}
