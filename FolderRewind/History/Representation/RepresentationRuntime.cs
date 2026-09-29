using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public sealed class RepresentationRuntime
{
    private readonly ImmutableArray<IRepresentationHandler> _handlers;

    public RepresentationRuntime(IEnumerable<IRepresentationHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = [.. handlers];
        if (_handlers.IsEmpty)
        {
            throw new ArgumentException("At least one Representation handler is required.", nameof(handlers));
        }
    }

    public async Task<VersionAssessment> AssessVersionAsync(
        VersionId versionId,
        IReadOnlyList<VersionRepresentation> representations,
        IRepresentationEnvironment environment,
        AssessmentDepth depth,
        MaterializationFidelity requiredFidelity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(representations);
        ArgumentNullException.ThrowIfNull(environment);
        var graph = representations.ToDictionary(item => item.RepresentationId);
        var candidates = representations.Where(item => item.VersionId == versionId).ToArray();
        if (candidates.Length == 0)
        {
            return new VersionAssessment(
                versionId,
                depth,
                requiredFidelity,
                HistoryReadiness.Unavailable,
                null,
                []);
        }

        var assessments = new Dictionary<RepresentationId, RepresentationAssessment>();
        var visiting = new HashSet<RepresentationId>();
        foreach (var candidate in candidates)
        {
            await AssessRepresentationAsync(candidate, graph, environment, depth, assessments, visiting, cancellationToken)
                .ConfigureAwait(false);
        }

        var candidateAssessments = candidates.Select(item => assessments[item.RepresentationId]).ToImmutableArray();
        var selected = candidateAssessments
            .Where(item => Satisfies(item.Fidelity, requiredFidelity))
            .OrderBy(SelectionScore)
            .ThenBy(item => item.RepresentationId.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected is null)
        {
            return new VersionAssessment(
                versionId,
                depth,
                requiredFidelity,
                candidateAssessments.Any(item => item.Readiness is HistoryReadiness.Ready or HistoryReadiness.PreparationRequired)
                    ? HistoryReadiness.Blocked
                    : candidateAssessments.All(item => item.Readiness == HistoryReadiness.Unavailable)
                        ? HistoryReadiness.Unavailable
                        : HistoryReadiness.Blocked,
                null,
                candidateAssessments);
        }

        return new VersionAssessment(
            versionId,
            depth,
            requiredFidelity,
            selected.Readiness,
            selected,
            candidateAssessments);
    }

    public async Task MaterializeAsync(
        RepresentationId representationId,
        IReadOnlyList<VersionRepresentation> representations,
        IRepresentationEnvironment environment,
        MaterializationFidelity requiredFidelity,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var graph = representations.ToDictionary(item => item.RepresentationId);
        if (!graph.TryGetValue(representationId, out var representation))
        {
            throw new KeyNotFoundException($"Representation '{representationId}' does not exist.");
        }

        var assessments = new Dictionary<RepresentationId, RepresentationAssessment>();
        await AssessRepresentationAsync(
            representation,
            graph,
            environment,
            AssessmentDepth.Deep,
            assessments,
            new HashSet<RepresentationId>(),
            cancellationToken).ConfigureAwait(false);
        var selected = assessments[representationId];
        if (selected.Readiness != HistoryReadiness.Ready
            || !Satisfies(selected.Fidelity, requiredFidelity))
        {
            throw new InvalidOperationException(
                $"Representation '{representationId}' is not Ready with required fidelity {requiredFidelity}.");
        }

        var closure = BuildDependencyFirstClosure(representation, graph);
        var handler = ResolveHandler(representation)
            ?? throw new InvalidOperationException(
                $"No handler understands {representation.Kind}/{representation.Format}.");
        Directory.CreateDirectory(stagingDirectory);
        await handler.MaterializeAsync(
            new RepresentationMaterializationContext(
                representation,
                closure,
                assessments,
                stagingDirectory),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RepresentationAssessment> AssessRepresentationAsync(
        VersionRepresentation representation,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph,
        IRepresentationEnvironment environment,
        AssessmentDepth depth,
        Dictionary<RepresentationId, RepresentationAssessment> assessments,
        HashSet<RepresentationId> visiting,
        CancellationToken cancellationToken)
    {
        if (assessments.TryGetValue(representation.RepresentationId, out var known))
        {
            return known;
        }
        if (!visiting.Add(representation.RepresentationId))
        {
            throw new InvalidDataException("Representation dependency graph contains a cycle.");
        }

        var dependencyAssessments = new Dictionary<RepresentationId, RepresentationAssessment>();
        foreach (var dependencyId in representation.DependencyRepresentationIds)
        {
            if (!graph.TryGetValue(dependencyId, out var dependency))
            {
                dependencyAssessments[dependencyId] = new RepresentationAssessment(
                    dependencyId,
                    HistoryReadiness.Blocked,
                    MaterializationFidelity.Unknown,
                    [],
                    ["Representation dependency is absent from metadata."]);
                continue;
            }

            dependencyAssessments[dependencyId] = await AssessRepresentationAsync(
                dependency,
                graph,
                environment,
                depth,
                assessments,
                visiting,
                cancellationToken).ConfigureAwait(false);
        }

        var handler = ResolveHandler(representation);
        var result = handler is null
            ? new RepresentationAssessment(
                representation.RepresentationId,
                HistoryReadiness.Blocked,
                MaterializationFidelity.Unknown,
                [],
                [$"No handler understands {representation.Kind}/{representation.Format}."])
            : await handler.AssessAsync(
                new RepresentationAssessmentContext(
                    representation,
                    graph,
                    dependencyAssessments,
                    environment,
                    depth),
                cancellationToken).ConfigureAwait(false);
        visiting.Remove(representation.RepresentationId);
        assessments.Add(representation.RepresentationId, result);
        return result;
    }

    private IRepresentationHandler? ResolveHandler(VersionRepresentation representation)
    {
        var matches = _handlers.Where(handler => handler.CanHandle(representation)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple Representation handlers claim '{representation.Kind}/{representation.Format}'.")
        };
    }

    private static IReadOnlyList<VersionRepresentation> BuildDependencyFirstClosure(
        VersionRepresentation root,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
    {
        var visited = new HashSet<RepresentationId>();
        var ordered = new List<VersionRepresentation>();
        Visit(root);
        return ordered.ToImmutableArray();

        void Visit(VersionRepresentation item)
        {
            if (!visited.Add(item.RepresentationId)) return;
            foreach (var dependencyId in item.DependencyRepresentationIds)
            {
                if (!graph.TryGetValue(dependencyId, out var dependency))
                {
                    throw new InvalidDataException($"Representation dependency '{dependencyId}' is missing.");
                }
                Visit(dependency);
            }
            ordered.Add(item);
        }
    }

    private static int SelectionScore(RepresentationAssessment assessment)
        => (assessment.Readiness, assessment.Fidelity) switch
        {
            (HistoryReadiness.Ready, MaterializationFidelity.Exact) => 0,
            (HistoryReadiness.PreparationRequired, MaterializationFidelity.Exact) => 1,
            (HistoryReadiness.Ready, MaterializationFidelity.Partial) => 2,
            (HistoryReadiness.PreparationRequired, MaterializationFidelity.Partial) => 3,
            (HistoryReadiness.Blocked, _) => 4,
            _ => 5
        };

    private static bool Satisfies(
        MaterializationFidelity actual,
        MaterializationFidelity required)
        => required switch
        {
            MaterializationFidelity.Exact => actual == MaterializationFidelity.Exact,
            MaterializationFidelity.Partial => actual is MaterializationFidelity.Exact or MaterializationFidelity.Partial,
            MaterializationFidelity.Unknown => true,
            _ => false
        };
}
