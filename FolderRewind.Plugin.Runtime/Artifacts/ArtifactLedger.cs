using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record ArtifactLedgerDocument(
    int SchemaVersion,
    ArtifactGraphRevision Revision,
    IReadOnlyList<ArtifactLedgerEntry> Artifacts,
    IReadOnlyList<ArtifactHistoryRoot> HistoryRoots);

public sealed record ArtifactLedgerEntry(
    ArtifactId ArtifactId,
    ArtifactFormatRef Format,
    int FormatVersion,
    RestoreStrategyId RestoreStrategyId,
    string ConfigId,
    Guid FolderId,
    string HistoryItemId,
    string ContentRelativePath,
    string LogicalSha256,
    long LogicalSize,
    string StorageSha256,
    long StorageSize,
    ArtifactCompleteness Completeness,
    CoreCaptureMode CoreCaptureMode,
    IReadOnlyList<ArtifactId> Dependencies,
    string TransactionId,
    ArtifactAvailability LocalAvailability,
    ArtifactAvailability CloudAvailability);

public sealed record ArtifactHistoryRoot(
    string HistoryItemId,
    string ConfigId,
    Guid FolderId,
    ArtifactId RootArtifactId);

public enum ArtifactAvailability
{
    Missing = 0,
    Available = 1,
    Pending = 2,
    Failed = 3
}

public sealed record StagedArtifactFacts(
    ArtifactId ArtifactId,
    ArtifactStagingHandle Staging,
    string ContentRelativePath,
    string LogicalSha256,
    long LogicalSize,
    string StorageSha256,
    long StorageSize);

public sealed record ArtifactPatchScope(
    PluginId PluginId,
    string ConfigId,
    Guid FolderId,
    IReadOnlyDictionary<string, ArtifactId> ReplaceableHistoryRoots,
    IReadOnlySet<ArtifactId> ReadableArtifacts,
    string TransactionId,
    ArtifactGraphRevision CommittedRevision);
