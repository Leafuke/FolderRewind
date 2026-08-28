using Microsoft.UI.Xaml.Controls;
using FolderRewind.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        private static async Task<bool> ValidateRestoreChainAsync(
            List<FileInfo> chain,
            string sevenZipExe,
            string? password,
            BackupTask? restoreTask)
        {
            if (chain.Count == 0) return false;
            for (var index = 0; index < chain.Count; index++)
            {
                var file = chain[index];
                if (restoreTask is not null)
                {
                    var current = index;
                    await RunOnUIAsync(() => restoreTask.Status = I18n.Format(
                        "BackupService_Task_VerifyingRestore_N",
                        current + 1,
                        chain.Count));
                }
                var arguments = $"t \"{file.FullName}\" -bsp1";
                if (!string.IsNullOrWhiteSpace(password)) arguments += $" -p\"{password}\"";
                var safe = string.IsNullOrWhiteSpace(password) ? arguments : arguments.Replace(password, "***");
                if (!await RunSevenZipProcessAsync(sevenZipExe, arguments, file.DirectoryName, safe))
                    return false;
            }
            return true;
        }
    }
}
