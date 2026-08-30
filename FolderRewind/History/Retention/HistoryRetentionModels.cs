using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

[Flags]
public enum HistoryProtectionReason
{
    None = 0,
    KeepCount = 1,
    BranchTip = 2,
    Pin = 4,
    Workspace = 8,
    ActiveOperation = 16,
    SafetySnapshot = 32
}

public sealed record HistoryRetentionOperationRoots
{
    public HistoryRetentionOperationRoots(
        IEnumerable<HistoryRetentionOperationVersion>? versions = null,
        IEnumerable<RepresentationId>? representationIds = null)
    {
        Versions = versions is null ? [] : [.. versions];
        RepresentationIds = representationIds is null ? [] : [.. representationIds];
    }

    public ImmutableArray<HistoryRetentionOperationVersion> Versions { get; }
    public ImmutableArray<RepresentationId> RepresentationIds { get; }

    public static HistoryRetentionOperationRoots Empty { get; } = new();
}

public sealed record HistoryRetentionOperationVersion(
    VersionId VersionId,
    MaterializationFidelity RequiredFidelity);

public sealed record HistoryRetentionRequest
{
    public HistoryRetentionRequest(
        int keepCount,
        HistoryRetentionOperationRoots activeOperations,
        bool allowPostMigrationCleanup = false)
    {
        if (keepCount < 0) throw new ArgumentOutOfRangeException(nameof(keepCount));
        KeepCount = keepCount;
        ActiveOperations = activeOperations ?? throw new ArgumentNullException(nameof(activeOperations));
        AllowPostMigrationCleanup = allowPostMigrationCleanup;
    }

    public int KeepCount { get; }
    public HistoryRetentionOperationRoots ActiveOperations { get; }
    public bool AllowPostMigrationCleanup { get; }
}

public sealed record HistoryProtectedCheckpoint(
    CheckpointId CheckpointId,
    HistoryProtectionReason Reasons);

public sealed record HistoryProtectedVersion(
    VersionId VersionId,
    HistoryProtectionReason Reasons,
    MaterializationFidelity RequiredFidelity);

public sealed record HistoryRepresentationClosure(
    VersionId VersionId,
    RepresentationId SelectedRepresentationId,
    ImmutableArray<RepresentationId> DependencyFirstRepresentationIds,
    MaterializationFidelity Fidelity,
    bool ExplicitlyReleased,
    long? EstimatedProtectedBytes);

public sealed record HistoryCompactionPlan(
    VersionId VersionId,
    SourceId SourceId,
    RepresentationId SelectedRepresentationId,
    RepresentationId ProposedReplacementRepresentationId,
    ImmutableArray<RepresentationId> ReplacedClosure,
    long EstimatedReclaimableBytes);

public sealed record HistoryLocalPayloadDeletion(
    LocalReplicaId LocalReplicaId,
    RepresentationId RepresentationId,
    string ResolvedPath,
    long EstimatedBytes,
    bool RequiresCompaction);

public sealed record HistoryRetentionPlan(
    HistoryTransactionId PlanId,
    HistoryRetentionRequest Request,
    string StateFingerprint,
    long ExpectedCatalogRevision,
    ImmutableArray<HistoryProtectedCheckpoint> ProtectedCheckpoints,
    ImmutableArray<HistoryProtectedVersion> ProtectedVersions,
    ImmutableArray<HistoryRepresentationClosure> ProtectedClosures,
    ImmutableArray<RepresentationId> ProtectedOperationRepresentations,
    ImmutableArray<Guid> ProtectedArtifactRootIds,
    ImmutableArray<HistoryCompactionPlan> Compactions,
    ImmutableArray<HistoryLocalPayloadDeletion> LocalPayloadDeletions,
    long EstimatedBytesReleased,
    ImmutableArray<string> Blockers)
{
    public bool CanExecute => Blockers.IsEmpty;
    public int SharedReplicaRetirementCount => 0;
    public int ReleasedFactCount => 0;
}

