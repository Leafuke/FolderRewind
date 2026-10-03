using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Immutable;

namespace FolderRewind.History.Retention;

public enum HistoryChainRewriteOrigin { Manual, Retention }

public sealed record HistoryChainRewriteRequest(
    HistoryChainRewriteOrigin Origin,
    ImmutableArray<LocalReplicaId> TargetReplicaIds,
    ImmutableArray<VersionId> TargetVersionIds,
    ImmutableArray<VersionId> RetainedVersionIds,
    int MaximumDeltaDepth = 5,
    bool HideTargets = false,
    bool ReleaseTargets = false);

public sealed record HistoryChainRewriteStep(
    VersionRepresentation Original,
    RepresentationId ReplacementId,
    RepresentationId? BaseRepresentationId,
    bool ReusePayload,
    RepresentationId? ExistingAlternativeId = null);

public sealed record HistoryChainRewritePlan(
    HistoryTransactionId OperationId,
    HistoryChainRewriteRequest Request,
    string StateFingerprint,
    LocalReplicaCatalog Catalog,
    ImmutableArray<VersionRepresentation> Representations,
    ImmutableArray<VersionId> ProtectedVersions,
    ImmutableArray<HistoryChainRewriteStep> Steps,
    ImmutableArray<string> Blockers)
{
    public bool CanExecute => Blockers.IsEmpty;
}

public sealed record HistoryChainRewriteProgress(string Stage, int Completed, int Total);

public sealed record HistoryChainRewriteResult(bool Committed, bool CleanupPending,
    long ReclaimedBytes, long CreatedBytes, int RewrittenVersions, string Diagnostic)
{
    public long NetReleasedBytes => ReclaimedBytes - CreatedBytes;
}

public static class HistoryChainRewriteStrategy
{
    public const decimal FullThreshold = 0.8m;

    public static bool PreferFull(bool hasBase, long changedBytes, long totalBytes,
        int deltaDepth, int maximumDepth, bool structuralChange, bool preferDelta = false)
        => !hasBase || totalBytes == 0 || structuralChange
           || (maximumDepth > 0 && deltaDepth > maximumDepth)
           || (!preferDelta && changedBytes >= totalBytes * FullThreshold);
}
