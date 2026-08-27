using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Capture;

public enum SourceCaptureOutcome
{
    Captured = 0,
    Reused = 1,
    NoChanges = 2,
    Unavailable = 3,
    Failed = 4,
    Canceled = 5,
    Blocked = 6
}

public enum CapturePayloadState
{
    Staged = 0,
    FinalUnverified = 1,
    VerifiedFinal = 2
}

public sealed record CapturePayloadCandidate(
    string AbsolutePath,
    CapturePayloadState State,
    long? ExpectedSize,
    string? ExpectedStorageSha256)
{
    public string FileName => Path.GetFileName(AbsolutePath);
}

public sealed record RepresentationCandidate
{
    public RepresentationCandidate(
        RepresentationId representationId,
        RepresentationKind kind,
        string format,
        IEnumerable<RepresentationId>? dependencyRepresentationIds,
        RestoreStrategy restoreStrategy,
        string? logicalSha256,
        string? stateFingerprint,
        IEnumerable<KeyValuePair<string, string>>? metadata,
        bool isLegacyBridgeCandidate = false)
    {
        RepresentationId = representationId;
        Kind = kind;
        Format = format;
        DependencyRepresentationIds = dependencyRepresentationIds is null ? [] : [.. dependencyRepresentationIds];
        RestoreStrategy = restoreStrategy;
        LogicalSha256 = logicalSha256;
        StateFingerprint = stateFingerprint;
        Metadata = metadata is null
            ? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
            : metadata.ToImmutableSortedDictionary(StringComparer.Ordinal);
        IsLegacyBridgeCandidate = isLegacyBridgeCandidate;
    }

    public RepresentationId RepresentationId { get; }
    public RepresentationKind Kind { get; }
    public string Format { get; }
    public ImmutableArray<RepresentationId> DependencyRepresentationIds { get; }
    public RestoreStrategy RestoreStrategy { get; }
    public string? LogicalSha256 { get; }
    public string? StateFingerprint { get; }
    public ImmutableSortedDictionary<string, string> Metadata { get; }
    public bool IsLegacyBridgeCandidate { get; }

    public VersionRepresentation ToFact(VersionId versionId)
    {
        if (IsLegacyBridgeCandidate)
        {
            throw new InvalidOperationException("A Legacy runtime bridge candidate cannot enter Native History.");
        }

        return new VersionRepresentation(
            RepresentationId,
            versionId,
            Kind,
            Format,
            DependencyRepresentationIds,
            RestoreStrategy,
            LogicalSha256,
            StateFingerprint,
            Metadata);
    }
}

public sealed record LocalReplicaCandidate(
    LocalReplicaId LocalReplicaId,
    RepresentationId RepresentationId,
    LocalReplicaLocator Locator,
    CapturePayloadState PayloadState,
    DateTimeOffset CapturedAtUtc);

public interface ICaptureCleanupHandle
{
    ValueTask CleanupAsync(CancellationToken cancellationToken);
}

public sealed record SourceCaptureResult
{
    public SourceCaptureResult(
        SourceId sourceId,
        SourceCaptureOutcome outcome,
        CaptureScope captureScope,
        string? stateFingerprint,
        VersionId? existingVersionId,
        RepresentationCandidate? representationCandidate,
        LocalReplicaCandidate? localReplicaCandidate,
        CapturePayloadCandidate? payloadCandidate,
        long expectedWorkspaceRevision,
        VersionId? expectedBaseVersionId,
        ICaptureCleanupHandle? cleanupHandle,
        IEnumerable<HistoryDiagnostic>? diagnostics)
    {
        SourceId = sourceId;
        Outcome = outcome;
        CaptureScope = captureScope;
        StateFingerprint = stateFingerprint;
        ExistingVersionId = existingVersionId;
        RepresentationCandidate = representationCandidate;
        LocalReplicaCandidate = localReplicaCandidate;
        PayloadCandidate = payloadCandidate;
        ExpectedWorkspaceRevision = expectedWorkspaceRevision;
        ExpectedBaseVersionId = expectedBaseVersionId;
        CleanupHandle = cleanupHandle;
        Diagnostics = diagnostics is null ? [] : [.. diagnostics];
        ValidateShape();
    }

    public SourceId SourceId { get; }
    public SourceCaptureOutcome Outcome { get; }
    public CaptureScope CaptureScope { get; }
    public string? StateFingerprint { get; }
    public VersionId? ExistingVersionId { get; }
    public RepresentationCandidate? RepresentationCandidate { get; }
    public LocalReplicaCandidate? LocalReplicaCandidate { get; }
    public CapturePayloadCandidate? PayloadCandidate { get; }
    public long ExpectedWorkspaceRevision { get; }
    public VersionId? ExpectedBaseVersionId { get; }
    public ICaptureCleanupHandle? CleanupHandle { get; }
    public ImmutableArray<HistoryDiagnostic> Diagnostics { get; }

    // Commit 8–16 的 Legacy Backup UI adapter 只消费这些投影；Native coordinator 消费上面的稳定字段。
    public bool Success => Outcome is SourceCaptureOutcome.Captured
        or SourceCaptureOutcome.Reused
        or SourceCaptureOutcome.NoChanges
        or SourceCaptureOutcome.Unavailable;
    public bool IsUnavailable => Outcome == SourceCaptureOutcome.Unavailable;
    public string? FileName => PayloadCandidate?.FileName;

    public static SourceCaptureResult NoChanges(SourceId sourceId, CaptureScope scope)
        => new(sourceId, SourceCaptureOutcome.NoChanges, scope, null, null, null, null, null, -1, null, null, []);

    public static SourceCaptureResult Unavailable(SourceId sourceId, CaptureScope scope, string? diagnostic = null)
        => new(
            sourceId, SourceCaptureOutcome.Unavailable, scope, null, null, null, null, null, -1, null, null,
            string.IsNullOrWhiteSpace(diagnostic)
                ? []
                : [new HistoryDiagnostic("capture.unavailable", HistoryDiagnosticSeverity.Warning, diagnostic)]);

    public static SourceCaptureResult Failed(SourceId sourceId, CaptureScope scope, string? diagnostic = null)
        => new(
            sourceId, SourceCaptureOutcome.Failed, scope, null, null, null, null, null, -1, null, null,
            string.IsNullOrWhiteSpace(diagnostic)
                ? []
                : [new HistoryDiagnostic("capture.failed", HistoryDiagnosticSeverity.Error, diagnostic)]);

    private void ValidateShape()
    {
        if (Outcome == SourceCaptureOutcome.Captured
            && (RepresentationCandidate is null || LocalReplicaCandidate is null || PayloadCandidate is null))
        {
            throw new ArgumentException("Captured result requires Representation, LocalReplica, and payload candidates.");
        }
        if (Outcome == SourceCaptureOutcome.Reused && ExistingVersionId is null)
        {
            throw new ArgumentException("Reused result requires ExistingVersionId.");
        }
        if (LocalReplicaCandidate is not null
            && RepresentationCandidate is not null
            && LocalReplicaCandidate.RepresentationId != RepresentationCandidate.RepresentationId)
        {
            throw new ArgumentException("LocalReplica candidate must belong to the Representation candidate.");
        }
    }
}
