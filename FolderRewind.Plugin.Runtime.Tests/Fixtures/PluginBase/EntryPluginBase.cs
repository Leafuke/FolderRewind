using FolderRewind.Plugin.Abstractions;

namespace Fixture.PluginBase;

public abstract class EntryPluginBase : IFolderRewindPlugin
{
    public ValueTask<PluginActivationResult> ActivateAsync(
        IPluginActivationContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginActivationResult.Empty);

    public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
