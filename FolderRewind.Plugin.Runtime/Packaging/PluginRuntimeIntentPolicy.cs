using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public enum PluginRuntimeIntentAction
{
    None = 0,
    Activate = 1,
    Deactivate = 2
}

public readonly record struct PluginRuntimeIntentDecision(
    bool PersistIntent,
    PluginRuntimeIntentAction RuntimeAction);

public static class PluginRuntimeIntentPolicy
{
    public static PluginRuntimeIntentDecision Decide(
        bool requestedEnabled,
        bool currentEnabledIntent,
        PluginRuntimeState currentRuntimeState)
    {
        var runtimeAction = requestedEnabled
            ? currentRuntimeState == PluginRuntimeState.Active
                ? PluginRuntimeIntentAction.None
                : PluginRuntimeIntentAction.Activate
            : currentRuntimeState == PluginRuntimeState.Inactive
                ? PluginRuntimeIntentAction.None
                : PluginRuntimeIntentAction.Deactivate;

        return new PluginRuntimeIntentDecision(
            requestedEnabled != currentEnabledIntent,
            runtimeAction);
    }
}
