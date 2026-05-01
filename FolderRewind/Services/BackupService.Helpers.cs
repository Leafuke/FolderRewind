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
        // 跨流程小工具集中在这里，避免主编排文件再次膨胀。

        private static Task RunOnUIAsync(Action action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }

        private static Task<T> RunOnUIAsync<T>(Func<Task<T>> action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }

        private static bool TryResolveStorageFolderName(string? rawFolderName, string? fallbackPath, out string storageFolderName)
            => BackupStoragePathService.TryResolveStorageFolderName(rawFolderName, fallbackPath, out storageFolderName);

        private static bool TryBuildPathWithinRoot(string rootPath, string childName, out string fullPath)
            => BackupStoragePathService.TryBuildPathWithinRoot(rootPath, childName, out fullPath);

        private static bool IsPathInsideRoot(string candidatePath, string rootPath)
            => BackupStoragePathService.IsPathInsideRoot(candidatePath, rootPath);

        private static bool TryResolveBackupStoragePaths(
            string destinationRoot,
            string folderDisplayName,
            string? fallbackPath,
            out string storageFolderName,
            out string backupSubDir,
            out string metadataDir)
            => BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationRoot,
                folderDisplayName,
                fallbackPath,
                out storageFolderName,
                out backupSubDir,
                out metadataDir);

        private static string GenerateFileName(string baseName, string format, string prefix, string comment)
        {
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string safeComment = SanitizeFileName(comment);

            // 格式: [Full][2025-01-01_12-00-00]WorldName [Comment].7z
            string commentPart = string.IsNullOrEmpty(safeComment) ? "" : $" [{safeComment}]";
            return $"[{prefix}][{timeStr}]{baseName}{commentPart}.{format}";
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            // 额外过滤掉中括号，以免破坏解析逻辑
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (!invalid.Contains(c) && c != '[' && c != ']')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static int GetConfigIndex(BackupConfig config)
        {
            try
            {
                var configs = ConfigService.CurrentConfig?.BackupConfigs;
                if (configs == null) return -1;

                for (int i = 0; i < configs.Count; i++)
                {
                    if (configs[i]?.Id == config.Id)
                    {
                        return i;
                    }
                }
            }
            catch
            {
            }

            return -1;
        }

        /// <summary>
        /// 通过备份文件名还原（供 KnotLink 远程调用使用）

        /// <summary>
        /// 获取配置的加密密码（从 EncryptionService 安全存储中检索）。
        /// </summary>
        private static string? ResolvePassword(BackupConfig config)
        {
            if (!config.IsEncrypted) return null;
            return EncryptionService.RetrievePassword(config.Id);
        }

        private static bool TryResolveRequiredPassword(BackupConfig config, out string? password, BackupTask? taskToUpdate = null)
        {
            password = ResolvePassword(config);

            if (!config.IsEncrypted)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                return true;
            }

            Log(MissingEncryptionPasswordMessage, LogLevel.Error);
            if (taskToUpdate != null)
            {
                UiDispatcherService.Enqueue(() =>
                {
                    if (string.IsNullOrWhiteSpace(taskToUpdate.ErrorMessage))
                    {
                        taskToUpdate.ErrorMessage = MissingEncryptionPasswordMessage;
                    }
                });
            }

            return false;
        }

        private static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogService.Log(message, InferLevel(message));
        }

        private static void Log(string message, LogLevel level)
        {
            System.Diagnostics.Debug.WriteLine(message);
            LogService.Log(message, level);
        }

        private static LogLevel InferLevel(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return LogLevel.Info;

            var lower = message.ToLowerInvariant();

            if (lower.Contains("[7z err]") || lower.Contains("[错误]") || lower.Contains("[失败]") || lower.Contains("[异常]") || lower.Contains("严重错误") || lower.Contains("[系统错误]"))
                return LogLevel.Error;

            if (lower.Contains("[警告]") || lower.Contains("[warning]"))
                return LogLevel.Warning;

            if (lower.Contains("[debug]") || lower.Contains("[调试]") || lower.Contains("[cmd]"))
                return LogLevel.Debug;

            return LogLevel.Info;
        }
    }
}

