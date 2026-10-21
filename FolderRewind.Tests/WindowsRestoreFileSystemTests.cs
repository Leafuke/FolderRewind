using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class WindowsRestoreFileSystemTests
{
    [TestMethod]
    public void CleanupRemovesOwnedNamesWhileOtherReadHandlesRemainUsable()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-open-cleanup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var container = Path.Combine(root, "owned"); Directory.CreateDirectory(container);
        var file = Path.Combine(container, "file"); File.WriteAllText(file, "original bytes");
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            WindowsRestoreFileSystem.DeleteOwned(file, WindowsRestoreFileSystem.Identity(file));
            Assert.IsEmpty(Directory.GetFileSystemEntries(container), "A pending name must not obstruct cleanup of its owned container.");
            WindowsRestoreFileSystem.DeleteOwned(container, WindowsRestoreFileSystem.Identity(container));
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
            Assert.AreEqual((int)'o', input.ReadByte(), "The existing handle retains its original data.");
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void CachedCleanupParentsBlockRenameAndRejectReplacementOrUnprotectedParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-cleanup-parents-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "file"); File.WriteAllText(file, "original");
            var original = WindowsRestoreFileSystem.Identity(file);
            File.Move(file, Path.Combine(root, "kept")); File.WriteAllText(file, "external");
            using (var parents = new WindowsRestoreFileSystem.RestoreParentHandles([(root, WindowsRestoreFileSystem.Identity(root))]))
            {
                Assert.Throws<IOException>(() => Directory.Move(root, root + "-renamed"));
                Assert.Throws<IOException>(() => WindowsRestoreFileSystem.Open(root, 0x40000000, 7));
                Assert.Throws<IOException>(() => WindowsRestoreFileSystem.DeleteOwned(file, original, parents));
                Assert.AreEqual("external", File.ReadAllText(file));
                var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
                var other = Path.Combine(child, "other"); File.WriteAllText(other, "unprotected");
                Assert.Throws<IOException>(() => WindowsRestoreFileSystem.DeleteOwned(other, WindowsRestoreFileSystem.Identity(other), parents));
                WindowsRestoreFileSystem.DeleteOwned(file, WindowsRestoreFileSystem.Identity(file), parents);
                Assert.IsFalse(File.Exists(file)); Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "kept")));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(root + "-renamed")) Directory.Delete(root + "-renamed", true);
        }
    }
    [TestMethod]
    public void AncestorLeaseProtectsAnEmptyDirectoryAgainstRename()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-empty-parent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var parent = Path.Combine(root, "parent"); Directory.CreateDirectory(parent);
        try
        {
            using var lease = new WindowsDirectoryReadLease(Path.Combine(parent, "not-yet-created"));
            Assert.Throws<IOException>(() => Directory.Move(parent, Path.Combine(root, "renamed")));
            Assert.IsTrue(Directory.Exists(parent));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void AlternateDataStreamIsAllowedAndMovesWithOriginalObject()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-ads-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "file"); File.WriteAllText(file, "world");
            File.WriteAllText(file + ":external", "keep");
            var identity = WindowsRestoreFileSystem.Identity(file);
            var retired = Path.Combine(root, "retired");
            WindowsRestoreFileSystem.MoveOwnedNoReplace(file, retired, identity);
            Assert.AreEqual("world", File.ReadAllText(retired)); Assert.AreEqual("keep", File.ReadAllText(retired + ":external"));
            WindowsRestoreFileSystem.MoveOwnedNoReplace(retired, file, identity);
            Assert.AreEqual("keep", File.ReadAllText(file + ":external"));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NonemptyDirectoryMoveRejectsOpenChildrenWithoutDroppingTheirProtection(bool holdRead)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-nonempty-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var file = Path.Combine(source, "file"); File.WriteAllText(file, "value");
            using var read = holdRead ? WindowsRestoreFileSystem.Open(file, 0x80, 7) : null;
            if (holdRead)
            {
                Assert.Throws<IOException>(() => WindowsRestoreFileSystem.MoveOwnedNoReplace(source, Path.Combine(root, "moved"), WindowsRestoreFileSystem.Identity(source)));
                Assert.AreEqual("value", File.ReadAllText(file));
            }
            else
            {
                WindowsRestoreFileSystem.MoveOwnedNoReplace(source, Path.Combine(root, "moved"), WindowsRestoreFileSystem.Identity(source));
                Assert.AreEqual("value", File.ReadAllText(Path.Combine(root, "moved", "file")));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void ExclusiveDirectoryCreationReturnsOwnedObjectAndRejectsExistingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-exclusive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "container"); var parent = WindowsRestoreFileSystem.Identity(root);
            var created = WindowsRestoreFileSystem.CreateDirectoryNew(path, parent);
            using (created.Handle)
            {
                Assert.AreEqual(created.Identity, WindowsRestoreFileSystem.Identity(path));
                Assert.Throws<IOException>(() => WindowsRestoreFileSystem.CreateDirectoryNew(path, parent));
                Assert.Throws<IOException>(() => Directory.Move(path, Path.Combine(root, "foreign")));
            }
            WindowsRestoreFileSystem.DeleteOwned(path, created.Identity);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HandleRenamePreservesIdentityAndNeverReplacesForeignDestination(bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-owned-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
            if (directory) Directory.CreateDirectory(source); else File.WriteAllText(source, "original");
            var id = WindowsRestoreFileSystem.Identity(source);
            File.WriteAllText(destination, "foreign");
            Assert.Throws<IOException>(() => WindowsRestoreFileSystem.MoveOwnedNoReplace(source, destination, id));
            Assert.AreEqual(id, WindowsRestoreFileSystem.Identity(source)); Assert.AreEqual("foreign", File.ReadAllText(destination));
            File.Delete(destination);
            WindowsRestoreFileSystem.MoveOwnedNoReplace(source, destination, id);
            Assert.AreEqual(id, WindowsRestoreFileSystem.Identity(destination));
            WindowsRestoreFileSystem.MoveOwnedNoReplace(destination, source, id);
            WindowsRestoreFileSystem.DeleteOwned(source, id);
            Assert.IsFalse(Path.Exists(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SourceReplacementAndActiveWriterAreBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-owned-replacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "file"); var dest = Path.Combine(root, "dest");
            File.WriteAllText(source, "old"); var id = WindowsRestoreFileSystem.Identity(source);
            File.Move(source, Path.Combine(root, "kept-original")); File.WriteAllText(source, "foreign");
            Assert.Throws<IOException>(() => WindowsRestoreFileSystem.MoveOwnedNoReplace(source, dest, id));
            var foreignId = WindowsRestoreFileSystem.Identity(source);
            using (var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.Read))
                Assert.Throws<IOException>(() => WindowsRestoreFileSystem.MoveOwnedNoReplace(source, dest, foreignId));
            Assert.AreEqual("foreign", File.ReadAllText(source));
        }
        finally { Directory.Delete(root, true); }
    }
}
