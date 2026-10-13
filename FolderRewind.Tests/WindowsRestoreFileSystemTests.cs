using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class WindowsRestoreFileSystemTests
{
    [TestMethod]
    public void AlternateDataStreamIsRejectedBeforeMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-ads-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "file"); File.WriteAllText(file, "world");
            File.WriteAllText(file + ":external", "keep");
            Assert.Throws<IOException>(() => WindowsRestoreFileSystem.Identity(file));
            Assert.AreEqual("world", File.ReadAllText(file)); Assert.AreEqual("keep", File.ReadAllText(file + ":external"));
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
