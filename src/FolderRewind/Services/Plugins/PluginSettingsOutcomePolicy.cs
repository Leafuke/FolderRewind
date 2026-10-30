using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Services;

internal static class PluginSettingsOutcomePolicy
{
    public static SemanticStatus GetStatus(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Success => SemanticStatus.Success,
        OperationOutcome.SuccessWithWarnings => SemanticStatus.Warning,
        _ => SemanticStatus.Error
    };
}
