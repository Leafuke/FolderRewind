using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;

namespace FolderRewind.Tests;

internal static class HistoryFixture
{
    internal static readonly SourceId DefaultSource = SourceId.Parse("11111111-1111-1111-1111-111111111111");
    internal static IEnumerable<WorkspaceSourceBaseline> SourceStates(IEnumerable<WorkspaceSourceBaseline>? baselines,
        BranchId? branch, BranchUpdateId? update, CheckpointId? anchor)
        => (baselines ?? []).Select(state => state with { ActiveBranchId = branch, ActiveBranchUpdateId = update,
            CheckpointAncestryAnchorId = anchor });
}
