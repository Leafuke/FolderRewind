using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services.Discovery;

public sealed class DiscoveredSourcePlan
{
    public required string FixedRoot { get; init; }
    public required string DisplayName { get; init; }
    public BackupSourceSelectionMode SelectionMode { get; init; }
    public IReadOnlyList<string> IncludePatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ResourceIds { get; init; } = Array.Empty<string>();
}

public static class DiscoveryResourcePlanner
{
    public static IReadOnlyList<DiscoveredSourcePlan> CreatePlans(
        IEnumerable<BackupResourceCandidate> resources,
        IReadOnlySet<string>? selectedResourceIds = null)
    {
        return (resources ?? Array.Empty<BackupResourceCandidate>())
            .Where(resource => resource.SupportState == BackupResourceSupportState.Supported
                               && !resource.IsSuppressed
                               && !string.IsNullOrWhiteSpace(resource.FixedRoot)
                               && (selectedResourceIds?.Contains(resource.ResourceId)
                                   ?? resource.IsSelectedByDefault))
            .GroupBy(resource => NormalizePath(resource.FixedRoot), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var grouped = group.ToList();
                var selectAll = grouped.Any(resource =>
                    resource.Kind == BackupResourceKind.Directory
                    && resource.IncludePatterns.Count == 0);
                return new DiscoveredSourcePlan
                {
                    FixedRoot = group.Key,
                    DisplayName = ResolveSourceDisplayName(group.Key, grouped),
                    SelectionMode = selectAll
                        ? BackupSourceSelectionMode.All
                        : BackupSourceSelectionMode.Include,
                    IncludePatterns = selectAll
                        ? Array.Empty<string>()
                        : grouped.SelectMany(resource => resource.IncludePatterns)
                            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    ResourceIds = grouped.Select(resource => resource.ResourceId)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            })
            .Where(plan => plan.SelectionMode == BackupSourceSelectionMode.All || plan.IncludePatterns.Count > 0)
            .OrderBy(plan => plan.FixedRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveSourceDisplayName(
        string fixedRoot,
        IReadOnlyList<BackupResourceCandidate> resources)
    {
        // ManagedFolder 的默认名称应描述实际备份目录；游戏名、标签等语义信息仍保留在发现候选中。
        // 根目录没有可用末级名称（例如卷根）时，才退回 Provider 提供的资源名称。
        var leafName = Path.GetFileName(
            fixedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !string.IsNullOrWhiteSpace(leafName)
            ? leafName
            : resources.Select(resource => resource.DisplayName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
              ?? "Game data";
    }

    public static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return (path ?? string.Empty).Trim().TrimEnd('\\', '/');
        }
    }
}
