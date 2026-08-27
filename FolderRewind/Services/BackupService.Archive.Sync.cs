using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        // 仅清理旧版遗留的可识别临时产物；不触碰已提交归档。

        private static void CleanupArchiveTempArtifacts(DirectoryInfo backupDir, string format)
        {
            if (!backupDir.Exists) return;

            try
            {
                foreach (var file in backupDir.GetFiles())
                {
                    if (!IsArchiveTempArtifact(file, format))
                    {
                        continue;
                    }

                    try
                    {
                        if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            file.Attributes &= ~FileAttributes.ReadOnly;
                        }
                        file.Delete();
                    }
                    catch
                    {
                    }
                }

                foreach (var dir in backupDir.GetDirectories("__FolderRewind_SafeDelete_*"))
                {
                    try
                    {
                        ClearReadonlyAttributes(dir.FullName);
                        dir.Delete(true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsArchiveTempArtifact(FileInfo file, string format)
        {
            if (file == null || string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            string pattern = $@"\.{Regex.Escape(format)}\.tmp\d*$";
            return Regex.IsMatch(file.Name, pattern, RegexOptions.IgnoreCase);
        }

    }
}
