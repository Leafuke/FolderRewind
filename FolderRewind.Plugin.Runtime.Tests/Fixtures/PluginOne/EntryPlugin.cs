using Fixture.PrivateDependency;
using FolderRewind.Plugin.Abstractions;

namespace Fixture.PluginOne;

public sealed class EntryPlugin : IFolderRewindPlugin
{
    public EntryPlugin()
    {
        var marker = Environment.GetEnvironmentVariable("FOLDERREWIND_PLUGIN_TEST_MARKER");
        if (!string.IsNullOrWhiteSpace(marker)) File.WriteAllText(marker, "constructed");
    }

    public string DependencyVersion => DependencyMarker.Value;

    public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginActivationResult.Empty);

    public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
