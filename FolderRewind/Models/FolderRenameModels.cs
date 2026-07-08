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
    public bool LocalBackupDirectoryMigrated { get; init; }
    public bool LocalMetadataDirectoryMigrated { get; init; }
}

public sealed class FolderMoveOperation
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required string Description { get; init; }
}
