using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public sealed class PluginArtifactRepresentationHandler : IRepresentationHandler
{
    public const string ArtifactRootIdMetadataKey = "artifactRootId";
    public const string PluginIdMetadataKey = "pluginId";
    private readonly IPluginArtifactBackend _backend;

    public PluginArtifactRepresentationHandler(IPluginArtifactBackend backend)
        => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public bool CanHandle(VersionRepresentation representation)
        => representation.Kind == RepresentationKind.PluginArtifact;

    public async ValueTask<RepresentationAssessment> AssessAsync(
        RepresentationAssessmentContext context,
        CancellationToken cancellationToken)
    {
        foreach (var dependencyId in context.Representation.DependencyRepresentationIds)
        {
            if (!context.DependencyAssessments.TryGetValue(dependencyId, out var dependency)
                || dependency.Readiness is HistoryReadiness.Blocked or HistoryReadiness.Unavailable)
            {
                return Blocked(context.Representation, $"Dependency '{dependencyId}' is not materializable.");
            }
            if (dependency.Readiness == HistoryReadiness.PreparationRequired)
            {
                return new RepresentationAssessment(
                    context.Representation.RepresentationId,
                    HistoryReadiness.PreparationRequired,
                    dependency.Fidelity,
                    dependency.Evidence,
                    [$"Dependency '{dependencyId}' requires preparation."]);
            }
        }

        if (!TryGetBinding(context.Representation, out var artifactRootId, out var pluginId, out var diagnostic))
        {
            return Blocked(context.Representation, diagnostic);
        }

        var probe = await _backend.ProbeAsync(artifactRootId, pluginId, cancellationToken).ConfigureAwait(false);
        if (!probe.PluginAvailable)
        {
            return Blocked(context.Representation, string.IsNullOrWhiteSpace(probe.Diagnostic)
                ? "Required plugin is unavailable."
                : probe.Diagnostic);
        }
        if (!probe.ArtifactAvailable)
        {
            return new RepresentationAssessment(
                context.Representation.RepresentationId,
                HistoryReadiness.Unavailable,
                probe.Fidelity,
                [new RepresentationEvidence(
                    RepresentationEvidenceKind.Plugin,
                    probe.Diagnostic,
                    DateTimeOffset.UtcNow,
                    ReplicaAvailabilityObservation.Missing,
                    ReplicaIntegrityObservation.Unknown)],
                ["Plugin Artifact payload is unavailable."]);
        }

        if (context.Depth == AssessmentDepth.Deep)
        {
            var verification = await _backend.VerifyAsync(artifactRootId, pluginId, cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Success)
            {
                return new RepresentationAssessment(
                    context.Representation.RepresentationId,
                    HistoryReadiness.Unavailable,
                    probe.Fidelity,
                    [new RepresentationEvidence(
                        RepresentationEvidenceKind.DeepVerification,
                        verification.Diagnostic,
                        DateTimeOffset.UtcNow,
                        ReplicaAvailabilityObservation.Available,
                        ReplicaIntegrityObservation.Corrupt)],
                    ["Plugin Artifact failed closure verification."]);
            }
        }

        return new RepresentationAssessment(
            context.Representation.RepresentationId,
            HistoryReadiness.Ready,
            probe.Fidelity,
            [new RepresentationEvidence(
                RepresentationEvidenceKind.Plugin,
                probe.Diagnostic,
                DateTimeOffset.UtcNow,
                ReplicaAvailabilityObservation.Available,
                context.Depth == AssessmentDepth.Deep
                    ? ReplicaIntegrityObservation.Verified
                    : ReplicaIntegrityObservation.Unknown)],
            []);
    }

    public async ValueTask MaterializeAsync(
        RepresentationMaterializationContext context,
        CancellationToken cancellationToken)
    {
        if (!TryGetBinding(context.Representation, out var artifactRootId, out var pluginId, out var diagnostic))
        {
            throw new InvalidOperationException(diagnostic);
        }
        await _backend.MaterializeAsync(
            artifactRootId,
            pluginId,
            context.StagingDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetBinding(
        VersionRepresentation representation,
        out Guid artifactRootId,
        out string pluginId,
        out string diagnostic)
    {
        representation.RepresentationSpecificMetadata.TryGetValue(ArtifactRootIdMetadataKey, out var rootText);
        representation.RepresentationSpecificMetadata.TryGetValue(PluginIdMetadataKey, out pluginId!);
        if (!Guid.TryParse(rootText, out artifactRootId)
            || artifactRootId == Guid.Empty
            || string.IsNullOrWhiteSpace(pluginId))
        {
            diagnostic = "Plugin Artifact representation binding is incomplete.";
            pluginId = string.Empty;
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static RepresentationAssessment Blocked(VersionRepresentation representation, string diagnostic)
        => new(
            representation.RepresentationId,
            HistoryReadiness.Blocked,
            MaterializationFidelity.Unknown,
            [],
            [diagnostic]);
}
