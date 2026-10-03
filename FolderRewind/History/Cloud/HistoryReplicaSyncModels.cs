using FolderRewind.History.Domain;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

public sealed record HistoryReplicaManifest(
    RepresentationId RepresentationId,
    ReplicaId ReplicaId,
    Guid? ArtifactRootId,
    string ObjectKey,
    long Size,
    string StorageSha256);

public sealed record HistoryReplicaVerification(bool Success, long Size, string StorageSha256, string Diagnostic);

public interface IHistoryReplicaTransport
{
    Task UploadAsync(ReplicaId replicaId, string localPath, CancellationToken cancellationToken);
    Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId replicaId, CancellationToken cancellationToken);
    Task CommitManifestOnceAsync(HistoryReplicaManifest manifest, CancellationToken cancellationToken);
    Task DownloadAsync(HistoryReplicaManifest manifest, string stagingPath, CancellationToken cancellationToken);
    Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken cancellationToken);
}

public interface IHistoryReplicaRetirementGuard
{
    Task EnsureRetirementSafeAsync(
        StorageReplica replica,
        bool releaseVersion,
        CancellationToken cancellationToken);
}

public enum HistoryReplicaOperationStatus
{
    Succeeded = 0,
    Failed = 1,
    MetadataCommittedPhysicalOrphan = 2
}

public sealed record HistoryReplicaOperationResult(
    HistoryReplicaOperationStatus Status,
    ReplicaId ReplicaId,
    string Diagnostic);
