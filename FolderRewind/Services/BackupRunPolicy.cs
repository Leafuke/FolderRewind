using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services;

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

public static class BackupRunPolicy
{
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

    public static bool IsHistoryItemReferenced(
        string historyItemId,
        IEnumerable<BackupRunRecord> runs) =>
        !string.IsNullOrWhiteSpace(historyItemId)
        && runs.Any(run => run.Sources.Any(source =>
            string.Equals(source.HistoryItemId, historyItemId, StringComparison.OrdinalIgnoreCase)));

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
