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
        private static async Task<bool> ValidateRestoreChainAsync(List<FileInfo> chain, string sevenZipExe, string? password, BackupTask? restoreTask)
        {
            if (chain == null || chain.Count == 0) return false;

            // 逐包做完整性检测，提前挡住损坏归档，避免真正解压时把目标目录弄成半成品。
            for (int i = 0; i < chain.Count; i++)
            {
                var file = chain[i];
                if (restoreTask != null)
                {
                    int fileIndex = i;
                    await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_VerifyingRestore_N", fileIndex + 1, chain.Count));
                }

                string testArgs = $"t \"{file.FullName}\" -bsp1";
                if (!string.IsNullOrWhiteSpace(password))
                {
                    testArgs = $"t \"{file.FullName}\" -bsp1 -p\"{password}\"";
                }

                string safeTestArgs = string.IsNullOrWhiteSpace(password) ? testArgs : testArgs.Replace(password, "***");
                bool ok = await RunSevenZipProcessAsync(sevenZipExe, testArgs, file.DirectoryName, safeTestArgs);
                if (!ok)
                {
                    Log(I18n.Format("BackupService_Log_RestoreIntegrityArchiveCheckFailed", file.Name), LogLevel.Error);
                    return false;
                }
            }

            if (restoreTask != null)
            {
                await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_Restoring"));
            }

            return true;
        }

        private static bool TryBuildSmartRestorePlan(IReadOnlyList<FileInfo> chain, BackupMetadata metadata, out SmartRestorePlan? plan)
        {
            plan = null;
            if (chain == null || chain.Count == 0)
            {
                return false;
            }

            // 通过“最终文件 -> 最近归档拥有者”的映射，生成最小提取集合。
            var normalized = NormalizeBackupMetadata(metadata);
            var recordMap = normalized.BackupRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.ArchiveFileName))
                .GroupBy(r => r.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.CreatedAtUtc).First(),
                    StringComparer.OrdinalIgnoreCase);

            if (!recordMap.TryGetValue(chain[0].Name, out var baseRecord)
                || !IsFullBackupRecord(baseRecord)
                || baseRecord.FullFileList == null
                || baseRecord.FullFileList.Count == 0)
            {
                return false;
            }

            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in baseRecord.FullFileList.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                owners[file] = chain[0].Name;
            }

            for (int i = 1; i < chain.Count; i++)
            {
                if (!recordMap.TryGetValue(chain[i].Name, out var record))
                {
                    return false;
                }

                foreach (var deleted in record.DeletedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    owners.Remove(deleted);
                }

                foreach (var added in record.AddedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    owners[added] = record.ArchiveFileName;
                }

                foreach (var modified in record.ModifiedFiles.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    owners[modified] = record.ArchiveFileName;
                }
            }

            var archiveLookup = chain.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var archiveIndex = chain
                .Select((file, index) => new { file.Name, Index = index })
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

            var groups = owners
                .GroupBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => archiveLookup.ContainsKey(g.Key))
                .Select(g => new SmartRestoreArchiveGroup
                {
                    Archive = archiveLookup[g.Key],
                    Files = g.Select(x => x.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .OrderBy(g => archiveIndex[g.Archive.Name])
                .ToList();

            plan = new SmartRestorePlan
            {
                Chain = chain.ToList(),
                ArchiveGroups = groups
            };
            return true;
        }

        private static bool IsFullBackupRecord(BackupChangeRecord record)
        {
            string backupType = string.IsNullOrWhiteSpace(record.BackupType)
                ? BackupArchiveTypePolicy.InferFromFileName(record.ArchiveFileName)
                : record.BackupType;
            return string.Equals(backupType, "Full", StringComparison.OrdinalIgnoreCase);
        }
    }
}
