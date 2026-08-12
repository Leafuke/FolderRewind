using Fixture.PrivateDependency;
using FolderRewind.Plugin.Abstractions;

namespace Fixture.PluginOne;

public sealed class EntryPlugin : IFolderRewindPlugin
{
    public string DependencyVersion => DependencyMarker.Value;

    public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginActivationResult.Empty);

    public ValueTask DeactivateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
