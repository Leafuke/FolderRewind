using System;
using System.Linq;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.History.Merge;

internal static class SemanticMergeDecisionPolicy
{
    internal static bool Allows(MergeSubject subject, MergeDecisionKind choice)
        => !subject.IsBlocked && Enum.IsDefined(choice)
            && (subject.IsAutomatic && choice == MergeDecisionKind.Automatic
                || (!subject.IsAutomatic || subject.CanOverrideAutomatic)
                    && choice != MergeDecisionKind.Automatic && subject.AllowedDecisions.Contains(choice));
}
