using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public class CoreArchiveRepresentationHandler : IRepresentationHandler
{
    private readonly IArchiveRepresentationBackend _backend;

    public CoreArchiveRepresentationHandler(IArchiveRepresentationBackend backend)
        => _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public virtual bool CanHandle(VersionRepresentation representation)
        => representation.Kind is RepresentationKind.CoreFull
            or RepresentationKind.CoreRolling
            or RepresentationKind.LegacyArchive;

    public virtual async ValueTask<RepresentationAssessment> AssessAsync(
        RepresentationAssessmentContext context,
        CancellationToken cancellationToken)
    {
        var dependencyFailure = EvaluateDependencies(context);
        if (dependencyFailure is not null)
        {
            return dependencyFailure;
        }

        return await AssessArchivePayloadAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public virtual async ValueTask MaterializeAsync(
        RepresentationMaterializationContext context,
        CancellationToken cancellationToken)
    {
        var inputs = context.DependencyFirstClosure
            .Select(representation =>
            {
                if (!context.Assessments.TryGetValue(representation.RepresentationId, out var assessment)
                    || assessment.Readiness != HistoryReadiness.Ready
                    || string.IsNullOrWhiteSpace(assessment.SelectedLocalPath))
                {
                    throw new InvalidOperationException(
                        $"Representation '{representation.RepresentationId}' is not locally materializable.");
                }

                return new ArchiveMaterializationInput(representation, assessment.SelectedLocalPath);
            })
            .ToImmutableArray();
        await _backend.MaterializeAsync(inputs, context.StagingDirectory, cancellationToken).ConfigureAwait(false);
    }

    protected async ValueTask<RepresentationAssessment> AssessArchivePayloadAsync(
        RepresentationAssessmentContext context,
        CancellationToken cancellationToken)
    {
        var fidelity = GetEffectiveFidelity(context);
        var localCandidates = context.Environment.GetLocalCandidates(context.Representation.RepresentationId);
        var failures = new List<string>();
        foreach (var local in localCandidates.Where(item => item.Availability == ReplicaAvailabilityObservation.Available))
        {
            if (context.Depth == AssessmentDepth.Fast)
            {
                return Ready(context.Representation, fidelity, local.ResolvedPath!, ReplicaIntegrityObservation.Unknown, "Local payload exists.");
            }

            try
            {
                if (context.Representation.RepresentationSpecificMetadata.TryGetValue("storageSha256", out var expected))
                {
                    await using var input = new FileStream(local.ResolvedPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                    if (!StringComparer.OrdinalIgnoreCase.Equals(expected, digest))
                    {
                        failures.Add($"Storage hash mismatch: {local.ResolvedPath}");
                        continue;
                    }
                }
                var verification = await _backend.VerifyAsync(context.Representation, local.ResolvedPath!, cancellationToken).ConfigureAwait(false);
                if (verification.Success)
                    return Ready(context.Representation, fidelity, local.ResolvedPath!, ReplicaIntegrityObservation.Verified, verification.Evidence);
                failures.Add($"{local.ResolvedPath}: {verification.Diagnostic}");
            }
            catch (IOException ex) { failures.Add($"{local.ResolvedPath}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { failures.Add($"{local.ResolvedPath}: {ex.Message}"); }

        }

        var shared = context.Environment.GetActiveSharedReplicas(context.Representation.RepresentationId)
            .Where(replica =>
            {
                var observation = context.Environment.GetSharedReplicaObservation(replica.ReplicaId);
                return observation?.Availability is not ReplicaAvailabilityObservation.Missing
                       && observation?.Integrity is not ReplicaIntegrityObservation.Corrupt;
            })
            .ToArray();
        if (shared.Length > 0)
        {
            return new RepresentationAssessment(
                context.Representation.RepresentationId,
                HistoryReadiness.PreparationRequired,
                fidelity,
                shared.Select(replica => new RepresentationEvidence(
                    RepresentationEvidenceKind.SharedReplica,
                    replica.ObjectKey,
                    DateTimeOffset.UtcNow,
                    context.Environment.GetSharedReplicaObservation(replica.ReplicaId)?.Availability
                        ?? ReplicaAvailabilityObservation.Unknown,
                    context.Environment.GetSharedReplicaObservation(replica.ReplicaId)?.Integrity
                        ?? ReplicaIntegrityObservation.Unknown)),
                ["A shared replica must be downloaded before materialization."]);
        }

        var hadLocalPayload = localCandidates.Any(item => item.Availability == ReplicaAvailabilityObservation.Available);
        return new RepresentationAssessment(
            context.Representation.RepresentationId,
            HistoryReadiness.Unavailable,
            fidelity,
            localCandidates.Select(item => new RepresentationEvidence(
                RepresentationEvidenceKind.LocalReplica,
                item.Diagnostic,
                DateTimeOffset.UtcNow,
                item.Availability,
                hadLocalPayload && context.Depth == AssessmentDepth.Deep
                    ? ReplicaIntegrityObservation.Corrupt
                    : ReplicaIntegrityObservation.Unknown)),
            new[] { hadLocalPayload
                ? "Every available local payload failed deep verification."
                : "No usable local or shared replica is known." }
                .Concat(failures).Concat(localCandidates.Where(c => c.Availability != ReplicaAvailabilityObservation.Available)
                    .Select(c => $"{c.ResolvedPath}: {c.Diagnostic}")));
    }

    protected static RepresentationAssessment? EvaluateDependencies(RepresentationAssessmentContext context)
    {
        foreach (var dependencyId in context.Representation.DependencyRepresentationIds)
        {
            if (!context.DependencyAssessments.TryGetValue(dependencyId, out var dependency))
            {
                return new RepresentationAssessment(
                    context.Representation.RepresentationId,
                    HistoryReadiness.Blocked,
                    GetEffectiveFidelity(context),
                    [],
                    [$"Dependency '{dependencyId}' is missing from the assessment graph."]);
            }

            if (dependency.Readiness == HistoryReadiness.PreparationRequired)
            {
                return new RepresentationAssessment(
                    context.Representation.RepresentationId,
                    HistoryReadiness.PreparationRequired,
                    GetEffectiveFidelity(context),
                    dependency.Evidence,
                    new[] { $"Dependency '{dependencyId}' requires preparation." }.Concat(dependency.Diagnostics));
            }

            if (dependency.Readiness != HistoryReadiness.Ready)
            {
                return new RepresentationAssessment(
                    context.Representation.RepresentationId,
                    HistoryReadiness.Blocked,
                    GetEffectiveFidelity(context),
                    dependency.Evidence,
                    new[] { $"Dependency '{dependencyId}' is not materializable." }.Concat(dependency.Diagnostics));
            }
        }

        return null;
    }

    protected static MaterializationFidelity GetEffectiveFidelity(RepresentationAssessmentContext context)
    {
        var fidelity = context.Representation.Fidelity;
        foreach (var dependencyId in context.Representation.DependencyRepresentationIds)
        {
            if (!context.DependencyAssessments.TryGetValue(dependencyId, out var dependency))
            {
                return MaterializationFidelity.Unknown;
            }

            // 根表示不能掩盖依赖闭包中更弱的保真度，否则会把不可精确恢复的链误报为 Exact。
            fidelity = Weakest(fidelity, dependency.Fidelity);
        }

        return fidelity;
    }

    private static MaterializationFidelity Weakest(
        MaterializationFidelity left,
        MaterializationFidelity right)
        => (MaterializationFidelity)Math.Max((int)left, (int)right);

    private static RepresentationAssessment Ready(
        VersionRepresentation representation,
        MaterializationFidelity fidelity,
        string path,
        ReplicaIntegrityObservation integrity,
        string detail)
        => new(
            representation.RepresentationId,
            HistoryReadiness.Ready,
            fidelity,
            [new RepresentationEvidence(
                integrity == ReplicaIntegrityObservation.Verified
                    ? RepresentationEvidenceKind.DeepVerification
                    : RepresentationEvidenceKind.LocalReplica,
                detail,
                DateTimeOffset.UtcNow,
                ReplicaAvailabilityObservation.Available,
                integrity)],
            [],
            path);
}

public sealed class SmartDeltaRepresentationHandler : CoreArchiveRepresentationHandler
{
    public SmartDeltaRepresentationHandler(IArchiveRepresentationBackend backend) : base(backend)
    {
    }

    public override bool CanHandle(VersionRepresentation representation)
        => representation.Kind == RepresentationKind.CoreSmartDelta;
}
