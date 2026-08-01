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
        private static async Task<bool> ApplyRestoreChainAsync(
            IReadOnlyList<FileInfo> chain,
            string targetDir,
            string sevenZipExe,
            string? password,
            BackupTask? restoreTask)
        {
            if (chain == null || chain.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < chain.Count; i++)
            {
                var file = chain[i];
                double segmentBase = (double)i / chain.Count * 100;
                double segmentRange = 100.0 / chain.Count;

                if (restoreTask != null && chain.Count > 1)
                {
                    int fileIndex = i;
                    await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_Restoring_N", fileIndex + 1, chain.Count));
                }

                Log(I18n.Format("BackupService_Log_RestoreApplyingArchive", file.Name), LogLevel.Info);
                string extractArgs = BuildRestoreExtractArguments(file.FullName, targetDir, password);
                string safeExtractArgs = string.IsNullOrWhiteSpace(password) ? extractArgs : extractArgs.Replace(password, "***");
                bool ok = await RunSevenZipProcessAsync(
                    sevenZipExe,
                    extractArgs,
                    file.DirectoryName,
                    safeExtractArgs,
                    restoreTask,
                    progressBase: segmentBase,
                    progressRange: segmentRange);

                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task<bool> ApplySmartRestorePlanAsync(
            SmartRestorePlan plan,
            string targetDir,
            string sevenZipExe,
            string? password,
            BackupTask? restoreTask)
        {
            var groups = plan.ArchiveGroups.Where(g => g.Files.Count > 0).ToList();
            if (groups.Count == 0)
            {
                if (restoreTask != null)
                {
                    await RunOnUIAsync(() =>
                    {
                        restoreTask.IsIndeterminate = false;
                        restoreTask.Progress = 100;
                    });
                }
                return true;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                double segmentBase = (double)i / groups.Count * 100;
                double segmentRange = 100.0 / groups.Count;
                string listFile = Path.GetTempFileName();

                try
                {
                    File.WriteAllLines(listFile, group.Files);
                    if (restoreTask != null && groups.Count > 1)
                    {
                        int fileIndex = i;
                        await RunOnUIAsync(() => restoreTask.Status = I18n.Format("BackupService_Task_Restoring_N", fileIndex + 1, groups.Count));
                    }

                    Log(I18n.Format("BackupService_Log_RestoreApplyingArchive", group.Archive.Name), LogLevel.Info);
                    string extractArgs = BuildRestoreExtractArguments(group.Archive.FullName, targetDir, password, listFile);
                    string safeExtractArgs = string.IsNullOrWhiteSpace(password) ? extractArgs : extractArgs.Replace(password, "***");
                    bool ok = await RunSevenZipProcessAsync(
                        sevenZipExe,
                        extractArgs,
                        group.Archive.DirectoryName,
                        safeExtractArgs,
                        restoreTask,
                        progressBase: segmentBase,
                        progressRange: segmentRange);

                    if (!ok)
                    {
                        return false;
                    }
                }
                finally
                {
                    try { File.Delete(listFile); } catch { }
                }
            }

            return true;
        }

        private static string BuildRestoreExtractArguments(string archivePath, string targetDir, string? password, string? listFile = null)
        {
            var sb = new StringBuilder();
            sb.Append($"x \"{archivePath}\"");
            if (!string.IsNullOrWhiteSpace(listFile))
            {
                sb.Append($" @\"{listFile}\"");
            }
            sb.Append($" -o\"{targetDir}\" -y -bsp1");
            if (!string.IsNullOrWhiteSpace(password))
            {
                sb.Append($" -p\"{password}\"");
            }
            return sb.ToString();
        }

        private static async Task<bool> ConfirmMissingBaseFullFallbackAsync(string folderDisplayName, string backupFileName)
        {
            var xamlRoot = MainWindowService.GetXamlRoot();
            if (xamlRoot == null)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("BackupService_RestoreMissingBaseFull_Title"),
                Content = I18n.Format("BackupService_RestoreMissingBaseFull_Content", backupFileName),
                PrimaryButtonText = I18n.GetString("BackupService_RestoreMissingBaseFull_Primary"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await RunOnUIAsync(async () => await dialog.ShowAsync());
            return result == ContentDialogResult.Primary;
        }

    }
}
