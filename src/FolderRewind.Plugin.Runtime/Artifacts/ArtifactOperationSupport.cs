using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed record ArtifactTransformLimits(int MaximumFiles, long MaximumBytes)
{
    public static ArtifactTransformLimits Default { get; } = new(100_000, 16L * 1024 * 1024 * 1024);
}

public sealed class NullPluginOperationProgress : IPluginOperationProgress
{
    public static NullPluginOperationProgress Instance { get; } = new();
    private NullPluginOperationProgress() { }
    public void Report(PluginProgress progress) { }
}
