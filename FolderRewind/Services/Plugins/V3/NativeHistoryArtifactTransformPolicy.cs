using FolderRewind.Models;

namespace FolderRewind.Services.Plugins.V3;

internal static class NativeHistoryArtifactTransformPolicy
{
    public const string BlockedDiagnosticCode = "plugin.artifact_transform_native_history_not_supported";

    public static bool MustBlock(ArtifactTransformPolicySettings? policy)
        => policy is not null;
}
