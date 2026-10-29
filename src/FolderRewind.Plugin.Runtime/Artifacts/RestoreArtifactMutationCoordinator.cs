using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record RestoreArtifactMutationResult(
    OperationOutcome Outcome,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    bool TargetMutationStarted);

/// <summary>
/// Materializes a verified Artifact graph before opening the once-only Host
/// target mutation continuation. The materializer never receives the target.
/// </summary>
public sealed class RestoreArtifactMutationCoordinator
{
    private readonly RestoreMaterializationCoordinator _materialization;

    public RestoreArtifactMutationCoordinator(RestoreMaterializationCoordinator materialization)
        => _materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));

    public async ValueTask<RestoreArtifactMutationResult> ExecuteAsync(
        ConfigSnapshot config,
        FolderSnapshot folder,
        string versionId,
        ArtifactId artifactRootId,
        RestoreMode requestedMode,
        RestoreMode effectiveMode,
        string workspaceRoot,
        Func<string, CancellationToken, ValueTask<OperationOutcome>> mutateTargetAsync,
        ArtifactTransformLimits? limits = null,
        IPluginOperationProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutateTargetAsync);
        await using var materialized = await _materialization.MaterializeAsync(
            config,
            folder,
            versionId,
            artifactRootId,
            requestedMode,
            effectiveMode,
            workspaceRoot,
            limits,
            progress,
            cancellationToken).ConfigureAwait(false);
        if (materialized.Outcome is not OperationOutcome.Success and not OperationOutcome.SuccessWithWarnings)
        {
            return new RestoreArtifactMutationResult(materialized.Outcome, materialized.Diagnostics, false);
        }
        if (materialized.WorkspacePath is null)
        {
            throw new InvalidOperationException("Successful materialization did not produce a Host workspace.");
        }

        var gate = new RestoreMutationContinuationGate(token => mutateTargetAsync(materialized.WorkspacePath, token));
        var mutationOutcome = await gate.InvokeAsync(cancellationToken).ConfigureAwait(false);
        var outcome = mutationOutcome == OperationOutcome.Success
                      && materialized.Outcome == OperationOutcome.SuccessWithWarnings
            ? OperationOutcome.SuccessWithWarnings
            : mutationOutcome;
        return new RestoreArtifactMutationResult(outcome, materialized.Diagnostics, gate.WasInvoked);
    }
}
