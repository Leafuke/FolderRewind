using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Operations;

public enum PluginOperationKind
{
    Backup = 0,
    Restore = 1
}

public sealed record PluginOperationResolutionRequest(
    ConfigKindDeclaration Kind,
    PluginOperationKind Operation,
    PluginRuntimeState? RuntimeState,
    bool ProviderScopeSelected,
    bool ScopeCapabilityAvailable,
    ConsistencyIntent ConsistencyIntent,
    bool ConsistencyCapabilityAvailable,
    bool RestoreCoordinatorAvailable);

public static class PluginOperationResolver
{
    private const string CoreOwnerId = "folderrewind.core";

    public static OperationResolution Resolve(PluginOperationResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<PluginDiagnostic>();
        var isCore = string.Equals(
            request.Kind.Kind.OwnerId.Value,
            CoreOwnerId,
            StringComparison.Ordinal);
        var runtimeActive = request.RuntimeState == PluginRuntimeState.Active;

        if (request.Operation == PluginOperationKind.Restore)
        {
            if (!isCore && !runtimeActive)
            {
                diagnostics.Add(Diagnostic(
                    "plugin.restore_owner_unavailable",
                    DiagnosticSeverity.Error,
                    request,
                    ("runtimeState", request.RuntimeState?.ToString() ?? "Missing")));
                return new OperationResolution(OperationReadiness.Blocked, diagnostics);
            }

            if (request.Kind.RestoreCoordination == RestoreCoordinationPolicy.Required
                && !request.RestoreCoordinatorAvailable)
            {
                diagnostics.Add(Diagnostic(
                    "plugin.restore_coordinator_unavailable",
                    DiagnosticSeverity.Error,
                    request));
                return new OperationResolution(OperationReadiness.Blocked, diagnostics);
            }

            return new OperationResolution(OperationReadiness.Ready, diagnostics);
        }

        if (request.ProviderScopeSelected && (!runtimeActive || !request.ScopeCapabilityAvailable))
        {
            diagnostics.Add(Diagnostic(
                "plugin.backup_scope_unavailable",
                DiagnosticSeverity.Error,
                request));
            return new OperationResolution(OperationReadiness.Blocked, diagnostics);
        }

        if (request.ConsistencyIntent == ConsistencyIntent.Require
            && (!runtimeActive || !request.ConsistencyCapabilityAvailable))
        {
            diagnostics.Add(Diagnostic(
                "plugin.backup_consistency_required",
                DiagnosticSeverity.Error,
                request));
            return new OperationResolution(OperationReadiness.Blocked, diagnostics);
        }

        if (!isCore && (!runtimeActive || !request.ConsistencyCapabilityAvailable))
        {
            if (request.Kind.BackupFallback == BackupFallbackPolicy.RawWithWarnings)
            {
                diagnostics.Add(Diagnostic(
                    "plugin.backup_raw_fallback",
                    DiagnosticSeverity.Warning,
                    request,
                    ("runtimeState", request.RuntimeState?.ToString() ?? "Missing")));
                return new OperationResolution(OperationReadiness.Degraded, diagnostics);
            }

            diagnostics.Add(Diagnostic(
                "plugin.backup_owner_unavailable",
                DiagnosticSeverity.Error,
                request,
                ("runtimeState", request.RuntimeState?.ToString() ?? "Missing")));
            return new OperationResolution(OperationReadiness.Blocked, diagnostics);
        }

        return new OperationResolution(OperationReadiness.Ready, diagnostics);
    }

    public static OperationOutcome Complete(
        OperationResolution resolution,
        OperationOutcome coreOutcome,
        IEnumerable<PluginDiagnostic>? completionDiagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Readiness == OperationReadiness.Blocked)
        {
            return OperationOutcome.Blocked;
        }

        var hasWarnings = resolution.Readiness == OperationReadiness.Degraded
            || resolution.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            || (completionDiagnostics?.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning) ?? false);
        return coreOutcome switch
        {
            OperationOutcome.Success when hasWarnings => OperationOutcome.SuccessWithWarnings,
            OperationOutcome.SuccessWithWarnings => OperationOutcome.SuccessWithWarnings,
            _ => coreOutcome
        };
    }

    private static PluginDiagnostic Diagnostic(
        string code,
        DiagnosticSeverity severity,
        PluginOperationResolutionRequest request,
        params (string Key, string Value)[] arguments)
        => new(
            code,
            severity,
            request.Operation.ToString(),
            request.Kind.Kind.OwnerId.Value,
            arguments.ToDictionary(argument => argument.Key, argument => argument.Value, StringComparer.Ordinal));
}
