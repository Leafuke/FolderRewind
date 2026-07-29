using System;
using System.Collections.Generic;

namespace FolderRewind.Models;

public sealed class FolderRenamePreview
{
    public bool IsValid { get; init; }
    public string Message { get; init; } = string.Empty;
    public string OldPath { get; init; } = string.Empty;
    public string NewPath { get; init; } = string.Empty;
    public string OldLeafName { get; init; } = string.Empty;
    public string NewLeafName { get; init; } = string.Empty;
    public string OldStorageFolderName { get; init; } = string.Empty;
    public string NewStorageFolderName { get; init; } = string.Empty;
    public int AffectedConfigCount { get; init; }
    public int AffectedHistoryCount { get; init; }
}

public sealed class FolderRenameResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string OldPath { get; init; } = string.Empty;
    public string NewPath { get; init; } = string.Empty;
    public int AffectedConfigCount { get; init; }
    public int AffectedHistoryCount { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public bool RollbackSucceeded { get; init; } = true;
    public IReadOnlyList<string> RollbackErrors { get; init; } = Array.Empty<string>();
}

public enum FolderMoveOperationKind
{
    SourceFolder,
    BackupDirectory,
    MetadataDirectory
}

public sealed class FolderMoveOperation
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required FolderMoveOperationKind Kind { get; init; }
    public string ConfigId { get; init; } = string.Empty;
}

internal sealed class FolderRenameReferencePlan
{
    public required string ConfigId { get; init; }
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public required string OldDisplayName { get; init; }
    public required string NewDisplayName { get; init; }
    public required string OldStorageFolderName { get; init; }
    public required string NewStorageFolderName { get; init; }
    internal required BackupConfig Config { get; init; }
    internal required ManagedFolder Folder { get; init; }
}

internal sealed record HistoryFolderIdentitySnapshot(
    HistoryItem Item,
    string FolderPath,
    string FolderName);

internal sealed class HistoryFolderIdentityUpdate
{
    public int UpdatedCount { get; init; }
    public IReadOnlyList<HistoryFolderIdentitySnapshot> Snapshots { get; init; }
        = Array.Empty<HistoryFolderIdentitySnapshot>();
}
