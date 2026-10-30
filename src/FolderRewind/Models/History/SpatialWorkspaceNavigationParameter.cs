using System;

namespace FolderRewind.Models;

public enum SpatialWorkspaceMode { Compare, Merge, Scope }
public sealed record SpatialWorkspaceNavigationParameter(string ConfigId, Guid FolderId, SpatialWorkspaceMode Mode)
{
    public Guid? VersionId { get; init; }
    public Guid? SessionId { get; init; }
}
