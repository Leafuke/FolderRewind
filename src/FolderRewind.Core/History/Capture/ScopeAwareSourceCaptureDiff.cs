using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FolderRewind.History.Capture;

public sealed record ScopeAwareSourceCaptureDiff(
    ImmutableArray<string> AddedFiles,
    ImmutableArray<string> ModifiedFiles,
    ImmutableArray<string> DeletedFiles)
{
    public bool HasChanges => !AddedFiles.IsEmpty || !ModifiedFiles.IsEmpty || !DeletedFiles.IsEmpty;

    public static ScopeAwareSourceCaptureDiff Compute(
        IReadOnlyDictionary<string, SourceCaptureFileState> exactParent,
        IReadOnlyDictionary<string, SourceCaptureFileState> currentWithinScope,
        Func<string, bool> isWithinCapturedScope)
    {
        ArgumentNullException.ThrowIfNull(exactParent);
        ArgumentNullException.ThrowIfNull(currentWithinScope);
        ArgumentNullException.ThrowIfNull(isWithinCapturedScope);
        var parentWithinScope = exactParent
            .Where(pair => isWithinCapturedScope(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var added = currentWithinScope.Keys
            .Where(path => !parentWithinScope.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var modified = currentWithinScope
            .Where(pair => parentWithinScope.TryGetValue(pair.Key, out var previous)
                && previous != pair.Value)
            .Select(pair => pair.Key)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var deleted = parentWithinScope.Keys
            .Where(path => !currentWithinScope.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new ScopeAwareSourceCaptureDiff(added, modified, deleted);
    }
}
