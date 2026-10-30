using FolderRewind.History.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

/// <summary>Host-only opt-in: verifies the complete payload closure inside the same materialization lease.
/// A successful return must include full byte verification; explicit Deep Verify is unaffected.</summary>
public interface IInlineVerifiedMaterializer
{
    bool CanVerifyDuringMaterialization(VersionRepresentation representation);
}

public sealed record RepresentationAssessmentContext(
    VersionRepresentation Representation,
    IReadOnlyDictionary<RepresentationId, VersionRepresentation> RepresentationGraph,
    IReadOnlyDictionary<RepresentationId, RepresentationAssessment> DependencyAssessments,
    IRepresentationEnvironment Environment,
    AssessmentDepth Depth,
    ArchiveRestoreVerificationContext? RestoreVerification = null);

public sealed record RepresentationMaterializationContext(
    VersionRepresentation Representation,
    IReadOnlyList<VersionRepresentation> DependencyFirstClosure,
    IReadOnlyDictionary<RepresentationId, RepresentationAssessment> Assessments,
    string StagingDirectory,
    ArchiveRestoreVerificationContext? RestoreVerification = null);

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

