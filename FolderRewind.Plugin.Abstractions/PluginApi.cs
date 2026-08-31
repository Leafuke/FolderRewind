namespace FolderRewind.Plugin.Abstractions;

public readonly record struct PluginApiVersion(int Major, int Minor)
{
    public static PluginApiVersion HostVersion { get; } = new(3, 2);

    public bool IsSatisfiedBy(PluginApiVersion host)
        => Major == host.Major && host.Minor >= Minor;

    public override string ToString() => $"{Major}.{Minor}";
}

public enum PluginRuntimeState
{
    Inactive = 0,
    Activating = 1,
    Active = 2,
    Draining = 3,
    Deactivating = 4,
    Failed = 5
}

public enum OperationReadiness
{
    Ready = 0,
    Degraded = 1,
    Blocked = 2
}

public enum OperationOutcome
{
    Success = 0,
    SuccessWithWarnings = 1,
    NoChanges = 2,
    Canceled = 3,
    Failed = 4,
    Blocked = 5
}

public enum ConsistencyIntent
{
    Prefer = 0,
    Require = 1
}

public enum DiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record PluginDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Capability,
    string Owner,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record OperationResolution(
    OperationReadiness Readiness,
    IReadOnlyList<PluginDiagnostic> Diagnostics);
