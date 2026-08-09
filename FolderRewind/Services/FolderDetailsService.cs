using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class FolderDetailsService
{
    public static IReadOnlyList<FolderDetailsSection> BuildBaseSections(BackupConfig config, ManagedFolder folder)
    {
        string modifiedDate = string.Empty;
        try
        {
            modifiedDate = Directory.GetLastWriteTime(folder.Path).ToString("yyyy/MM/dd HH:mm:ss");
        }
        catch
        {
            // 如果无法读取修改日期（如路径不存在），则留空
        }

        var section = new FolderDetailsSection
        {
            Title = I18n.GetString("FolderDetailsDialog_Section_Basic"),
            Items =
            {
                new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_Name"), Value = folder.DisplayName ?? string.Empty },
                new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_Path"), Value = folder.Path ?? string.Empty },
                new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_ModifiedDate"), Value = modifiedDate },
                new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_LastBackup"), Value = folder.LastBackupTime ?? string.Empty }
            }
        };

        if (folder.Selection?.Mode == BackupSourceSelectionMode.Include)
        {
            // 精确来源的统计只覆盖 Include 允许的文件，直接展示规则可避免把 0 误解为目录扫描失败。
            section.Items.Add(new FolderDetailsItem
            {
                Label = I18n.GetString("FolderDetailsDialog_IncludePatterns"),
                Value = string.Join(Environment.NewLine, folder.Selection.IncludePatterns)
            });
            section.Items.Add(new FolderDetailsItem
            {
                Label = I18n.GetString("FolderDetailsDialog_MatchStatus"),
                Value = I18n.GetString("FolderDetailsDialog_Loading"),
                IsLoading = true
            });
        }

        section.Items.Add(new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_Size"), Value = I18n.GetString("FolderDetailsDialog_Loading"), IsLoading = true });
        section.Items.Add(new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_FileCount"), Value = I18n.GetString("FolderDetailsDialog_Loading"), IsLoading = true });
        section.Items.Add(new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_DirectoryCount"), Value = I18n.GetString("FolderDetailsDialog_Loading"), IsLoading = true });

        return [section];
    }

    public static async Task<FolderStatisticsSnapshot> ComputeStatisticsAsync(string folderPath, CancellationToken cancellationToken)
        => await ComputeStatisticsAsync(
            new BackupConfig(),
            new ManagedFolder { Path = folderPath },
            cancellationToken);

    public static async Task<FolderStatisticsSnapshot> ComputeStatisticsAsync(
        BackupConfig config,
        ManagedFolder folder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            long totalBytes = 0;
            int fileCount = 0;
            int directoryCount = 0;

            var files = BackupSourceFileEnumerator.Enumerate(
                folder.Path,
                folder.Selection,
                file => BackupService.ShouldIncludeInBackup(
                    file,
                    folder.Path,
                    folder.Path,
                    config.Filters),
                cancellationToken);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += file.Size;
                fileCount++;
                var directory = Path.GetDirectoryName(file.RelativePath);
                while (!string.IsNullOrWhiteSpace(directory) && directories.Add(directory))
                {
                    directory = Path.GetDirectoryName(directory);
                }
            }
            directoryCount = directories.Count;

            return new FolderStatisticsSnapshot
            {
                TotalBytes = totalBytes,
                FileCount = fileCount,
                DirectoryCount = directoryCount
            };
        }, cancellationToken);
    }
}
