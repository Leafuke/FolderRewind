using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FolderRewind.History.Representation;

public sealed record RecoveryPayloadPreview(RepresentationId RepresentationId, long? DownloadBytes);

// A metadata/local-presence preview, never a claim of remote reachability or verified payloads.
public sealed record HistoryRecoveryPreview(SourceVersion Version, VersionAssessment Assessment,
    ImmutableArray<RecoveryPayloadPreview> MissingPayloads)
{
    public MaterializationFidelity RequiredFidelity => FidelityFor(Version);
    public bool CanPrepare => Assessment.Readiness is HistoryReadiness.Ready or HistoryReadiness.PreparationRequired
        && Assessment.Selected is not null && MissingPayloads.All(p => p.DownloadBytes is >= 0);
    public long? DownloadBytes => MissingPayloads.Any(p => p.DownloadBytes is null)
        ? null : MissingPayloads.Aggregate(0L, (total, p) => checked(total + p.DownloadBytes!.Value));

    public static MaterializationFidelity FidelityFor(CaptureScope scope) => scope == CaptureScope.PartialSource
        ? MaterializationFidelity.Partial : MaterializationFidelity.Exact;

    public static MaterializationFidelity FidelityFor(SourceVersion version)
        => version.BoundaryConfidence == HistoricalBoundaryConfidence.Unknown ? MaterializationFidelity.Partial : FidelityFor(version.CaptureScope);

    public static HistoryRecoveryPreview Create(SourceVersion version,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph, VersionAssessment assessment,
        Func<RepresentationId, bool> isLocal, Func<RepresentationId, long?> cloudBytes)
    {
        if (assessment.VersionId != version.VersionId || assessment.RequiredFidelity != FidelityFor(version))
            throw new ArgumentException("Recovery assessment does not match the selected version and scope.");
        if (assessment.Selected is null) return new(version, assessment, []);
        var closure = ExactReplicaPreparation.Closure(graph[assessment.Selected.RepresentationId], graph);
        return new(version, assessment, [.. closure.Where(r => !isLocal(r.RepresentationId))
            .Select(r => new RecoveryPayloadPreview(r.RepresentationId, cloudBytes(r.RepresentationId)))]);
    }
}
