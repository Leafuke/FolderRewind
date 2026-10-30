using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

public interface IHistoryMetadataTransport
{
    Task<byte[]?> ReadDescriptorAsync(HistoryConfigId configId, CancellationToken cancellationToken);
    Task CreateDescriptorOnceAsync(HistoryConfigId configId, byte[] canonicalBytes, CancellationToken cancellationToken);
    Task<IReadOnlyList<PackId>> ListPacksAsync(HistoryConfigId configId, CancellationToken cancellationToken);
    Task<byte[]> DownloadPackAsync(HistoryConfigId configId, PackId packId, CancellationToken cancellationToken);
    Task UploadPackOnceAsync(HistoryConfigId configId, PackId packId, byte[] bytes, CancellationToken cancellationToken);
    Task<bool> LegacyHistoryExistsAsync(HistoryConfigId configId, CancellationToken cancellationToken);
}

public interface IHistoryLegacyCloudBridge
{
    Task<bool> TryBridgeAsync(CancellationToken cancellationToken);
}

public enum HistoryMetadataSyncStatus
{
    Succeeded = 0,
    CompatibilityBlocked = 1,
    RemoteRepositoryIncomplete = 2,
    IntegrityConflict = 3,
    Failed = 4
}

public sealed record HistoryMetadataSyncResult(
    HistoryMetadataSyncStatus Status,
    int DownloadedPacks,
    int UploadedPacks,
    ImmutableArray<BranchId> DivergentBranches,
    ImmutableArray<string> BranchNameCollisions,
    string Diagnostic)
{
    public bool Succeeded => Status == HistoryMetadataSyncStatus.Succeeded;
}
