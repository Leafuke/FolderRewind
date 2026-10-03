using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

public sealed record PluginActivationCandidate(
    PluginId PluginId,
    Func<IFolderRewindPlugin> Factory,
    PluginSettingsSnapshot Settings,
    IReadOnlyList<ConfigSnapshot> Configs,
    IPluginHostServices HostServices,
    IPluginActivationStore Store,
    PluginManifestContract? Manifest = null);

public sealed record PluginActivationCommit(
    PluginId PluginId,
    PluginSettingsSnapshot Settings,
    IReadOnlyList<ProviderStatePatch> ProviderStatePatches);

/// <summary>
/// The Host implementation must commit settings and state patches as one
/// transaction or leave the prior durable state unchanged.
/// </summary>
public interface IPluginActivationStore
{
    ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken cancellationToken);
}

public sealed record PluginRuntimeTransitionResult(
    bool Success,
    OperationOutcome Outcome,
    PluginRuntimeState State,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    bool RequiresRestart = false)
{
    public static PluginRuntimeTransitionResult Completed(
        PluginRuntimeState state,
        IReadOnlyList<PluginDiagnostic>? diagnostics = null,
        bool requiresRestart = false)
        => new(true, OperationOutcome.Success, state, diagnostics ?? Array.Empty<PluginDiagnostic>(), requiresRestart);

    public static PluginRuntimeTransitionResult Rejected(
        OperationOutcome outcome,
        PluginRuntimeState state,
        params PluginDiagnostic[] diagnostics)
        => new(false, outcome, state, diagnostics);
}

public sealed record PluginRuntimeSnapshot(
    PluginId PluginId,
    PluginRuntimeState State,
    int ActiveLeases,
    string LastError,
    bool RequiresRestart = false);

public static class SafeModePolicy
{
    public static bool IsRequested(IEnumerable<string> arguments)
        => arguments.Any(argument => string.Equals(argument, "--safe-mode", StringComparison.OrdinalIgnoreCase));
}

internal static class RuntimeDiagnostic
{
    public static PluginDiagnostic Error(string code, PluginId pluginId, string message)
        => Create(code, DiagnosticSeverity.Error, pluginId, message);

    public static PluginDiagnostic Warning(string code, PluginId pluginId, string message)
        => Create(code, DiagnosticSeverity.Warning, pluginId, message);

    private static PluginDiagnostic Create(string code, DiagnosticSeverity severity, PluginId pluginId, string message)
        => new(
            code,
            severity,
            "Runtime",
            pluginId.Value,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = message });
}
