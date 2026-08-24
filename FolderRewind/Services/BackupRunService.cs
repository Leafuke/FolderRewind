using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>
/// 备份运行记录的内存态仓储：懒加载 backup-runs.json（Magic 与 SchemaVersion 不匹配时视为空），
/// 所有变更在锁内执行并立即经 <see cref="PersistLocked"/> 原子落盘；
/// 持久化失败的变更会回滚内存状态（见 Remove）。
/// </summary>
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

    public static bool Export(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        Initialize();
        lock (Gate)
        {
            try
            {
                var document = new BackupRunDocument { Runs = _runs.ToList() };
                AtomicFileService.Write(
                    destinationPath,
                    stream => JsonSerializer.Serialize(
                        stream,
                        document,
                        AppJsonContext.Default.BackupRunDocument));
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError($"Failed to export backup runs: {ex.Message}", nameof(BackupRunService), ex);
                return false;
            }
        }
    }

    public static (bool Success, int ImportedCount, int DuplicateCount) Import(
        string sourcePath,
        bool merge = true,
        string? configId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        try
        {
            var json = File.ReadAllText(sourcePath);
            var document = JsonSerializer.Deserialize(json, AppJsonContext.Default.BackupRunDocument);
            if (document?.Magic != BackupRunDocument.CurrentMagic
                || document.SchemaVersion != BackupRunDocument.CurrentSchemaVersion)
            {
                return (false, 0, 0);
            }

            return Import(document.Runs, merge, configId);
        }
        catch (Exception ex)
        {
            LogService.LogError($"Failed to import backup runs: {ex.Message}", nameof(BackupRunService), ex);
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// 导入运行记录：merge=false 时先清空现有记录；RunId 重复的跳过并计入重复数，
    /// configId 非空时只导入该配置的记录。返回 (成功与否, 导入数, 重复数)。
    /// </summary>
    public static (bool Success, int ImportedCount, int DuplicateCount) Import(
        IEnumerable<BackupRunRecord> runs,
        bool merge = true,
        string? configId = null)
    {
        Initialize();
        var imported = Normalize(runs)
            .Where(run => string.IsNullOrWhiteSpace(configId)
                          || string.Equals(run.ConfigId, configId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        lock (Gate)
        {
            var duplicateCount = 0;
            var importedCount = 0;
            if (!merge)
            {
                _runs.Clear();
            }
            foreach (var run in imported)
            {
                if (_runs.Any(existing => string.Equals(existing.RunId, run.RunId, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicateCount++;
                    continue;
                }
                _runs.Add(run);
                importedCount++;
            }

            return PersistLocked()
                ? (true, importedCount, duplicateCount)
                : (false, 0, duplicateCount);
        }
    }

    /// <summary>
    /// 添加一条运行记录：RunId 已存在时拒绝（返回 false），成功后立即持久化。
    /// </summary>
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

    /// <summary>
    /// 按配置的 KeepCount 裁剪运行记录（Important 运行受保护），返回被移除的记录。
    /// </summary>
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

    /// <summary>
    /// 移除一条运行记录：持久化失败时把记录放回内存（回滚），返回 null 表示未移除。
    /// </summary>
    public static BackupRunRecord? Remove(string runId)
    {
        Initialize();
        lock (Gate)
        {
            var run = _runs.FirstOrDefault(candidate =>
                string.Equals(candidate.RunId, runId, StringComparison.OrdinalIgnoreCase));
            if (run == null)
            {
                return null;
            }
            _runs.Remove(run);
            if (PersistLocked())
            {
                return run;
            }
            _runs.Add(run);
            return null;
        }
    }

    /// <summary>
    /// 整运行还原：逐源查找其文件夹与历史条目并分别调用核心还原，
    /// 由 BackupRunRestoreOrchestrator 汇总各源结果。
    /// </summary>
    public static async Task<BackupRunRestoreResult> RestoreAsync(
        BackupConfig config,
        BackupRunRecord run,
        BackupService.RestoreMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(run);
        return await BackupRunRestoreOrchestrator.RestoreAsync(run, async source =>
        {
            var sourceResult = new BackupRunRestoreSourceResult { FolderPath = source.FolderPath };
            var folder = config.SourceFolders.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, source.FolderPath, StringComparison.OrdinalIgnoreCase));
            var historyItem = HistoryService.TryGetEntryById(source.HistoryItemId);
            if (folder == null || historyItem == null)
            {
                sourceResult.ErrorMessage = folder == null
                    ? "The source is no longer present in this configuration."
                    : "The referenced history item is missing.";
                return sourceResult;
            }

            sourceResult.Success = await BackupService.RestoreBackupAsync(config, folder, historyItem, mode);
            if (!sourceResult.Success)
            {
                sourceResult.ErrorMessage = "The source restore failed; see the local log for details.";
            }
            return sourceResult;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 过滤无效记录并按 RunId 去重（保留首条）。
    /// </summary>
    private static List<BackupRunRecord> Normalize(IEnumerable<BackupRunRecord>? runs)
    {
        return (runs ?? Array.Empty<BackupRunRecord>())
            .Where(run => run != null && !string.IsNullOrWhiteSpace(run.RunId) && !string.IsNullOrWhiteSpace(run.ConfigId))
            .GroupBy(run => run.RunId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// 原子写 backup-runs.json。必须在 Gate 锁内调用（故名 Locked）。
    /// </summary>
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
