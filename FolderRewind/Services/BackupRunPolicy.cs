using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services;

/// <summary>
/// 历史条目旧版 ID 生成：由"配置|规范化文件夹路径|文件名|UTC 时间戳"拼接后取 SHA-256，
/// 用于旧数据迁移时为无 ID 条目补发稳定标识。
/// </summary>
public static class HistoryItemIdentity
{
    public static string CreateLegacyId(
        string configId,
        string folderPath,
        string fileName,
        DateTime timestamp)
    {
        var normalizedFolder = (folderPath ?? string.Empty).Trim().Replace('\\', '/').ToUpperInvariant();
        var stableTicks = timestamp.Kind == DateTimeKind.Unspecified
            ? timestamp.Ticks
            : timestamp.ToUniversalTime().Ticks;
        var payload = $"{configId?.Trim().ToUpperInvariant()}|{normalizedFolder}|{fileName?.Trim().ToUpperInvariant()}|{stableTicks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

/// <summary>
/// 备份运行与历史条目保留策略（纯逻辑、无 IO）：运行记录的创建与裁剪、
/// 历史条目的按源文件夹保留计算。Important 项与被保留运行引用的条目永远受保护。
/// </summary>
public static class BackupRunPolicy
{
    /// <summary>
    /// 由各源执行结果创建运行记录：没有任何源产生新归档时返回 null（不落盘空运行）；
    /// 存在失败或不可用源时状态记为 Partial、产出记为 SuccessWithWarnings。
    /// </summary>
    public static BackupRunRecord? Create(
        string runId,
        string configId,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        BackupRunTriggerSource triggerSource,
        string comment,
        IEnumerable<BackupRunSourceRecord> sourceResults)
    {
        var sources = sourceResults?.ToList() ?? new List<BackupRunSourceRecord>();
        if (!sources.Any(source => source.Status == BackupRunSourceStatus.NewArchive))
        {
            return null;
        }

        return new BackupRunRecord
        {
            RunId = runId,
            ConfigId = configId,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            TriggerSource = triggerSource,
            Comment = comment ?? string.Empty,
            Status = sources.Any(source => source.Status is BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable)
                ? BackupRunStatus.Partial
                : BackupRunStatus.Completed,
            Outcome = sources.Any(source => source.Status is BackupRunSourceStatus.Failed or BackupRunSourceStatus.Unavailable)
                ? PersistedOperationOutcome.SuccessWithWarnings
                : PersistedOperationOutcome.Success,
            Sources = sources
        };
    }

    /// <summary>
    /// 计算应裁剪的运行记录：按完成时间降序保留最新的 keepCount 个非 Important 运行，
    /// Important 运行永不移除、也不占用保留名额。
    /// </summary>
    public static IReadOnlyList<BackupRunRecord> SelectRunsToRemove(
        IEnumerable<BackupRunRecord> runs,
        int keepCount)
    {
        if (keepCount <= 0)
        {
            return Array.Empty<BackupRunRecord>();
        }

        var ordered = runs
            .OrderByDescending(run => run.CompletedAtUtc)
            .ThenByDescending(run => run.RunId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var retainedRegularIds = ordered
            .Where(run => !run.IsImportant)
            .Take(keepCount)
            .Select(run => run.RunId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ordered
            .Where(run => !run.IsImportant && !retainedRegularIds.Contains(run.RunId))
            .ToList();
    }

    /// <summary>
    /// 判断历史条目是否被任一运行的源记录引用（引用中的条目不可删除）。
    /// </summary>
    public static bool IsHistoryItemReferenced(
        string historyItemId,
        IEnumerable<BackupRunRecord> runs) =>
        !string.IsNullOrWhiteSpace(historyItemId)
        && runs.Any(run => run.Sources.Any(source =>
            string.Equals(source.HistoryItemId, historyItemId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// 计算应删除的历史条目 ID：按源文件夹分组，各组保留最新的 keepCount 个非 Important 条目；
    /// Important 条目与被保留运行引用的条目跳过。结果按时间升序返回（先删最旧）。
    /// </summary>
    public static IReadOnlyList<string> SelectHistoryItemIdsToRemove(
        IEnumerable<BackupRetentionHistoryRecord> historyItems,
        IEnumerable<BackupRunRecord> retainedRuns,
        int keepCount)
    {
        if (keepCount <= 0)
        {
            return Array.Empty<string>();
        }

        var referencedIds = (retainedRuns ?? Array.Empty<BackupRunRecord>())
            .SelectMany(run => run.Sources)
            .Select(source => source.HistoryItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removable = new List<BackupRetentionHistoryRecord>();
        foreach (var sourceGroup in (historyItems ?? Array.Empty<BackupRetentionHistoryRecord>())
                     .GroupBy(item => NormalizeSourcePath(item.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            var retainedRegularIds = sourceGroup
                .Where(item => !item.IsImportant)
                .OrderByDescending(item => item.Timestamp)
                .ThenByDescending(item => item.HistoryItemId, StringComparer.OrdinalIgnoreCase)
                .Take(keepCount)
                .Select(item => item.HistoryItemId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            removable.AddRange(sourceGroup.Where(item =>
                !item.IsImportant
                && !retainedRegularIds.Contains(item.HistoryItemId)
                && !referencedIds.Contains(item.HistoryItemId)));
        }
        return removable
            .OrderBy(item => item.Timestamp)
            .ThenBy(item => item.HistoryItemId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.HistoryItemId)
            .ToList();
    }

    /// <summary>
    /// 云端同步合并：用本地运行整体替换远端列表中同一配置的运行，再按 RunId 去重
    /// （同 ID 时本地条目排在拼接末尾、后者胜出）。
    /// </summary>
    public static IReadOnlyList<BackupRunRecord> ReplaceConfigurationRuns(
        IEnumerable<BackupRunRecord> remoteRuns,
        IEnumerable<BackupRunRecord> localRuns,
        string configId)
    {
        return (remoteRuns ?? Array.Empty<BackupRunRecord>())
            .Where(run => !string.Equals(run.ConfigId, configId, StringComparison.OrdinalIgnoreCase))
            .Concat(localRuns ?? Array.Empty<BackupRunRecord>())
            .GroupBy(run => run.RunId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }

    private static string NormalizeSourcePath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path ?? string.Empty)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return (path ?? string.Empty).Trim().TrimEnd('\\', '/');
        }
    }
}
