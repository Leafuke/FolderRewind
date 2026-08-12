using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Settings;

public sealed record PluginSettingsApplyResult(
    PluginSettingsValidationResult Validation,
    PluginRuntimeTransitionResult? Transition)
{
    public bool Success => Validation.IsValid && Transition?.Success == true;
}

public sealed class PluginSettingsTransactionCoordinator
{
    private readonly PluginRuntimeManager _runtime;

    public PluginSettingsTransactionCoordinator(PluginRuntimeManager runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async ValueTask<PluginSettingsApplyResult> ApplyAsync(
        PluginSettingsSchema schema,
        PluginActivationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(candidate);
        var validation = schema.Validate(candidate.Settings);
        if (!validation.IsValid)
        {
            return new PluginSettingsApplyResult(validation, null);
        }

        var normalizedCandidate = candidate with { Settings = validation.NormalizedSettings };
        var snapshot = _runtime.GetSnapshot(candidate.PluginId);
        var transition = snapshot.State == PluginRuntimeState.Active
            ? await _runtime.ReplaceAsync(normalizedCandidate, cancellationToken).ConfigureAwait(false)
            : await _runtime.ActivateAsync(normalizedCandidate, cancellationToken).ConfigureAwait(false);
        return new PluginSettingsApplyResult(validation, transition);
    }
}
