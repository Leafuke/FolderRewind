using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FolderRewind.History.Migration;

public sealed record LegacyMigrationSourceSnapshot(
    SourceId SourceId,
    string OriginalPath,
    string DisplayName,
    string ArchiveDirectory);

public sealed record LegacyHistoryEntrySnapshot(
    SourceId SourceId,
    string OriginalFolderPath,
    string FolderName,
    string FileName,
    DateTime Timestamp,
    string BackupType,
    string Comment,
    bool IsImportant,
    bool IsPartialBackup,
    bool IsCloudArchived,
    string LegacyCloudRelativeLocator);

public sealed record LegacySmartRecordSnapshot(
    SourceId SourceId,
    string ArchiveFileName,
    string PreviousBackupFileName,
    string BasedOnFullBackup);

public sealed record LegacyHistoryMigrationInput
{
    public LegacyHistoryMigrationInput(
        string configDirectory,
        HistoryConfigId configId,
        IEnumerable<LegacyMigrationSourceSnapshot> sources,
        IEnumerable<LegacyHistoryEntrySnapshot> entries,
        IEnumerable<LegacySmartRecordSnapshot>? smartRecords = null)
    {
        ConfigDirectory = System.IO.Path.GetFullPath(configDirectory);
        ConfigId = configId;
        Sources = [.. sources];
        Entries = [.. entries];
        SmartRecords = smartRecords is null ? [] : [.. smartRecords];
    }

    public string ConfigDirectory { get; }
    public HistoryConfigId ConfigId { get; }
    public ImmutableArray<LegacyMigrationSourceSnapshot> Sources { get; }
    public ImmutableArray<LegacyHistoryEntrySnapshot> Entries { get; }
    public ImmutableArray<LegacySmartRecordSnapshot> SmartRecords { get; }
}

public sealed record LegacyHistoryMigrationBuild(
    ImmutableArray<HistoryCommitPack> Packs,
    LocalReplicaCatalog LocalReplicaCatalog,
    HistoryWorkspace Workspace,
    CheckpointId BootstrapCheckpointId,
    BranchUpdateId BootstrapBranchUpdateId);

public enum LegacyHistoryMigrationStatus
{
    MigratedAndBound = 0,
    ExistingRepositoryBound = 1,
    BindingPersistenceFailed = 2,
    Failed = 3
}

public sealed record LegacyHistoryMigrationResult(
    LegacyHistoryMigrationStatus Status,
    string Diagnostic,
    FileHistoryRepository? Repository)
{
    public bool IsReady => Status is LegacyHistoryMigrationStatus.MigratedAndBound
        or LegacyHistoryMigrationStatus.ExistingRepositoryBound;
}
