using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services;

internal sealed record SpatialWorkspaceInput(string Key, SpatialFixedInput? Fixed, bool Partial);

internal sealed class SpatialWorkspaceCoverage : ISpatialInputCoverage
{
    private readonly HashSet<string> _files;
    private readonly HistoryRestoreTreeScope? _scope;
    private readonly bool _known;
    internal SpatialWorkspaceCoverage(SourceVersion version, MergeTreeManifest tree)
    {
        _files = tree.Files.Keys.ToHashSet(StringComparer.Ordinal);
        _known = version.BoundaryConfidence != HistoricalBoundaryConfidence.Unknown
            && Path.IsPathFullyQualified(version.SourceDescriptorSnapshot.PathHint)
            && Enum.IsDefined(version.EffectiveSourceBoundary.ScopeMode) && Enum.IsDefined(version.EffectiveSourceBoundary.FilterMode);
        if (_known) _scope = new(new(version.SourceId, version.SourceDescriptorSnapshot.PathHint, version.EffectiveSourceBoundary));
    }
    public SpatialPathPresence InspectPath(string relativePath)
    {
        if (_files.Contains(relativePath)) return SpatialPathPresence.Present;
        if (!_known) return SpatialPathPresence.Unknown;
        SemanticMergeSourceView.Canonical(relativePath);
        return _scope!.Includes(relativePath) ? SpatialPathPresence.Absent : SpatialPathPresence.Filtered;
    }
    internal static string RootOf(MergeTreeManifest tree)
    {
        var item = tree.Files.FirstOrDefault();
        if (item.Value is null) throw new IOException(I18n.GetString("SpatialWorkspace_EmptyInput"));
        var root = item.Value.Handle[..^(item.Key.Replace('/', Path.DirectorySeparatorChar).Length)].TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(root) || tree.Files.Any(f => !string.Equals(Path.GetFullPath(Path.Combine(root, f.Key.Replace('/', Path.DirectorySeparatorChar))), f.Value.Handle, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Spatial preview requires one fixed input tree.");
        return root;
    }
}
