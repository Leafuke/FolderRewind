using FolderRewind.History.LocalState;

namespace FolderRewind.History.Merge;

internal static class MergeSessionActions
{
    internal static bool Allowed(string key, MergeSessionState? state, bool hasSource)
    {
        var active = state is MergeSessionState.Preparing or MergeSessionState.Resolving or MergeSessionState.Ready or MergeSessionState.Stale;
        return key switch
        {
            "Merge_New" => hasSource,
            "Merge_Apply" => state == MergeSessionState.Ready,
            "Merge_Recompute" or "Merge_Abandon" => active,
            "Merge_PrepareReplicas" => active && state != MergeSessionState.Stale,
            "Merge_Resume" => active || state == MergeSessionState.Applying,
            "Merge_Ours" or "Merge_Theirs" or "Merge_Manual" => state is MergeSessionState.Resolving or MergeSessionState.Ready,
            "Merge_PreviewBase" or "Merge_PreviewOurs" or "Merge_PreviewTheirs" => active,
            _ => state is not null
        };
    }
}
