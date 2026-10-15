using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Runtime.Artifacts;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Application;

public sealed record HistoryTransferPayloadFile(string RelativePath, string EntryName, long Size, string StorageSha256);
public sealed record HistoryTransferProjectMigration(string BackupRootHint,
    ImmutableArray<HistoryReplicaRecoveryHint> Replicas, ImmutableArray<WorkspaceSourceBaseline> Continuation,
    ImmutableArray<HistoryTransferPayloadFile> PayloadFiles, ArtifactLedgerDocument? ArtifactClosure = null,
    ImmutableArray<string> PayloadDirectories = default);
public sealed record HistoryTransferV3Manifest(string Magic, int FormatVersion, string ApplicationVersion,
    bool IncludesPayloads, ImmutableArray<HistoryTransferProject> Projects, string? ConfigurationSha256,
    string TransferId, System.DateTimeOffset CreatedAtUtc);
public sealed record HistoryMigrationExportProject(HistoryTransferExportProject History, string BackupRoot,
    FolderRewind.History.Representation.IArchiveRepresentationBackend? Archive = null);
public sealed record HistoryMigrationBundlePreview(HistoryTransferPreview History, bool IncludesPayloads,
    byte[]? ConfigurationUtf8, string TransferId);
