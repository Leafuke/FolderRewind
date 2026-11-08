using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class CloudSyncService
    {
        // 云同步与云存档的编排入口。
        // 这里主要负责命令组织、任务状态和错误反馈，具体历史/配置读写复用已有 Service。
        private const string DefaultRcloneExecutable = "rclone.exe";
        private const string DefaultWindowsPowerShellExecutable = "powershell.exe";
        private const string DefaultCommandShellExecutable = "cmd.exe";
        private const string UploadTaskIconGlyph = "\uE898";
        private const string DownloadTaskIconGlyph = "\uE896";
        private const int MaxRetryCount = 5;
        private const int MaxTimeoutSeconds = 86400;
        private const string InternalCloudStateDirectoryName = "_folderrewind";
        // 故意串行执行云命令，避免并发 rclone 时任务状态、日志和元数据写入互相打架。
        private static readonly SemaphoreSlim CommandSemaphore = new(1, 1);

        private sealed class ResolvedCommand
        {
            public RcloneExecutionContext? Execution { get; init; } = RcloneExecutionScope.Current;
            public required string ExecutablePath { get; init; }
            public required string Arguments { get; init; }
            public required string WorkingDirectory { get; init; }
            public required string DisplayPreview { get; init; }
            public required string LogPreview { get; init; }
        }

        private static void SerializeToFile<T>(string path, T value, JsonTypeInfo<T> typeInfo)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, value, typeInfo);
        }

        public static bool CanUseHistoryCloudActions(BackupConfig? config)
        {
            return CanUseManualCloudActions(config);
        }

        public static bool CanUseManualCloudActions(BackupConfig? config)
        {
            return config?.Cloud != null
                && config.Cloud.CommandMode == CloudCommandMode.Rclone;
        }

        public static string GetEffectiveExecutablePath(BackupConfig? config)
        {
            return ResolveRcloneExecutable(config?.Cloud.ConnectionProfileId is { } id
                && ConfigService.CurrentConfig.CloudConnections.SingleOrDefault(p => p.Id == id) is { } profile
                    ? new CloudSettings { ExecutablePath = profile.ExecutablePath } : null);
        }


        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }


        private static string AppendRemotePath(string root, params string[] segments)
            => CloudRemotePath.Append(root, segments);

        private static string BuildRcloneCopyToArguments(string sourcePath, string destinationPath)
        {
            return $"copyto {Quote(sourcePath)} {Quote(destinationPath)}";
        }

        private static (string Executable, string Arguments) WrapScriptExecutionIfNeeded(string executablePath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return (string.Empty, arguments);
            }

            string extension = Path.GetExtension(executablePath);
            if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                string combined = $"-ExecutionPolicy Bypass -File {Quote(executablePath)}";
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    combined += " " + arguments;
                }

                return (DefaultWindowsPowerShellExecutable, combined);
            }

            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                string combined = $"/c \"{executablePath}";
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    combined += " " + arguments;
                }

                combined += "\"";
                return (DefaultCommandShellExecutable, combined);
            }

            return (executablePath, arguments);
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "\"\"";
            }

            return value.StartsWith('"') && value.EndsWith('"') ? value : $"\"{value}\"";
        }

        private static Task RunOnUIAsync(Action action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }
    }
}
