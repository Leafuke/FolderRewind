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
        /// 安全恢复工作区-准备阶段：把目标目录整体移动到同级临时快照并重建空目标目录，
        /// 使还原在干净目录中执行；目标本不存在时仅创建目录（无快照、无需回滚）。
        /// </summary>
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

        /// <summary>
        /// 安全恢复工作区-提交阶段（仅在还原成功后调用）：
        /// 清理内部标记目录、从快照回迁白名单内容，最后删除快照目录。提交失败由调用方触发回滚。
        /// </summary>
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

        /// <summary>
        /// 安全恢复工作区-回滚阶段：删除还原出一半的新目录，把快照整体移回原位。
        /// 采用目录级替换而非逐文件恢复，避免新旧状态混杂；快照缺失视为回滚失败。
        /// </summary>
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

        /// <summary>
        /// 在目标目录的同级生成"目录名-Temp"快照路径；被占用时追加 -1、-2 递增后缀。
        /// 与目标同盘同级，保证 Directory.Move 不跨卷。
        /// </summary>
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
