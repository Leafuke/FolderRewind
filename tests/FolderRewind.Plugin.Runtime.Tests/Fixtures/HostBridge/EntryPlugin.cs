using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace Fixture.HostBridge;

public sealed class EntryPlugin : IFolderRewindPlugin
{
    public ValueTask<PluginActivationResult> ActivateAsync(
        IPluginActivationContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginActivationResult.Empty);

    public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public void TouchRuntimeImplementation()
        => _ = new PluginRuntimeManager();
}
