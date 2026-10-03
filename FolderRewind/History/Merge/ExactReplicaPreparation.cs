using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

// Explicit user preparation only. The download delegate owns publication under the Runtime gate.
internal static class ExactReplicaPreparation
{
    internal static async Task PrepareAsync(IEnumerable<(SourceId Source, VersionId Version)> inputs,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations,
        Func<VersionId, AssessmentDepth, CancellationToken, Task<VersionAssessment>> assess,
        Func<RepresentationId, bool> isLocal,
        Func<SourceId, VersionRepresentation, CancellationToken, Task<bool>> download,
        CancellationToken token)
    {
        var versions = inputs.DistinctBy(i => i.Version).ToArray();
        var downloaded = new HashSet<RepresentationId>();
        foreach (var input in versions)
        {
            token.ThrowIfCancellationRequested();
            var state = await assess(input.Version, AssessmentDepth.Fast, token).ConfigureAwait(false);
            if (state.Readiness == HistoryReadiness.Ready) continue;
            if (state.Readiness != HistoryReadiness.PreparationRequired || state.Selected is null)
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.ExactUnavailable, input.Source, input.Version));
            foreach (var representation in Closure(representations[state.Selected.RepresentationId], representations))
            {
                token.ThrowIfCancellationRequested();
                if (!downloaded.Add(representation.RepresentationId) || isLocal(representation.RepresentationId)) continue;
                var success = await download(input.Source, representation, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!success)
                    throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.PreparationRequired, input.Source, input.Version, representation.RepresentationId));
            }
        }
        foreach (var input in versions)
        {
            var state = await assess(input.Version, AssessmentDepth.Deep, token).ConfigureAwait(false);
            if (state.Readiness != HistoryReadiness.Ready)
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.ExactUnavailable, input.Source, input.Version, state.Selected?.RepresentationId));
        }
    }

    internal static IReadOnlyList<VersionRepresentation> Closure(VersionRepresentation root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> representations)
    {
        var result = new List<VersionRepresentation>();
        var complete = new HashSet<RepresentationId>();
        var visiting = new HashSet<RepresentationId>();
        void Visit(VersionRepresentation value)
        {
            if (complete.Contains(value.RepresentationId)) return;
            if (!visiting.Add(value.RepresentationId)) throw new InvalidDataException("Representation dependency cycle.");
            foreach (var id in value.DependencyRepresentationIds)
            {
                if (!representations.TryGetValue(id, out var dependency)) throw new InvalidDataException("Representation dependency is missing.");
                Visit(dependency);
            }
            visiting.Remove(value.RepresentationId); complete.Add(value.RepresentationId); result.Add(value);
        }
        Visit(root); return result;
    }
}
