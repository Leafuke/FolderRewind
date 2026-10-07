using FolderRewind.Models;
using System;
using System.Threading;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services.Plugins.V3;

internal static class NativeHistoryArtifactTransformPolicy
{
    public const string BlockedDiagnosticCode = "plugin.artifact_transform_native_history_not_supported";

    public static bool MustBlock(ArtifactTransformPolicySettings? policy)
        => policy is not null && !IsExperimentalEnabled(policy);

    internal static bool IsIncremental(ArtifactTransformPolicySettings? policy)
        => policy?.Parameters?.TryGetValue("representation", out var value) == true
            && value.ValueKind == JsonValueKind.String && value.GetString() == "incremental-v1";

    internal static bool IsExperimentalEnabled(ArtifactTransformPolicySettings? policy)
        => policy?.ExperimentalEnabled == true && IsIncremental(policy)
            && !string.IsNullOrWhiteSpace(policy.Transformer?.PluginId)
            && !string.IsNullOrWhiteSpace(policy.Transformer?.TransformerId)
            && Enum.IsDefined(policy.FailureBehavior);

    internal static bool SupportsCapture(CoreCaptureMode mode, int sourceCount, bool pluginScopeSelected, bool hasPartialSource)
        => mode is CoreCaptureMode.Smart or CoreCaptureMode.Full && sourceCount > 0 && !pluginScopeSelected && !hasPartialSource;

    private static readonly AsyncLocal<string?> AcceptanceConfig = new();
    internal static IDisposable EnterAcceptance(string configId)
    {
        var previous = AcceptanceConfig.Value;
        AcceptanceConfig.Value = configId;
        return new Scope(() => AcceptanceConfig.Value = previous);
    }
    internal static bool IsEnabled(string configId) => AcceptanceConfig.Value == configId;
    internal static bool MustBlock(ArtifactTransformPolicySettings? policy, string configId)
        => MustBlock(policy) && !IsEnabled(configId);
    private sealed class Scope(Action exit) : IDisposable
    { public void Dispose() => exit(); }
}
