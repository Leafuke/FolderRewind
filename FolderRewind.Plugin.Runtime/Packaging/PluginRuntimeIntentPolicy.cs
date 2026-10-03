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
    PluginRuntimeIntentAction RuntimeAction,
    bool ActivationDeferred = false);

public static class PluginRuntimeIntentPolicy
{
    public static PluginRuntimeIntentDecision Decide(
        bool requestedEnabled,
        bool currentEnabledIntent,
        PluginRuntimeState currentRuntimeState,
        bool currentRequiresRestart = false)
    {
        var activationDeferred = requestedEnabled && currentRequiresRestart;
        var runtimeAction = currentRequiresRestart
            ? PluginRuntimeIntentAction.None
            : requestedEnabled
                ? currentRuntimeState == PluginRuntimeState.Active
                    ? PluginRuntimeIntentAction.None
                    : PluginRuntimeIntentAction.Activate
                : currentRuntimeState == PluginRuntimeState.Inactive
                    ? PluginRuntimeIntentAction.None
                    : PluginRuntimeIntentAction.Deactivate;

        return new PluginRuntimeIntentDecision(
            requestedEnabled != currentEnabledIntent,
            runtimeAction,
            activationDeferred);
    }
}