public enum HistoryRetentionExecutionStatus
{
    Succeeded = 0,
    Blocked = 1,
    StalePlan = 2,
    Failed = 3,
    OrphanedLocalBytes = 4
}

public sealed record HistoryRetentionExecutionResult(
    HistoryRetentionExecutionStatus Status,
    string Diagnostic,
    ImmutableArray<RepresentationId> ReplacementRepresentationIds,
    ImmutableArray<LocalReplicaId> RemovedLocalRegistrations,
    long DeletedBytes)
{
    public bool Succeeded => Status == HistoryRetentionExecutionStatus.Succeeded;
}

public sealed record HistoryLocalPayloadInspection(
    bool CanRemoveRegistration,
    bool PayloadFileExists,
    string ResolvedPath,
    long Size,
    string Diagnostic);

public interface IHistoryLocalPayloadStore
{
    ValueTask<HistoryLocalPayloadInspection> InspectAsync(
        LocalReplicaCatalogEntry entry,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(string resolvedPath, CancellationToken cancellationToken);
}

public sealed class FileSystemHistoryLocalPayloadStore : IHistoryLocalPayloadStore
{
    private readonly IReadOnlyDictionary<string, string> _stableRoots;

    public FileSystemHistoryLocalPayloadStore(IReadOnlyDictionary<string, string>? stableRoots = null)
        => _stableRoots = stableRoots ?? ImmutableDictionary<string, string>.Empty;

    public ValueTask<HistoryLocalPayloadInspection> InspectAsync(
        LocalReplicaCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = entry.Locator.Resolve(_stableRoots);
            if (File.Exists(path))
                return ValueTask.FromResult(new HistoryLocalPayloadInspection(
                    true, true, path, new FileInfo(path).Length, string.Empty));
            if (Directory.Exists(path))
                return ValueTask.FromResult(new HistoryLocalPayloadInspection(
                    false, false, path, 0, "Automatic retention does not recursively delete directory payloads."));
            return ValueTask.FromResult(new HistoryLocalPayloadInspection(
                true, false, path, 0, "Registered local payload bytes are already absent."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return ValueTask.FromResult(new HistoryLocalPayloadInspection(false, false, string.Empty, 0, ex.Message));
        }
    }

    public ValueTask DeleteAsync(string resolvedPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathFullyQualified(resolvedPath))
            throw new ArgumentException("Payload deletion requires a fully qualified file path.", nameof(resolvedPath));
        if (Directory.Exists(resolvedPath))
            throw new InvalidOperationException("Automatic retention never recursively deletes a directory payload.");
        if (File.Exists(resolvedPath))
        {
            File.SetAttributes(resolvedPath, FileAttributes.Normal);
            File.Delete(resolvedPath);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed record HistoryCompactionPayload(
    string Format,
    string PayloadPath,
    long Size,
    string? LogicalSha256,
    string? StateFingerprint,
    ImmutableDictionary<string, string> Metadata);

public interface IHistoryCompactionBackend
{
    Task<HistoryCompactionPayload> CreateFullAsync(
        SourceVersion version,
        string materializedDirectory,
        RepresentationId replacementRepresentationId,
        string durableOutputDirectory,
        CancellationToken cancellationToken);

    ValueTask<PayloadVerificationResult> DeepVerifyAsync(
        VersionRepresentation representation,
        string payloadPath,
        CancellationToken cancellationToken);
}

public interface IHistoryArtifactGarbageCollector
{
    Task GarbageCollectAsync(
        IReadOnlySet<Guid> protectedArtifactRootIds,
        CancellationToken cancellationToken);
}

public sealed class NullHistoryArtifactGarbageCollector : IHistoryArtifactGarbageCollector
{
    public Task GarbageCollectAsync(
        IReadOnlySet<Guid> protectedArtifactRootIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
