using FolderRewind.History.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public sealed record RepresentationAssessmentContext(
    VersionRepresentation Representation,
    IReadOnlyDictionary<RepresentationId, VersionRepresentation> RepresentationGraph,
    IReadOnlyDictionary<RepresentationId, RepresentationAssessment> DependencyAssessments,
    IRepresentationEnvironment Environment,
    AssessmentDepth Depth);

public sealed record RepresentationMaterializationContext(
    VersionRepresentation Representation,
    IReadOnlyList<VersionRepresentation> DependencyFirstClosure,
    IReadOnlyDictionary<RepresentationId, RepresentationAssessment> Assessments,
    string StagingDirectory);

public interface IRepresentationHandler
{
    bool CanHandle(VersionRepresentation representation);

    ValueTask<RepresentationAssessment> AssessAsync(
        RepresentationAssessmentContext context,
        CancellationToken cancellationToken);

    ValueTask MaterializeAsync(
        RepresentationMaterializationContext context,
        CancellationToken cancellationToken);
}

