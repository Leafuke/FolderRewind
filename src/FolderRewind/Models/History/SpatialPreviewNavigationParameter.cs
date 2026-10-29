using System;

namespace FolderRewind.Models;

public sealed record SpatialPreviewNavigationParameter(string ConfigId, Guid FolderId)
{
    public Guid? VersionId { get; init; }
}
