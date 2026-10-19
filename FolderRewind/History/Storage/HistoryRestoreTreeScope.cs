using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;

namespace FolderRewind.History.Storage;

internal sealed record RestoreTreeEntry(string RelativePath, string FullPath, bool Directory, bool Managed);

/// <summary>所有读取/计划/发布检查使用相同范围；排除节点仅保留存在标记，不进入其子树。</summary>
internal sealed class HistoryRestoreTreeScope
{
    private readonly HistoryRestoreSourceBinding _binding;
    private readonly PathRuleMatcher _filter;
    private readonly BackupSourceScopePatternSet? _scope;
    internal HistoryRestoreTreeScope(HistoryRestoreSourceBinding binding)
    {
        _binding = binding;
        _scope = binding.Boundary.ScopeMode == EffectiveBoundaryScopeMode.Include
            ? BackupSourceScopePatternSet.Compile(binding.Boundary.ScopeRules) : null;
        _filter = PathRuleMatcher.CreateForBackup(binding.Boundary.FilterRules,
            binding.ComparisonRoot, binding.ComparisonRoot, binding.Boundary.UseRegex);
    }
    internal bool Includes(string relative)
    {
        if (_scope is not null && !_scope.IsMatch(relative)) return false;
        var match = _filter.IsMatch(Path.Combine(_binding.ComparisonRoot, relative));
        return _binding.Boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist ? match : !match;
    }
    internal bool MayDescend(string relative)
    {
        if (_scope is not null && !_scope.MayContainDescendant(relative)) return false;
        return _binding.Boundary.FilterMode != EffectiveBoundaryFilterMode.Blacklist
            || !_filter.ExcludesWholeSubtree(Path.Combine(_binding.ComparisonRoot, relative));
    }
    internal IEnumerable<RestoreTreeEntry> Enumerate(string root, bool includeExcluded = false)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var parent))
            foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                bool managed = Includes(relative);
                bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
                bool descend = directory && MayDescend(relative);
                if (!managed && !descend)
                {
                    if (includeExcluded) yield return new(relative, entry.FullName, directory, false);
                    continue;
                }
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Managed restore path is an unsupported link: " + entry.FullName);
                if (managed || includeExcluded) yield return new(relative, entry.FullName, directory, managed);
                if (descend) pending.Push(entry.FullName);
            }
    }
}
