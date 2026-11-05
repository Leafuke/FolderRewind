using FolderRewind.History.LocalState;
using System.Collections.Immutable;

namespace FolderRewind.History.Merge;

// Ephemeral notification of one persisted resolution transaction, never part of a saved plan.
internal sealed record MergeDecisionChange(MergeSession Before, MergeSession After, ImmutableArray<string> Ids)
{
    internal bool Matches(MergeSession? session) => session is not null && After.Id == session.Id
        && After.Plan.Revision == session.Plan.Revision && After.Revision == session.Revision && After.State == session.State;
}
