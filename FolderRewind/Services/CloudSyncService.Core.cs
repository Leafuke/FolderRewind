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
        private const int MaxLogLength = 4096;
        private const string InternalCloudStateDirectoryName = "_folderrewind";
        private const string ActiveHistoryManifestFileName = "active-history.json";
        // 故意串行执行云命令，避免并发 rclone 时任务状态、日志和元数据写入互相打架。
        private static readonly SemaphoreSlim CommandSemaphore = new(1, 1);

        private sealed class CloudCommandContext
        {
            public required string ConfigName { get; init; }
            public required string ConfigId { get; init; }
            public required string FolderName { get; init; }
            public required string SourcePath { get; init; }
            public required string DestinationPath { get; init; }
            public required string BackupSubDir { get; init; }
            public required string MetadataDir { get; init; }
            public required string ArchiveFileName { get; init; }
            public required string ArchiveFilePath { get; init; }
            public required string BackupMode { get; init; }
            public required string Comment { get; init; }
            public required string Timestamp { get; init; }
        }

        private sealed class ResolvedCommand
        {
            public required string ExecutablePath { get; init; }
            public required string Arguments { get; init; }
            public required string WorkingDirectory { get; init; }
            public required string Preview { get; init; }
        }

        private sealed class HistoryCloudPaths
        {
            public required string FolderName { get; init; }
            public required string ArchiveFilePath { get; init; }
            public required string ArchiveRemotePath { get; init; }
            public required string MetadataDir { get; init; }
            public required string MetadataStateFilePath { get; init; }
            public required string MetadataRecordFilePath { get; init; }
            public required string MetadataStateRemotePath { get; init; }
            public required string MetadataRecordRemotePath { get; init; }
        }

        private static void SerializeToFile<T>(string path, T value, JsonTypeInfo<T> typeInfo)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, value, typeInfo);
        }

        public static string VariablesHelpText => string.Join(Environment.NewLine, new[]
        {
            "{ArchiveFilePath}",
            "{ArchiveFileName}",
            "{BackupSubDir}",
            "{MetadataDir}",
            "{ConfigName}",
            "{ConfigId}",
            "{FolderName}",
            "{SourcePath}",
            "{DestinationPath}",
            "{BackupMode}",
            "{Comment}",
            "{Timestamp}",
            "{RemoteBasePath}"
        });

        public static void ApplyRecommendedTemplate(CloudSettings? settings)
        {
            if (settings == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.ExecutablePath))
            {
                settings.ExecutablePath = DefaultRcloneExecutable;
            }

            if (string.IsNullOrWhiteSpace(settings.RemoteBasePath))
            {
                settings.RemoteBasePath = "remote:FolderRewind";
            }

            settings.ArgumentsTemplate = GetRecommendedArgumentsTemplate(settings.TemplateKind);
        }

        public static string BuildPreview(BackupConfig? config)
        {
            if (config?.Cloud == null)
            {
                return string.Empty;
            }

            var context = BuildSampleContext(config);
            var resolved = ResolveCommand(config.Cloud, context);
            return resolved.Preview;
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
            return ResolveRcloneExecutable(config?.Cloud);
        }

        public static string GetSuggestedRemoteBasePath()
        {
            string globalDefault = ConfigService.CurrentConfig?.GlobalSettings?.DefaultCloudRemoteBasePath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(globalDefault))
            {
                return globalDefault;
            }

            var configured = ConfigService.CurrentConfig?.BackupConfigs?
                .Select(cfg => cfg?.Cloud?.RemoteBasePath?.Trim())
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

            return string.IsNullOrWhiteSpace(configured)
                ? "remote:FolderRewind"
                : configured!;
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


        private static bool TryBuildHistoryCloudPaths(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem item,
            out HistoryCloudPaths paths,
            out string errorMessage)
        {
            paths = null!;
            errorMessage = string.Empty;

            string archiveFilePath = HistoryService.GetBackupFilePath(config, folder, item) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(archiveFilePath))
            {
                errorMessage = I18n.GetString("History_ViewFile_PathEmpty");
                return false;
            }

            string folderName = string.IsNullOrWhiteSpace(item.FolderName)
                ? (folder.DisplayName ?? string.Empty)
                : item.FolderName;
            string destinationPath = config.DestinationPath ?? string.Empty;
            string metadataDir = Path.Combine(destinationPath, "_metadata", folderName);
            if (BackupStoragePathService.TryResolveBackupStoragePaths(
                destinationPath,
                folderName,
                folder.Path,
                out _,
                out _,
                out var resolvedMetadataDir))
            {
                metadataDir = resolvedMetadataDir;
            }

            if (!BackupMetadataStoreService.TryGetStateFilePath(metadataDir, out var stateFilePath))
            {
                stateFilePath = Path.Combine(metadataDir, "state.json");
            }

            if (!BackupMetadataStoreService.TryGetRecordFilePath(metadataDir, item.FileName, out var recordFilePath))
            {
                recordFilePath = Path.Combine(metadataDir, "records", item.FileName + ".json");
            }

            var defaultRemotePaths = BuildDefaultRemotePaths(config.Name ?? string.Empty, folderName, item.FileName, config.Cloud?.RemoteBasePath);
            // 历史项若已有远端路径则优先复用，避免远端目录结构调整后被“默认路径”覆盖。
            paths = new HistoryCloudPaths
            {
                FolderName = folderName,
                ArchiveFilePath = archiveFilePath,
                ArchiveRemotePath = string.IsNullOrWhiteSpace(item.CloudArchiveRemotePath) ? defaultRemotePaths.ArchiveRemotePath : item.CloudArchiveRemotePath,
                MetadataDir = metadataDir,
                MetadataStateFilePath = stateFilePath,
                MetadataRecordFilePath = recordFilePath,
                MetadataStateRemotePath = string.IsNullOrWhiteSpace(item.CloudMetadataStateRemotePath) ? defaultRemotePaths.MetadataStateRemotePath : item.CloudMetadataStateRemotePath,
                MetadataRecordRemotePath = string.IsNullOrWhiteSpace(item.CloudMetadataRecordRemotePath) ? defaultRemotePaths.MetadataRecordRemotePath : item.CloudMetadataRecordRemotePath
            };

            return true;
        }

        private static (string ArchiveRemotePath, string MetadataRecordRemotePath, string MetadataStateRemotePath) BuildDefaultRemotePaths(
            string configName,
            string folderName,
            string archiveFileName,
            string? remoteBasePath)
        {
            string normalizedRemoteBasePath = string.IsNullOrWhiteSpace(remoteBasePath)
                ? "remote:FolderRewind"
                : remoteBasePath.Trim().TrimEnd('/');

            string remoteFolderRoot = AppendRemotePath(normalizedRemoteBasePath, configName, folderName);
            return (
                AppendRemotePath(remoteFolderRoot, archiveFileName),
                AppendRemotePath(remoteFolderRoot, "_metadata", "records", archiveFileName + ".json"),
                AppendRemotePath(remoteFolderRoot, "_metadata", "state.json"));
        }

        private static string AppendRemotePath(string root, params string[] segments)
        {
            // 远端路径统一使用 '/'，不要混入本地路径分隔符。
            string result = root?.Trim().TrimEnd('/') ?? string.Empty;
            foreach (var rawSegment in segments)
            {
                var segment = (rawSegment ?? string.Empty).Trim().Trim('/');
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                result = string.IsNullOrWhiteSpace(result)
                    ? segment
                    : result + "/" + segment;
            }

            return result;
        }

        private static string BuildRcloneCopyToArguments(string sourcePath, string destinationPath)
        {
            return $"copyto {Quote(sourcePath)} {Quote(destinationPath)}";
        }

        private static string BuildRcloneCopyArguments(string sourcePath, string destinationPath, params string[] excludePatterns)
        {
            var builder = new StringBuilder();
            builder.Append("copy ");
            builder.Append(Quote(sourcePath));
            builder.Append(' ');
            builder.Append(Quote(destinationPath));

            foreach (var pattern in excludePatterns ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                builder.Append(" --exclude ");
                builder.Append(Quote(pattern.Trim()));
            }

            return builder.ToString();
        }

        private static string BuildRcloneListFileArguments(string remotePath)
        {
            return $"lsf {Quote(remotePath)} --files-only --max-depth 1";
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

        private static Dictionary<string, string> BuildVariables(CloudCommandContext context, string? remoteBasePath)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ArchiveFilePath"] = context.ArchiveFilePath,
                ["ArchiveFileName"] = context.ArchiveFileName,
                ["BackupSubDir"] = context.BackupSubDir,
                ["MetadataDir"] = context.MetadataDir,
                ["ConfigName"] = context.ConfigName,
                ["ConfigId"] = context.ConfigId,
                ["FolderName"] = context.FolderName,
                ["SourcePath"] = context.SourcePath,
                ["DestinationPath"] = context.DestinationPath,
                ["BackupMode"] = context.BackupMode,
                ["Comment"] = context.Comment,
                ["Timestamp"] = context.Timestamp,
                ["RemoteBasePath"] = string.IsNullOrWhiteSpace(remoteBasePath) ? "remote:FolderRewind" : remoteBasePath.Trim().TrimEnd('/')
            };
        }

        private static string ReplaceVariables(string input, IReadOnlyDictionary<string, string> variables)
        {
            if (string.IsNullOrWhiteSpace(input) || variables.Count == 0)
            {
                return input ?? string.Empty;
            }

            string result = input;
            foreach (var pair in variables)
            {
                result = Regex.Replace(
                    result,
                    Regex.Escape("{" + pair.Key + "}"),
                    _ => pair.Value ?? string.Empty,
                    RegexOptions.IgnoreCase);
            }

            return result;
        }

        private static string GetRecommendedArgumentsTemplate(CloudTemplateKind templateKind)
        {
            return templateKind switch
            {
                CloudTemplateKind.UploadBackupDirectory => "copy \"{BackupSubDir}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}\"",
                CloudTemplateKind.Custom => string.Empty,
                _ => "copyto \"{ArchiveFilePath}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}/{ArchiveFileName}\""
            };
        }

        private static Task RunOnUIAsync(Action action)
        {
            return UiDispatcherService.RunOnUiAsync(action);
        }
    }
}
