using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Runtime.Artifacts;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Application;

public sealed record HistoryTransferPayloadFile(string RelativePath, string EntryName, long Size, string StorageSha256);
public sealed record HistoryTransferNativePayload(string PayloadType, RepresentationId RepresentationId, System.Guid ArtifactRootId,
    System.Guid SourceId, string ArtifactConfigId, string RootStorageSha256, string EntryName, long Size, string StorageSha256);
public sealed record HistoryTransferNativeObject(string ObjectKey, string EntryName, long Size, string StorageSha256);
public sealed record HistoryTransferProjectMigration(string BackupRootHint,
    ImmutableArray<HistoryReplicaRecoveryHint> Replicas, ImmutableArray<WorkspaceSourceBaseline> Continuation,
    ImmutableArray<HistoryTransferPayloadFile> PayloadFiles, ArtifactLedgerDocument? ArtifactClosure = null,
    ImmutableArray<string> PayloadDirectories = default,
    ImmutableArray<HistoryTransferNativePayload> NativePayloads = default,
    ImmutableArray<HistoryTransferNativeObject> NativeObjects = default);
public sealed record HistoryTransferV3Manifest(string Magic, int FormatVersion, string ApplicationVersion,
    bool IncludesPayloads, ImmutableArray<HistoryTransferProject> Projects, string? ConfigurationSha256,
    string TransferId, System.DateTimeOffset CreatedAtUtc);
public sealed record HistoryMigrationExportProject(HistoryTransferExportProject History, string BackupRoot,
    FolderRewind.History.Representation.IArchiveRepresentationBackend? Archive = null)
{
    public NativeArtifactStore? NativeStore { get; init; }
    public FolderRewind.History.Representation.RepresentationRuntime? Consumers { get; init; }
}
public sealed record HistoryMigrationBundlePreview(HistoryTransferPreview History, bool IncludesPayloads,
    byte[]? ConfigurationUtf8, string TransferId);
