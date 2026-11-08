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
    ExactRepresentationUnavailable = 6,
    HistoricalBoundaryUnknown = 7
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
        SourceCheckpoint checkpoint,
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
        return Evaluate(checkpoint, versions, representations, cancellationToken);
    }

    internal static HistoryExactCheckpointAdmissionResult Evaluate(
        SourceCheckpoint checkpoint,
        IReadOnlyDictionary<VersionId, SourceVersion> versions,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations,
        CancellationToken cancellationToken = default)
        => new EvaluationContext(versions, representations, cancellationToken).Evaluate(checkpoint, cancellationToken);

    // One immutable graph snapshot per operation. No file availability or cross-query
    // state is cached, and checkpoints sharing a Version still retain their own boundary checks.
    internal sealed class EvaluationContext
    {
        private readonly IReadOnlyDictionary<VersionId, SourceVersion> _versions;
        private readonly IReadOnlyDictionary<RepresentationId, VersionRepresentation> _representations;
        private readonly ILookup<VersionId, VersionRepresentation> _byVersion;
        private readonly Dictionary<RepresentationId, bool> _exactClosures = [];
        private readonly Dictionary<VersionId, bool> _logicalParents = [];
        private readonly Dictionary<EffectiveSourceBoundarySnapshot, string> _boundaries = new(new BoundaryComparer());
        private readonly Dictionary<SourceCheckpoint, HistoryExactCheckpointAdmissionResult> _checkpoints = new(ReferenceEqualityComparer.Instance);

        internal EvaluationContext(IReadOnlyDictionary<VersionId, SourceVersion> versions,
            IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _versions = versions;
            _representations = representations;
            _byVersion = representations.Values.ToLookup(item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return item.VersionId;
            });
        }

        internal HistoryExactCheckpointAdmissionResult Evaluate(SourceCheckpoint checkpoint, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (_checkpoints.TryGetValue(checkpoint, out var known)) return known;
            var result = EvaluateCore(checkpoint, token);
            token.ThrowIfCancellationRequested();
            _checkpoints.Add(checkpoint, result);
            return result;
        }

        private HistoryExactCheckpointAdmissionResult EvaluateCore(SourceCheckpoint checkpoint, CancellationToken token)
        {
            if (!checkpoint.IsStructurallyComplete)
                return Blocked(HistoryExactCheckpointAdmissionStatus.StructurallyIncomplete,
                    [checkpoint.SourceId],
                    "Checkpoint requires a valid SourceVersion identity.");

            token.ThrowIfCancellationRequested();
            if (!_versions.TryGetValue(checkpoint.VersionId, out var version))
                return Blocked(HistoryExactCheckpointAdmissionStatus.VersionMissing, [checkpoint.SourceId],
                    "Checkpoint references a missing SourceVersion.");
            if (version.ConfigId != checkpoint.ConfigId || version.SourceId != checkpoint.SourceId)
                return Blocked(HistoryExactCheckpointAdmissionStatus.VersionIdentityMismatch, [checkpoint.SourceId],
                    "Checkpoint SourceVersion belongs to another Config or Source.");
            if (version.BoundaryConfidence == HistoricalBoundaryConfidence.Unknown)
                return Blocked(HistoryExactCheckpointAdmissionStatus.HistoricalBoundaryUnknown,
                    [checkpoint.SourceId], LegacyRecoveryPolicy.BoundaryDiagnostic);
            if (!StringComparer.Ordinal.Equals(Boundary(checkpoint.EffectiveSourceBoundary), Boundary(version.EffectiveSourceBoundary)))
                return Blocked(HistoryExactCheckpointAdmissionStatus.BoundaryMismatch, [checkpoint.SourceId],
                    "Checkpoint and SourceVersion Effective Source Boundaries differ.");
            if (version.CaptureScope == CaptureScope.PartialSource && !HasReliableLogicalParent(version, token))
                return Blocked(HistoryExactCheckpointAdmissionStatus.ExactLogicalParentMissing, [checkpoint.SourceId],
                    "PartialSource artifact has no reliable Exact logical parent.");
            if (!HasExactVersion(version.VersionId, token))
                return Blocked(HistoryExactCheckpointAdmissionStatus.ExactRepresentationUnavailable, [checkpoint.SourceId],
                    "SourceVersion has no declared Exact representation closure.");
            return new(HistoryExactCheckpointAdmissionStatus.Ready, [], string.Empty);
        }

        private string Boundary(EffectiveSourceBoundarySnapshot boundary)
        {
            if (!_boundaries.TryGetValue(boundary, out var value)) _boundaries.Add(boundary, value = boundary.Fingerprint);
            return value;
        }

        // Deserialized Versions/Checkpoints have separate boundary objects. Compare the
        // serialized fields structurally so identical boundaries need only one digest.
        private sealed class BoundaryComparer : IEqualityComparer<EffectiveSourceBoundarySnapshot>
        {
            public bool Equals(EffectiveSourceBoundarySnapshot? left, EffectiveSourceBoundarySnapshot? right)
                => ReferenceEquals(left, right) || left is not null && right is not null
                    && left.SchemaVersion == right.SchemaVersion && left.ScopeMode == right.ScopeMode
                    && left.FilterMode == right.FilterMode && left.UseRegex == right.UseRegex
                    && left.ScopeRules.SequenceEqual(right.ScopeRules) && left.FilterRules.SequenceEqual(right.FilterRules)
                    && left.TypedFilterRules.HasValue == right.TypedFilterRules.HasValue
                    && (left.TypedFilterRules is not { } rules || rules.SequenceEqual(right.TypedFilterRules!.Value));

            public int GetHashCode(EffectiveSourceBoundarySnapshot value)
            {
                var hash = new HashCode();
                hash.Add(value.SchemaVersion); hash.Add(value.ScopeMode); hash.Add(value.FilterMode); hash.Add(value.UseRegex);
                foreach (var rule in value.ScopeRules) hash.Add(rule, StringComparer.Ordinal);
                foreach (var rule in value.FilterRules) hash.Add(rule, StringComparer.Ordinal);
                hash.Add(value.TypedFilterRules.HasValue);
                if (value.TypedFilterRules is { } typed) foreach (var rule in typed) hash.Add(rule);
                return hash.ToHashCode();
            }
        }

        private bool HasExactVersion(VersionId version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            foreach (var root in _byVersion[version])
            {
                if (HasExactClosure(root, token)) return true;
            }
            return false;
        }

        private sealed class LogicalParentFrame(SourceVersion version)
        {
            internal SourceVersion Version { get; } = version;
            internal int NextParent;
            internal bool Reliable = !version.ParentVersionIds.IsEmpty;
        }

        private bool HasReliableLogicalParent(SourceVersion version, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_logicalParents.TryGetValue(version.VersionId, out var known)) return known;
            var visiting = new HashSet<VersionId> { version.VersionId };
            var pending = new Stack<LogicalParentFrame>();
            pending.Push(new(version));
            while (pending.TryPeek(out var frame))
            {
                token.ThrowIfCancellationRequested();
                var current = frame.Version;
                if (!frame.Reliable || frame.NextParent == current.ParentVersionIds.Length)
                {
                    pending.Pop();
                    visiting.Remove(current.VersionId);
                    _logicalParents.Add(current.VersionId, frame.Reliable);
                    continue;
                }
                var id = current.ParentVersionIds[frame.NextParent];
                if (!_versions.TryGetValue(id, out var parent)
                    || parent.ConfigId != current.ConfigId || parent.SourceId != current.SourceId
                    || !StringComparer.Ordinal.Equals(Boundary(parent.EffectiveSourceBoundary), Boundary(current.EffectiveSourceBoundary)))
                {
                    frame.Reliable = false;
                    continue;
                }
                if (parent.CaptureScope == CaptureScope.PartialSource)
                {
                    if (!_logicalParents.TryGetValue(id, out var reliable))
                    {
                        // A back edge fails this AND branch. Only completed frames enter
                        // the operation memo; cancellation never publishes an unfinished node.
                        if (!visiting.Add(id)) frame.Reliable = false;
                        else pending.Push(new(parent));
                        continue;
                    }
                    if (!reliable) { frame.Reliable = false; continue; }
                }
                frame.Reliable = HasExactVersion(id, token);
                frame.NextParent++;
            }
            return _logicalParents[version.VersionId];
        }

        private sealed class ExactClosureFrame(VersionRepresentation representation)
        {
            internal VersionRepresentation Representation { get; } = representation;
            internal int NextDependency;
            internal bool Exact = representation.Fidelity == MaterializationFidelity.Exact;
        }

        private bool HasExactClosure(VersionRepresentation representation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_exactClosures.TryGetValue(representation.RepresentationId, out var known)) return known;
            var exact = representation.Fidelity == MaterializationFidelity.Exact;
            if (!exact || representation.DependencyRepresentationIds.IsEmpty)
            {
                token.ThrowIfCancellationRequested();
                _exactClosures.Add(representation.RepresentationId, exact);
                return exact;
            }
            var visiting = new HashSet<RepresentationId> { representation.RepresentationId };
            var pending = new Stack<ExactClosureFrame>();
            pending.Push(new(representation));
            while (pending.TryPeek(out var frame))
            {
                token.ThrowIfCancellationRequested();
                var current = frame.Representation;
                if (!frame.Exact || frame.NextDependency == current.DependencyRepresentationIds.Length)
                {
                    pending.Pop();
                    visiting.Remove(current.RepresentationId);
                    _exactClosures.Add(current.RepresentationId, frame.Exact);
                    continue;
                }
                var id = current.DependencyRepresentationIds[frame.NextDependency];
                if (!_representations.TryGetValue(id, out var dependency)) frame.Exact = false;
                else if (_exactClosures.TryGetValue(id, out var dependencyExact))
                {
                    frame.Exact = dependencyExact;
                    frame.NextDependency++;
                }
                else if (!visiting.Add(id)) frame.Exact = false;
                else pending.Push(new(dependency));
            }
            return _exactClosures[representation.RepresentationId];
        }
    }

    private static HistoryExactCheckpointAdmissionResult Blocked(
        HistoryExactCheckpointAdmissionStatus status,
        IEnumerable<SourceId> affectedSources,
        string diagnostic)
        => new(status, affectedSources.Distinct().ToImmutableArray(), diagnostic);
}
