using FolderRewind.Models;

namespace FolderRewind.Services;

internal static class MigrationPresentationPolicy
{
    internal static bool CanReuseEditor(MigrationProjectPhase phase, string configurationSignature, MigrationWorkflowProject current)
        => phase == current.Phase && configurationSignature == current.ConfigurationSignature;

    internal static SemanticStatus StatusFor(MigrationWorkflowStage stage) => stage switch
    {
        MigrationWorkflowStage.Completed => SemanticStatus.Success,
        MigrationWorkflowStage.NeedsAttention or MigrationWorkflowStage.AwaitingPermissions
            or MigrationWorkflowStage.PartiallyCompleted => SemanticStatus.Warning,
        _ => SemanticStatus.Info
    };
}
