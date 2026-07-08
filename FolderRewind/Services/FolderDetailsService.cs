using FolderRewind.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class FolderDetailsService
{
    public static IReadOnlyList<FolderDetailsSection> BuildBaseSections(BackupConfig config, ManagedFolder folder)
    {
        BackupStoragePathService.TryResolveBackupStoragePaths(
            config.DestinationPath ?? string.Empty,
            folder.DisplayName ?? string.Empty,
            folder.Path,
            out _,
            out string backupSubDir,
            out string metadataDir);

        return
        [
            new FolderDetailsSection
            {
                Title = I18n.GetString("FolderDetailsDialog_Section_Basic"),
                Items =
                {
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_Name"), Value = folder.DisplayName ?? string.Empty },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_Path"), Value = folder.Path ?? string.Empty },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_ConfigType"), Value = config.ConfigType ?? string.Empty },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_LastBackup"), Value = folder.LastBackupTime ?? string.Empty },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_BackupDirectory"), Value = backupSubDir },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_MetadataDirectory"), Value = metadataDir },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_Size"), Value = I18n.GetString("FolderDetailsDialog_Loading"), IsLoading = true },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_FileCount"), Value = I18n.GetString("FolderDetailsDialog_Loading"), IsLoading = true },
                    new FolderDetailsItem { Label = I18n.GetString("FolderDetailsDialog_DirectoryCount"), Value = I18n.GetString("FolderDetailsDialog_Loading"), IsLoading = true }
                }
            }
        ];
    }

    public static async Task<FolderStatisticsSnapshot> ComputeStatisticsAsync(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            long totalBytes = 0;
            int fileCount = 0;
            int directoryCount = 0;

            foreach (string directory in Directory.EnumerateDirectories(folderPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                directoryCount++;
            }

            foreach (string file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += new FileInfo(file).Length;
                fileCount++;
            }

            return new FolderStatisticsSnapshot
            {
                TotalBytes = totalBytes,
                FileCount = fileCount,
                DirectoryCount = directoryCount
            };
        }, cancellationToken);
    }
}
