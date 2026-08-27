using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FolderRewind.History.Legacy;

/// <summary>
/// Commit 8–16 唯一允许把捕获结果写入旧 history.json/backup-runs.json 的桥。
/// Commit 17 切流后停止调用，Commit 20 连同旧 authority 一并删除；Native Capture 不得引用本类型。
/// </summary>
internal static class LegacyHistoryCapturePersistenceAdapter
{
    public static bool PersistRun(BackupConfig config, BackupRunRecord run)
    {
        if (!BackupRunService.Add(run))
        {
            return false;
        }

        BackupRunService.ApplyRetention(config);
        CloudSyncService.QueueConfigurationHistorySyncAfterLocalChange(
            config,
            "configuration backup run completion");
        return true;
    }

    public static HistoryItem AddEntry(
        BackupConfig config,
        ManagedFolder folder,
        string fileName,
        string type,
        string comment,
        string storageFolderName,
        bool isPartial,
        string? createdByRunId,
        string? historyItemId,
        Guid? artifactRootId,
        string? artifactGraphRevision,
        PersistedOperationOutcome outcome,
        IReadOnlyList<OperationDiagnosticRecord>? diagnostics)
        => HistoryService.AddEntry(
            config,
            folder,
            fileName,
            type,
            comment,
            storageFolderName,
            isPartial,
            createdByRunId,
            historyItemId,
            artifactRootId,
            artifactGraphRevision,
            outcome,
            diagnostics);

    public static HistoryItem? GetLatestEntry(BackupConfig config, ManagedFolder folder)
        => HistoryService.GetLatestEntryForFolder(config.Id, folder);

    public static ObservableCollection<HistoryItem> GetEntries(BackupConfig config, ManagedFolder folder)
        => HistoryService.GetHistoryForFolder(config, folder);

    public static void ApplyArtifactRoots(IReadOnlyDictionary<string, Guid> roots, string graphRevision)
        => HistoryService.ApplyArtifactRoots(roots, graphRevision);

    public static void ApplyOperationResult(
        string historyItemId,
        PersistedOperationOutcome outcome,
        IReadOnlyList<OperationDiagnosticRecord> diagnostics)
        => HistoryService.ApplyOperationResult(historyItemId, outcome, diagnostics);
}
