using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Settings;

public sealed record PluginSettingsApplyResult(
    PluginSettingsValidationResult Validation,
    PluginRuntimeTransitionResult? Transition)
{
    public bool Success => Validation.IsValid && Transition?.Success == true;
}
