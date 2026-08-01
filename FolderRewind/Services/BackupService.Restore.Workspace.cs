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
        private static bool TryPrepareSafeRestoreWorkspace(string targetDir, out string? tempDir, out string? errorMessage)
        {
            tempDir = null;
            errorMessage = null;

            try
            {
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    return true;
                }

                // 先搬走旧目录，确保还原在“干净目录”执行，失败可直接回滚。
                tempDir = CreateSafeRestoreTempDirectoryPath(targetDir);
                Directory.Move(targetDir, tempDir);
                Directory.CreateDirectory(targetDir);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryCommitSafeRestoreWorkspace(
            string targetDir,
            string tempDir,
            PathRuleMatcher? whitelistMatcher,
            out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                // 提交阶段要先补回白名单内容，再删除旧快照目录。
                CleanupInternalRestoreMarkers(targetDir);
                CopyRestoreWhitelistEntries(tempDir, targetDir, whitelistMatcher, targetDir);

                if (Directory.Exists(tempDir))
                {
                    ClearReadonlyAttributes(tempDir);
                    Directory.Delete(tempDir, true);
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static bool TryRollbackSafeRestoreWorkspace(string targetDir, string tempDir, out string? errorMessage)
        {
            errorMessage = null;

            try
            {
                // 回滚采用目录级替换，避免逐文件恢复造成新旧状态混杂。
                if (Directory.Exists(targetDir))
                {
                    ClearReadonlyAttributes(targetDir);
                    Directory.Delete(targetDir, true);
                }

                if (!Directory.Exists(tempDir))
                {
                    errorMessage = "Snapshot directory is missing.";
                    return false;
                }

                Directory.Move(tempDir, targetDir);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string CreateSafeRestoreTempDirectoryPath(string targetDir)
        {
            string normalizedTarget = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(normalizedTarget);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidOperationException("Restore target has no parent directory.");
            }

            string name = Path.GetFileName(normalizedTarget);
            string basePath = Path.Combine(parent, name + "-Temp");
            string candidate = basePath;
            int suffix = 1;

            while (Directory.Exists(candidate) || File.Exists(candidate))
            {
                candidate = basePath + "-" + suffix.ToString();
                suffix++;
            }

            return candidate;
        }

    }
}
