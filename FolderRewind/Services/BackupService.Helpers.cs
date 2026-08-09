using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            string safeBaseName = SanitizeFileName(baseName);
            string safeComment = SanitizeFileName(comment);
            string safeFormat = SanitizeFileName(format).Trim('.');
            string safePrefix = SanitizeFileName(prefix);

            if (string.IsNullOrWhiteSpace(safeBaseName)) safeBaseName = "Backup";
            if (string.IsNullOrWhiteSpace(safeFormat)) safeFormat = "7z";
            if (string.IsNullOrWhiteSpace(safePrefix)) safePrefix = "Backup";

            // 格式: [Full][2025-01-01_12-00-00]WorldName [Comment].7z
            string commentPart = string.IsNullOrEmpty(safeComment) ? "" : $" [{safeComment}]";
            string fileName = $"[{safePrefix}][{timeStr}]{safeBaseName}{commentPart}.{safeFormat}";

            // 显示名可能来自旧配置、插件或用户输入；调用 7-Zip 前必须再次关闭非法路径和 ADS 入口。
            if (!BackupStoragePathService.IsSafeSinglePathSegment(fileName))
            {
                throw new InvalidDataException("The generated backup archive name is not a safe Windows file name.");
            }

            return fileName;
        }

        private static string SanitizeFileName(string name)
        {
            if (!BackupStoragePathService.TryResolveStorageFolderName(name, null, out var sanitized)) return "";

            // 中括号由归档类型和时间戳占用，来源名称与评论不能注入额外的解析片段。
            return sanitized.Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal)
                .Trim();
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

