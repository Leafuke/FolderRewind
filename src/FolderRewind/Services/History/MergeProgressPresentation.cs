using FolderRewind.History.Merge;

namespace FolderRewind.Services;

internal static class MergeProgressPresentation
{
    internal static string Text(MergePreparationProgress progress)
    {
        var stage = I18n.GetString("MergeProgress_" + progress.Stage);
        return progress.Total is > 0 ? I18n.Format("MergeProgress_Count", stage, progress.Completed, progress.Total.Value)
            : progress.Completed > 0 ? I18n.Format(progress.Stage == MergePreparationStage.AnalyzingWorld ? "MergeProgress_ReadFiles" : "MergeProgress_Items", stage, progress.Completed)
            : stage;
    }
}
