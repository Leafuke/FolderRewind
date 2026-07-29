using FolderRewind.Models;

namespace FolderRewind.Services.Tests;

[TestClass]
public sealed class FolderRenameServiceTests
{
    private readonly List<string> _temporaryRoots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string root in _temporaryRoots.OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ExecuteMovePlanMovesAllTypedDirectories()
    {
        string root = CreateRoot();
        string source = CreateDirectory(root, "source-old");
        string backup = CreateDirectory(root, "backup-old");
        string metadata = CreateDirectory(root, "metadata-old");
        string sourceNew = Path.Combine(root, "source-new");
        string backupNew = Path.Combine(root, "backup-new");
        string metadataNew = Path.Combine(root, "metadata-new");

        var result = FolderRenameService.ExecuteMovePlan(
        [
            Move(source, sourceNew, FolderMoveOperationKind.SourceFolder),
            Move(backup, backupNew, FolderMoveOperationKind.BackupDirectory),
            Move(metadata, metadataNew, FolderMoveOperationKind.MetadataDirectory)
        ]);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.LocalBackupDirectoryMigrated);
        Assert.IsTrue(result.LocalMetadataDirectoryMigrated);
        Assert.IsTrue(Directory.Exists(sourceNew));
        Assert.IsTrue(Directory.Exists(backupNew));
        Assert.IsTrue(Directory.Exists(metadataNew));
    }

    [TestMethod]
    public void ExistingDestinationFailsBeforeAnyMove()
    {
        string root = CreateRoot();
        string source = CreateDirectory(root, "source");
        string destination = CreateDirectory(root, "destination");

        var result = FolderRenameService.ExecuteMovePlan(
        [
            Move(source, destination, FolderMoveOperationKind.SourceFolder)
        ]);

        Assert.IsFalse(result.Success);
        Assert.IsNotEmpty(result.Conflicts);
        Assert.IsTrue(Directory.Exists(source));
    }

    [TestMethod]
    public void DuplicateTargetsAndMoveChainsAreRejected()
    {
        string root = CreateRoot();
        string first = CreateDirectory(root, "first");
        string second = CreateDirectory(root, "second");
        string sharedTarget = Path.Combine(root, "target");

        var duplicateTarget = FolderRenameService.ValidateMovePlan(
        [
            Move(first, sharedTarget, FolderMoveOperationKind.SourceFolder),
            Move(second, sharedTarget, FolderMoveOperationKind.BackupDirectory)
        ]);
        Assert.IsTrue(duplicateTarget.Any(message => message.Contains("same rename destination")));

        string third = CreateDirectory(root, "third");
        string fourth = Path.Combine(root, "fourth");
        var chain = FolderRenameService.ValidateMovePlan(
        [
            Move(third, fourth, FolderMoveOperationKind.SourceFolder),
            Move(fourth, Path.Combine(root, "fifth"), FolderMoveOperationKind.BackupDirectory)
        ]);
        Assert.IsNotEmpty(chain);
    }

    [TestMethod]
    public void DisplayAndHistoryNamesOnlyChangeWhenTheyMatchOldIdentity()
    {
        Assert.AreEqual(
            "renamed",
            FolderRenameService.ResolveUpdatedDisplayName("old", "old", "renamed"));
        Assert.AreEqual(
            "custom",
            FolderRenameService.ResolveUpdatedDisplayName("custom", "old", "renamed"));
        Assert.AreEqual(
            "new-storage",
            FolderRenameService.ResolveUpdatedHistoryFolderName(
                "old-storage",
                "old-storage",
                "new-storage"));
        Assert.AreEqual(
            "custom-storage",
            FolderRenameService.ResolveUpdatedHistoryFolderName(
                "custom-storage",
                "old-storage",
                "new-storage"));
    }

    private string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "FolderRewindRenameTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _temporaryRoots.Add(root);
        return root;
    }

    private static string CreateDirectory(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static FolderMoveOperation Move(
        string source,
        string destination,
        FolderMoveOperationKind kind)
        => new()
        {
            SourcePath = source,
            DestinationPath = destination,
            Kind = kind
        };
}
