using FolderRewind.History.Application;
using FolderRewind.History.Domain;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryRestoreWorkspacePlannerTests
{
    [TestMethod]
    public void TargetVolumePlanRejectsMissingParentsNestedSourcesAndHistoryOverlapWithoutCreatingAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-workspace-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "world");
            var plan = HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), [new(SourceId.New(), target)], []);
            Assert.AreEqual(FolderRewind.History.Storage.WindowsRestoreFileSystem.Identity(root), plan.Single().ParentIdentity);
            Assert.IsFalse(Path.Exists(plan.Single().Container)); Assert.IsFalse(Path.Exists(target));
            Directory.CreateDirectory(target);
            var trailing = HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), [new(SourceId.New(), target + Path.DirectorySeparatorChar)], []);
            Assert.AreEqual(target, trailing.Single().LogicalTarget);
            Assert.AreEqual(plan.Single().ParentIdentity, trailing.Single().ParentIdentity);
            Directory.Delete(target);
            Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(),
                [new(SourceId.New(), target), new(SourceId.New(), Path.Combine(target, "nested"))], []));
            Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), [new(SourceId.New(), target)], [root]));
            Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), [new(SourceId.New(), Path.Combine(root, "absent", "world"))], []));
            Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.Plan(HistoryTransactionId.New(), [new(SourceId.New(), target)], [], long.MaxValue));
            Assert.IsEmpty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
