using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryExactCheckpointAdmissionStatus
{
    Ready = 0,
    StructurallyIncomplete = 1,
    VersionMissing = 2,
    VersionIdentityMismatch = 3,
    BoundaryMismatch = 4,
    ExactLogicalParentMissing = 5,
    ExactRepresentationUnavailable = 6
}

public sealed record HistoryExactCheckpointAdmissionResult(
    HistoryExactCheckpointAdmissionStatus Status,
    ImmutableArray<SourceId> AffectedSources,
    string Diagnostic)
{
    public bool IsReady => Status == HistoryExactCheckpointAdmissionStatus.Ready;
}

public sealed class HistoryExactCheckpointAdmission
{
    private readonly HistoryRuntime _history;

    public HistoryExactCheckpointAdmission(HistoryRuntime history)
        => _history = history ?? throw new ArgumentNullException(nameof(history));

    public async Task<HistoryExactCheckpointAdmissionResult> EvaluateAsync(
        ConfigurationCheckpoint checkpoint,
        IEnumerable<SourceVersion>? additionalVersions = null,
        IEnumerable<VersionRepresentation>? additionalRepresentations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var versions = (await _history.Query.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false))
            .Concat(additionalVersions ?? [])
            .DistinctBy(item => item.VersionId)
            .ToDictionary(item => item.VersionId);
        var representations = (await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false))
            .Concat(additionalRepresentations ?? [])
            .DistinctBy(item => item.RepresentationId)
            .ToDictionary(item => item.RepresentationId);
        return Evaluate(checkpoint, versions, representations);
    }

    internal static HistoryExactCheckpointAdmissionResult Evaluate(
        ConfigurationCheckpoint checkpoint,
        IReadOnlyDictionary<VersionId, SourceVersion> versions,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations)
    {
        if (!checkpoint.IsStructurallyComplete)
            return Blocked(HistoryExactCheckpointAdmissionStatus.StructurallyIncomplete,
                checkpoint.Sources.Where(item => item.VersionId is null).Select(item => item.SourceId),
                "Checkpoint Source roster contains a non-deletion entry without a Version.");

        foreach (var source in checkpoint.Sources)
        {
            if (!versions.TryGetValue(source.VersionId!.Value, out var version))
                return Blocked(HistoryExactCheckpointAdmissionStatus.VersionMissing, [source.SourceId],
                    "Checkpoint references a missing SourceVersion.");
            if (version.ConfigId != checkpoint.ConfigId || version.SourceId != source.SourceId)
                return Blocked(HistoryExactCheckpointAdmissionStatus.VersionIdentityMismatch, [source.SourceId],
                    "Checkpoint SourceVersion belongs to another Config or Source.");
            if (!StringComparer.Ordinal.Equals(
                    source.EffectiveSourceBoundaryFingerprint,
                    version.EffectiveSourceBoundaryFingerprint))
                return Blocked(HistoryExactCheckpointAdmissionStatus.BoundaryMismatch, [source.SourceId],
                    "Checkpoint and SourceVersion Effective Source Boundaries differ.");
            if (version.CaptureScope == CaptureScope.PartialSource
                && version.ParentVersionIds.IsEmpty)
                return Blocked(HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing, [source.SourceId],
                    "PartialSource artifact has no reliable Exact logical parent.");
            if (!representations.Values
                    .Where(item => item.VersionId == version.VersionId)
                    .Any(root => HasExactClosure(root, representations, new HashSet<RepresentationId>())))
                return Blocked(HistoryExactCheckpointAdmissionStatus.ExactRepresentationUnavailable, [source.SourceId],
                    "SourceVersion has no declared Exact representation closure.");
        }

        return new(HistoryExactCheckpointAdmissionStatus.Ready, [], string.Empty);
    }

    private static bool HasExactClosure(
        VersionRepresentation representation,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations,
        HashSet<RepresentationId> visiting)
    {
        if (representation.Fidelity != MaterializationFidelity.Exact
            || !visiting.Add(representation.RepresentationId))
            return false;
        foreach (var dependencyId in representation.DependencyRepresentationIds)
        {
            if (!representations.TryGetValue(dependencyId, out var dependency)
                || !HasExactClosure(dependency, representations, visiting))
                return false;
        }
        visiting.Remove(representation.RepresentationId);
        return true;
    }

    private static HistoryExactCheckpointAdmissionResult Blocked(
        HistoryExactCheckpointAdmissionStatus status,
        IEnumerable<SourceId> affectedSources,
        string diagnostic)
        => new(status, affectedSources.Distinct().ToImmutableArray(), diagnostic);
}
