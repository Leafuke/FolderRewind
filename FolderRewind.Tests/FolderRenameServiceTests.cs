using FolderRewind.Models;

namespace FolderRewind.Services.Tests;

[TestClass]
[DoNotParallelize]
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

        ConfigService.CurrentConfig = new AppConfig();
        ConfigService.SaveResults.Clear();
        ConfigService.BeforeSave = null;
        HistoryService.SaveResults.Clear();
        HistoryService.GetEntriesForConfigCallCount = 0;
    }

    [TestMethod]
    public async Task ExecuteMovePlanMovesAllTypedDirectories()
    {
        string root = CreateRoot();
        string source = CreateDirectory(root, "source-old");
        string backup = CreateDirectory(root, "backup-old");
        string metadata = CreateDirectory(root, "metadata-old");
        string sourceNew = Path.Combine(root, "source-new");
        string backupNew = Path.Combine(root, "backup-new");
        string metadataNew = Path.Combine(root, "metadata-new");

        var result = await FolderRenameService.ExecuteMovePlanAsync(
        [
            Move(source, sourceNew, FolderMoveOperationKind.SourceFolder),
            Move(backup, backupNew, FolderMoveOperationKind.BackupDirectory),
            Move(metadata, metadataNew, FolderMoveOperationKind.MetadataDirectory)
        ]);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(Directory.Exists(sourceNew));
        Assert.IsTrue(Directory.Exists(backupNew));
        Assert.IsTrue(Directory.Exists(metadataNew));
    }

    [TestMethod]
    public async Task ExistingDestinationFailsBeforeAnyMove()
    {
        string root = CreateRoot();
        string source = CreateDirectory(root, "source");
        string destination = CreateDirectory(root, "destination");

        var result = await FolderRenameService.ExecuteMovePlanAsync(
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

    [TestMethod]
    public async Task ConfigSaveFailureRollsBackPathAndInMemoryReference()
    {
        var setup = CreateRenameSetup();
        ConfigService.SaveResults.Enqueue(new ConfigSaveResult
        {
            Success = false,
            ErrorMessage = "injected config failure"
        });
        ConfigService.SaveResults.Enqueue(new ConfigSaveResult { Success = true });

        var result = await FolderRenameService.RenameAsync(setup.Folder, "renamed");

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.RollbackSucceeded);
        Assert.IsTrue(Directory.Exists(setup.OldPath));
        Assert.IsFalse(Directory.Exists(setup.NewPath));
        Assert.AreEqual(setup.OldPath, setup.Folder.Path);
        Assert.AreEqual("world", setup.Folder.DisplayName);
    }

    [TestMethod]
    public async Task HistorySaveFailureRollsBackBeforeConfigIsPublished()
    {
        var setup = CreateRenameSetup();
        HistoryService.SaveResults.Enqueue(new HistorySaveResult
        {
            Success = false,
            ErrorMessage = "injected history failure"
        });
        HistoryService.SaveResults.Enqueue(new HistorySaveResult { Success = true });

        var result = await FolderRenameService.RenameAsync(setup.Folder, "renamed");

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.RollbackSucceeded);
        Assert.IsTrue(Directory.Exists(setup.OldPath));
        Assert.AreEqual(setup.OldPath, setup.Folder.Path);
    }

    [TestMethod]
    public async Task RollbackFailureIsReportedWithoutBeingSwallowed()
    {
        var setup = CreateRenameSetup();
        ConfigService.SaveResults.Enqueue(new ConfigSaveResult
        {
            Success = false,
            ErrorMessage = "injected config failure"
        });
        ConfigService.SaveResults.Enqueue(new ConfigSaveResult { Success = true });
        ConfigService.BeforeSave = () => Directory.CreateDirectory(setup.OldPath);

        var result = await FolderRenameService.RenameAsync(setup.Folder, "renamed");

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.RollbackSucceeded, result.Message);
        Assert.IsTrue(result.RollbackErrors.Any(error => error.Contains("occupied")));
        Assert.IsTrue(Directory.Exists(setup.NewPath));
    }

    [TestMethod]
    public void AtomicFileWriterReplacesExistingFileWithoutLeavingTemporaryFile()
    {
        string root = CreateRoot();
        string destination = Path.Combine(root, "state.json");
        File.WriteAllText(destination, "old");

        AtomicFileService.Write(destination, stream =>
        {
            using var writer = new StreamWriter(
                stream,
                System.Text.Encoding.UTF8,
                bufferSize: 1024,
                leaveOpen: true);
            writer.Write("new");
        });

        Assert.AreEqual("new", File.ReadAllText(destination));
        Assert.IsEmpty(Directory.GetFiles(root, "*.tmp"));
    }

    [TestMethod]
    public void CachedPreviewRevalidatesWithoutRescanningHistory()
    {
        var setup = CreateRenameSetup();
        var initial = FolderRenameService.PreviewRename(setup.Folder, "world");
        int callsAfterInitialPreview = HistoryService.GetEntriesForConfigCallCount;

        var updated = FolderRenameService.PreviewRenameWithCachedImpact(
            setup.Folder,
            "renamed",
            initial);

        Assert.IsTrue(updated.IsValid);
        Assert.AreEqual(
            callsAfterInitialPreview,
            HistoryService.GetEntriesForConfigCallCount);
        Assert.AreEqual(initial.AffectedConfigCount, updated.AffectedConfigCount);
    }

    [TestMethod]
    public void HistoryIdentityUpdateIsIsolatedByConfigAndOldStorageIdentity()
    {
        string oldPath = Path.Combine(CreateRoot(), "world");
        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "renamed");
        var firstConfig = new BackupConfig { Id = "config-a" };
        var secondConfig = new BackupConfig { Id = "config-b" };
        var references = new[]
        {
            Reference(
                firstConfig,
                oldPath,
                newPath,
                oldStorage: "world",
                newStorage: "renamed"),
            Reference(
                secondConfig,
                oldPath,
                newPath,
                oldStorage: "custom",
                newStorage: "custom")
        };

        Assert.IsTrue(FolderRenameService.TryResolveHistoryIdentityUpdate(
            "config-a",
            oldPath,
            "world",
            references,
            out string firstPath,
            out string firstName));
        Assert.AreEqual(newPath, firstPath);
        Assert.AreEqual("renamed", firstName);

        Assert.IsTrue(FolderRenameService.TryResolveHistoryIdentityUpdate(
            "config-b",
            oldPath,
            "world",
            references,
            out string secondPath,
            out string secondName));
        Assert.AreEqual(newPath, secondPath);
        Assert.AreEqual("world", secondName);

        Assert.IsFalse(FolderRenameService.TryResolveHistoryIdentityUpdate(
            "config-c",
            oldPath,
            "world",
            references,
            out _,
            out _));
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

    private (ManagedFolder Folder, string OldPath, string NewPath) CreateRenameSetup()
    {
        string root = CreateRoot();
        string oldPath = CreateDirectory(root, "world");
        string newPath = Path.Combine(root, "renamed");
        var folder = new ManagedFolder
        {
            Path = oldPath,
            DisplayName = "world"
        };
        var config = new BackupConfig
        {
            Id = "config-a",
            DestinationPath = Path.Combine(root, "backups"),
            SourceFolders = [folder]
        };
        ConfigService.CurrentConfig = new AppConfig
        {
            BackupConfigs = [config]
        };
        return (folder, oldPath, newPath);
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

    private static FolderRenameReferencePlan Reference(
        BackupConfig config,
        string oldPath,
        string newPath,
        string oldStorage,
        string newStorage)
    {
        var folder = new ManagedFolder
        {
            Path = oldPath,
            DisplayName = oldStorage
        };
        return new FolderRenameReferencePlan
        {
            ConfigId = config.Id,
            OldPath = oldPath,
            NewPath = newPath,
            OldDisplayName = oldStorage,
            NewDisplayName = newStorage,
            OldStorageFolderName = oldStorage,
            NewStorageFolderName = newStorage,
            Config = config,
            Folder = folder
        };
    }
}
