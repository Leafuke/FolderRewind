using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Linq;

namespace FolderRewind.Services;

internal static class SpatialMergeRows
{
    internal static SpatialWorkspaceRow Create(MergeChangeRecord item, Func<LocalizedText, string> localize, Func<string, string> text)
    {
        var conflict = item.Conflict; var subject = conflict.SemanticSubject;
        var status = subject?.IsBlocked == true ? "Blocked" : subject is { IsAutomatic: true, CanOverrideAutomatic: false } ? "Locked"
            : item.Resolution is null ? "Unresolved" : item.Resolution.Choice == MergeResolutionChoice.Automatic ? "Automatic" : "Resolved";
        var directories = conflict.BaseDirectories.Concat(conflict.OursDirectories).Concat(conflict.TheirsDirectories).ToHashSet(StringComparer.Ordinal);
        var path = subject is not null ? localize(subject.DisplayName) : conflict.Subject.Paths.IsEmpty ? text("Merge_WholeSource")
            : string.Join("\n", conflict.Subject.Paths.Select(p => directories.Contains(p) ? p + "/" : p));
        var description = subject?.IsBlocked == true ? text("Merge_BlockedSubject") : item.Automatic ? text("MergeWorkspace_Automatic")
            : text("Merge_Conflict_" + conflict.Kind) + " · " + text(item.Resolution is null ? "MergeWorkspace_Unresolved" : "Merge_" + item.Resolution.Choice);
        return new(conflict.Id, path, subject is null ? description : localize(subject.Description) + "\n" + description, status,
            subject is not null && (!item.Automatic || subject.CanOverrideAutomatic) && !subject.IsBlocked, subject?.SpatialLocations ?? []);
    }
}
