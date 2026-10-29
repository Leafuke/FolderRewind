using FolderRewind.History.Merge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FolderRewind.Services;

/// <summary>Preserves the existing Source ceiling. Never approximates an intersection by a wider glob.</summary>
internal static class SourceScopeIntersection
{
    internal static IReadOnlyList<string> Intersect(IEnumerable<string> sourceRules, IEnumerable<string> pluginRules, CancellationToken token)
    {
        var ceiling = BackupSourceScopePatternSet.NormalizeAndValidate(sourceRules);
        var selected = BackupSourceScopePatternSet.NormalizeAndValidate(pluginRules);
        var matcher = BackupSourceScopePatternSet.Compile(ceiling);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in selected)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.IndexOfAny(['*', '?', '[']) < 0)
            { if (matcher.IsMatch(candidate)) result.Add(candidate); continue; }
            foreach (var original in ceiling)
            {
                if (original.IndexOfAny(['*', '?', '[']) < 0)
                { if (BackupSourceScopePatternSet.Compile([candidate]).IsMatch(original)) result.Add(original); continue; }
                if (original.Contains('[') || candidate.Contains('[')) throw Unsupported();
                var a = new MergePathLanguage(); a.AddGlob("", original);
                var b = new MergePathLanguage(); b.AddGlob("", candidate);
                try
                {
                    if (MergePathLanguage.Counterexample(a, b, false, token) is null) continue;
                    if (MergePathLanguage.Counterexample(a, b, true, token) is null) result.Add(original);
                    else if (MergePathLanguage.Counterexample(b, a, true, token) is null) result.Add(candidate);
                    else throw Unsupported();
                }
                catch (MergePathLanguage.LimitException) { throw Unsupported(); }
            }
        }
        if (result.Count == 0) throw new InvalidDataException(I18n.GetString("SpatialWorkspace_EmptyIntersection"));
        return BackupSourceScopePatternSet.NormalizeAndValidate(result);
    }
    private static InvalidDataException Unsupported() => new(I18n.GetString("SpatialWorkspace_ComplexIntersection"));
}
