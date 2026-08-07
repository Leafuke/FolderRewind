using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class CloudSyncService
{
    private const string BackupRunsFileName = "backup-runs.json";

    private static async Task<(bool Success, int ImportedCount)> ImportConfigurationBackupRunsAsync(
        BackupConfig config,
        IReadOnlyList<HistoryItem> mappedHistoryItems)
    {
        return await ImportBackupRunsFromCloudOptionalAsync(
            config.Cloud?.RemoteBasePath ?? string.Empty,
            config.Cloud ?? new CloudSettings(),
            merge: true,
            config.Id,
            mappedHistoryItems).ConfigureAwait(false);
    }

    private static async Task<(bool Success, int ImportedCount)> ImportBackupRunsFromCloudOptionalAsync(
        string remoteBasePath,
        CloudSettings settings,
        bool merge,
        string? configId = null,
        IReadOnlyList<HistoryItem>? mappedHistoryItems = null)
    {
        string remotePath = AppendRemotePath(remoteBasePath, BackupRunsFileName);
        if (!TryResolveSharedRcloneRuntime(settings, out var executablePath, out var workingDirectory, out _))
        {
            return (false, 0);
        }
        if (!await RemoteFileExistsAsync(executablePath, workingDirectory, settings, remotePath).ConfigureAwait(false))
        {
            // A missing runs document is the expected legacy state.
            return (true, 0);
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"FolderRewind_runs_import_{Guid.NewGuid():N}.json");
        await CommandSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var command = CreateDirectCommand(
                executablePath,
                workingDirectory,
                BuildRcloneCopyToArguments(remotePath, tempPath));
            var result = await RunSilentCommandAsync(
                command,
                Math.Clamp(settings.TimeoutSeconds, 10, MaxTimeoutSeconds)).ConfigureAwait(false);
            if (!result.Success || !File.Exists(tempPath))
            {
                return (false, 0);
            }

            var json = await File.ReadAllTextAsync(tempPath).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupRunDocument);
            if (document?.Magic != BackupRunDocument.CurrentMagic
                || document.SchemaVersion != BackupRunDocument.CurrentSchemaVersion)
            {
                return (false, 0);
            }

            IEnumerable<BackupRunRecord> runs = document.Runs;
            if (!string.IsNullOrWhiteSpace(configId))
            {
                var historyMap = (mappedHistoryItems ?? Array.Empty<HistoryItem>())
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                runs = document.Runs
                    .Where(run => string.Equals(run.ConfigId, configId, StringComparison.OrdinalIgnoreCase)
                                  || run.Sources.Any(source => historyMap.ContainsKey(source.HistoryItemId)))
                    .Select(run => MapRunToLocalConfiguration(run, configId, historyMap))
                    .ToList();
            }

            var import = BackupRunService.Import(runs, merge);
            return (import.Success, import.ImportedCount);
        }
        finally
        {
            CommandSemaphore.Release();
            TryDeleteTempFile(tempPath);
        }
    }

    private static BackupRunRecord MapRunToLocalConfiguration(
        BackupRunRecord source,
        string configId,
        IReadOnlyDictionary<string, HistoryItem> historyMap)
    {
        var mappedSources = source.Sources.Select(item =>
        {
            historyMap.TryGetValue(item.HistoryItemId, out var historyItem);
            return new BackupRunSourceRecord
            {
                FolderPath = historyItem?.FolderPath ?? item.FolderPath,
                FolderName = historyItem?.FolderName ?? item.FolderName,
                Status = item.Status,
                HistoryItemId = item.HistoryItemId,
                ArchiveFileName = historyItem?.FileName ?? item.ArchiveFileName,
                ErrorMessage = item.ErrorMessage
            };
        }).ToList();
        return new BackupRunRecord
        {
            RunId = source.RunId,
            ConfigId = configId,
            StartedAtUtc = source.StartedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            TriggerSource = source.TriggerSource,
            Comment = source.Comment,
            IsImportant = source.IsImportant,
            Status = source.Status,
            Sources = mappedSources
        };
    }

    private static async Task<(bool Success, string Message)> ExportBackupRunsToCloudAsync(
        string remoteBasePath)
    {
        return await ExportJsonToCloudAsync(
            AppendRemotePath(remoteBasePath, BackupRunsFileName),
            BackupRunsFileName,
            I18n.GetString("CloudSync_Notification_HistoryExportSucceeded"),
            I18n.GetString("CloudSync_Notification_HistoryExportFailed"),
            BackupRunService.Export).ConfigureAwait(false);
    }

    private static async Task<List<BackupRunRecord>> DownloadRemoteBackupRunsOptionalAsync(
        string executablePath,
        string workingDirectory,
        CloudSettings settings,
        string remotePath,
        BackupTask task)
    {
        if (!await RemoteFileExistsAsync(executablePath, workingDirectory, settings, remotePath).ConfigureAwait(false))
        {
            return new List<BackupRunRecord>();
        }

        string tempPath = Path.Combine(Path.GetTempPath(), $"FolderRewind_runs_optional_{Guid.NewGuid():N}.json");
        try
        {
            var result = await ExecuteCommandWithRetryAsync(
                task,
                settings,
                CreateDirectCommand(executablePath, workingDirectory, BuildRcloneCopyToArguments(remotePath, tempPath)),
                I18n.GetString("CloudSync_Task_DownloadingConfigurationHistory"),
                BackupRunsFileName).ConfigureAwait(false);
            if (!result.Success || !File.Exists(tempPath))
            {
                throw new IOException($"Failed to download {BackupRunsFileName}: {result.ErrorMessage}");
            }

            var document = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(tempPath).ConfigureAwait(false),
                AppJsonContext.Default.BackupRunDocument);
            if (document?.Magic != BackupRunDocument.CurrentMagic
                || document.SchemaVersion != BackupRunDocument.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"Remote {BackupRunsFileName} has an unsupported format.");
            }
            return document.Runs;
        }
        catch (Exception ex)
        {
            LogService.LogWarning($"Failed to download remote backup runs: {ex.Message}", nameof(CloudSyncService));
            throw;
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static void SerializeBackupRuns(string path, IEnumerable<BackupRunRecord> runs)
    {
        SerializeToFile(
            path,
            new BackupRunDocument { Runs = runs.ToList() },
            AppJsonContext.Default.BackupRunDocument);
    }
}
