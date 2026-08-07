using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static class BackupRunService
{
    private const string FileName = "backup-runs.json";
    private static readonly object Gate = new();
    private static bool _initialized;
    private static List<BackupRunRecord> _runs = new();

    private static string StoragePath => Path.Combine(ConfigService.ConfigDirectory, FileName);

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            if (File.Exists(StoragePath))
            {
                try
                {
                    var json = File.ReadAllText(StoragePath);
                    var document = JsonSerializer.Deserialize(
                        json,
                        AppJsonContext.Default.BackupRunDocument);
                    if (document?.Magic == BackupRunDocument.CurrentMagic
                        && document.SchemaVersion == BackupRunDocument.CurrentSchemaVersion)
                    {
                        _runs = Normalize(document.Runs);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarning($"Failed to load backup-runs.json: {ex.Message}", nameof(BackupRunService));
                }
            }

            _initialized = true;
        }
    }

    public static IReadOnlyList<BackupRunRecord> GetRuns(string configId)
    {
        Initialize();
        lock (Gate)
        {
            return _runs
                .Where(run => string.Equals(run.ConfigId, configId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(run => run.CompletedAtUtc)
                .ToList();
        }
    }

    public static IReadOnlyList<BackupRunRecord> GetAllRuns()
    {
        Initialize();
        lock (Gate)
        {
            return _runs.ToList();
        }
    }

    public static bool Add(BackupRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Initialize();
        lock (Gate)
        {
            if (_runs.Any(existing => string.Equals(existing.RunId, run.RunId, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            _runs.Add(run);
            return PersistLocked();
        }
    }

    public static IReadOnlyList<BackupRunRecord> ApplyRetention(BackupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Initialize();
        lock (Gate)
        {
            var removable = BackupRunPolicy.SelectRunsToRemove(
                _runs.Where(run => string.Equals(run.ConfigId, config.Id, StringComparison.OrdinalIgnoreCase)),
                config.Archive.KeepCount);
            if (removable.Count == 0)
            {
                return removable;
            }

            var ids = removable.Select(run => run.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _runs.RemoveAll(run => ids.Contains(run.RunId));
            PersistLocked();
            return removable;
        }
    }

    public static bool IsHistoryItemReferenced(string historyItemId)
    {
        Initialize();
        lock (Gate)
        {
            return BackupRunPolicy.IsHistoryItemReferenced(historyItemId, _runs);
        }
    }

    public static bool SetImportant(string runId, bool isImportant)
    {
        Initialize();
        lock (Gate)
        {
            var run = _runs.FirstOrDefault(candidate =>
                string.Equals(candidate.RunId, runId, StringComparison.OrdinalIgnoreCase));
            if (run == null)
            {
                return false;
            }
            run.IsImportant = isImportant;
            return PersistLocked();
        }
    }

    public static bool UpdateComment(string runId, string comment)
    {
        Initialize();
        lock (Gate)
        {
            var run = _runs.FirstOrDefault(candidate =>
                string.Equals(candidate.RunId, runId, StringComparison.OrdinalIgnoreCase));
            if (run == null)
            {
                return false;
            }
            run.Comment = comment ?? string.Empty;
            return PersistLocked();
        }
    }

    public static async Task<BackupRunRestoreResult> RestoreAsync(
        BackupConfig config,
        BackupRunRecord run,
        BackupService.RestoreMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(run);
        var result = new BackupRunRestoreResult { RunId = run.RunId };
        foreach (var source in run.Sources)
        {
            var sourceResult = new BackupRunRestoreSourceResult { FolderPath = source.FolderPath };
            result.Sources.Add(sourceResult);
            if (source.Status is BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable
                || string.IsNullOrWhiteSpace(source.HistoryItemId))
            {
                sourceResult.ErrorMessage = string.IsNullOrWhiteSpace(source.ErrorMessage)
                    ? "No recoverable archive reference is available for this source."
                    : source.ErrorMessage;
                continue;
            }

            var folder = config.SourceFolders.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, source.FolderPath, StringComparison.OrdinalIgnoreCase));
            var historyItem = HistoryService.TryGetEntryById(source.HistoryItemId);
            if (folder == null || historyItem == null)
            {
                sourceResult.ErrorMessage = folder == null
                    ? "The source is no longer present in this configuration."
                    : "The referenced history item is missing.";
                continue;
            }

            try
            {
                sourceResult.Success = await BackupService.RestoreBackupAsync(config, folder, historyItem, mode);
                if (!sourceResult.Success)
                {
                    sourceResult.ErrorMessage = "The source restore failed; see the local log for details.";
                }
            }
            catch (Exception ex)
            {
                sourceResult.ErrorMessage = ex.Message;
            }
        }

        return result;
    }

    private static List<BackupRunRecord> Normalize(IEnumerable<BackupRunRecord>? runs)
    {
        return (runs ?? Array.Empty<BackupRunRecord>())
            .Where(run => run != null && !string.IsNullOrWhiteSpace(run.RunId) && !string.IsNullOrWhiteSpace(run.ConfigId))
            .GroupBy(run => run.RunId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool PersistLocked()
    {
        try
        {
            var document = new BackupRunDocument { Runs = _runs.ToList() };
            AtomicFileService.Write(
                StoragePath,
                stream => JsonSerializer.Serialize(
                    stream,
                    document,
                    AppJsonContext.Default.BackupRunDocument));
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogError($"Failed to save backup-runs.json: {ex.Message}", nameof(BackupRunService), ex);
            return false;
        }
    }
}
