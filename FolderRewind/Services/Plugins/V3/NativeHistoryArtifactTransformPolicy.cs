using FolderRewind.Models;
using System;
using System.Threading;

namespace FolderRewind.Services.Plugins.V3;

internal static class NativeHistoryArtifactTransformPolicy
{
    public const string BlockedDiagnosticCode = "plugin.artifact_transform_native_history_not_supported";

    public static bool MustBlock(ArtifactTransformPolicySettings? policy)
        => policy is not null;

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
