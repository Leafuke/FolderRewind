using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FolderRewind.History.Legacy;

/// <summary>
/// Commit 8–16 唯一允许把捕获结果写入旧 history.json/backup-runs.json 的桥。
/// Commit 17 切流后停止调用，Commit 20 连同旧 authority 一并删除；Native Capture 不得引用本类型。
/// </summary>
internal static class LegacyHistoryCapturePersistenceAdapter
{
    public static bool PersistRun(BackupConfig config, BackupRunRecord run)
        => throw new InvalidOperationException("Legacy backup-runs.json authority is read-only after Native History cutover.");

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
        => new()
        {
            Id = historyItemId ?? Guid.NewGuid().ToString("N"),
            CreatedByRunId = createdByRunId ?? string.Empty,
            ConfigId = config.Id,
            FolderId = Guid.TryParse(folder.Id, out var sourceId) ? sourceId : null,
            FolderPath = folder.Path,
            FolderName = storageFolderName,
            FileName = fileName,
            Timestamp = DateTime.Now,
            BackupType = type,
            Comment = comment,
            IsPartialBackup = isPartial,
            ArtifactRootId = artifactRootId,
            ArtifactGraphRevision = artifactGraphRevision ?? string.Empty,
            Outcome = outcome,
            Diagnostics = diagnostics?.ToList() ?? []
        };

    public static HistoryItem? GetLatestEntry(BackupConfig config, ManagedFolder folder)
        => null;

    public static ObservableCollection<HistoryItem> GetEntries(BackupConfig config, ManagedFolder folder)
        => [];

    public static void ApplyArtifactRoots(IReadOnlyDictionary<string, Guid> roots, string graphRevision)
    {
        // Legacy HistoryItem bindings are read-only after the one-way cutover.
    }

    public static void ApplyOperationResult(
        string historyItemId,
        PersistedOperationOutcome outcome,
        IReadOnlyList<OperationDiagnosticRecord> diagnostics)
    {
        // Native diagnostics are committed with Version/Run facts; never mutate history.json.
    }
}
