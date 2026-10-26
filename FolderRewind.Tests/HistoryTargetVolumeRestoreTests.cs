using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;
using System.Security.AccessControl;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryTargetVolumeRestoreTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TypedStructuralConflictNeverReplacesAnExcludedObject(bool excludedDirectory)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var name = excludedDirectory ? "voxy" : "session.lock";
        var boundary = EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist,
            [new(name, excludedDirectory ? PathFilterTarget.Directory : PathFilterTarget.File)], false);
        var node = await f.AddAsync(null, root =>
        {
            if (excludedDirectory) File.WriteAllText(Path.Combine(root, name), "new file");
            else { Directory.CreateDirectory(Path.Combine(root, name)); File.WriteAllText(Path.Combine(root, name, "new"), "new"); }
        }, boundary: boundary);
        var target = Path.Combine(f.Root, "typed-conflict"); Directory.CreateDirectory(target);
        var protectedPath = Path.Combine(target, name);
        if (excludedDirectory) Directory.CreateDirectory(protectedPath);
        var content = excludedDirectory ? Path.Combine(protectedPath, "keep") : protectedPath;
        File.WriteAllText(content, "protected");
        var identity = WindowsRestoreFileSystem.Identity(content);
        using var context = new FolderRewind.History.Representation.ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, f.Engine,
            async token => new FolderRewind.History.Representation.RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, AllowDirectorySwap = false, VerificationContext = context };
        var result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target, boundary),
            (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
        Assert.IsFalse(result.Succeeded, "A file/directory replacement must not consume an excluded object.");
        Assert.AreEqual("protected", File.ReadAllText(content));
        Assert.AreEqual(identity, WindowsRestoreFileSystem.Identity(content));
    }
    [TestMethod]
    public async Task TypedFiltersPreserveExcludedFilesButRestoreSameNamedDirectoriesAndOrdinaryVoxyFiles()
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var boundary = EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist,
            [new("session.lock", PathFilterTarget.File), new("voxy", PathFilterTarget.Directory)], false);
        var node = await f.AddAsync(null, root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "nested", "session.lock"));
            File.WriteAllText(Path.Combine(root, "nested", "session.lock", "data"), "restored");
            Directory.CreateDirectory(Path.Combine(root, "empty", "session.lock"));
            File.WriteAllText(Path.Combine(root, "voxy"), "ordinary file");
        }, boundary: boundary);
        var target = Path.Combine(f.Root, "typed-target"); Directory.CreateDirectory(Path.Combine(target, "nested", "voxy"));
        File.WriteAllText(Path.Combine(target, "session.lock"), "keep lock");
        File.WriteAllText(Path.Combine(target, "nested", "voxy", "cache"), "keep cache");
        File.WriteAllText(Path.Combine(target, "stale"), "remove");
        var lockIdentity = WindowsRestoreFileSystem.Identity(Path.Combine(target, "session.lock"));
        using var context = new FolderRewind.History.Representation.ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, f.Engine,
            async token => new FolderRewind.History.Representation.RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, AllowDirectorySwap = false, VerificationContext = context };
        var result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target, boundary),
            (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual(lockIdentity, WindowsRestoreFileSystem.Identity(Path.Combine(target, "session.lock")));
        Assert.AreEqual("keep cache", File.ReadAllText(Path.Combine(target, "nested", "voxy", "cache")));
        Assert.AreEqual("restored", File.ReadAllText(Path.Combine(target, "nested", "session.lock", "data")));
        Assert.IsTrue(Directory.Exists(Path.Combine(target, "empty", "session.lock")));
        Assert.AreEqual("ordinary file", File.ReadAllText(Path.Combine(target, "voxy")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "stale")));
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalManagedFileCannotBeReportedAsCommittedByFileDelta(bool afterCommit)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var target = Path.Combine(f.Root, "target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "original"), "keep");
        var originalIdentity = WindowsRestoreFileSystem.Identity(Path.Combine(target, "original"));
        try
        {
            HistoryRestoreFileSystemPlanStore.TestFault = point => { if (point == (afterCommit ? "after-workspace" : "after-move-0")) File.WriteAllText(Path.Combine(target, "external"), "foreign"); };
            var result = await f.Restore(targetVolume: true, allowSwap: false).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                new(f.Source, target), f.Before, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(afterCommit ? HistoryRestoreStatus.CommittedRecoveryRequired : HistoryRestoreStatus.MutationFailedRolledBack, result.Status, result.Diagnostic);
            Assert.AreEqual("keep", File.ReadAllText(afterCommit ? Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories)
                .Single(path => WindowsRestoreFileSystem.Identity(path) == originalIdentity) : Path.Combine(target, "original")));
            Assert.AreEqual("foreign", File.ReadAllText(Path.Combine(target, "external")));
            Assert.AreEqual(afterCommit, File.Exists(Path.Combine(target, "a.txt")));
        }
        finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultipleSourcesCommitTogetherOrRollbackTogether(bool failSecond)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var other = SourceId.New();
        var first = await f.AddAsync(null, dir => File.WriteAllText(Path.Combine(dir, "file"), "first-new"));
        var second = await f.AddAsync(null, dir => File.WriteAllText(Path.Combine(dir, "file"), "second-new"), source: other);
        var targets = new[] { Path.Combine(f.Root, "first"), Path.Combine(f.Root, "second") };
        foreach (var target in targets) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "file"), "original"); }
        using var verification = new FolderRewind.History.Representation.ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, f.Engine,
            async token => new FolderRewind.History.Representation.RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, VerificationContext = verification };
        try
        {
            if (failSecond) HistoryRestoreFileSystemPlanStore.TestFault = point => { if (point == "before-move-3") throw new IOException("second source fault"); };
            var result = await restore.RestoreVersionsAsync(new Dictionary<SourceId, VersionId> { [f.Source] = first.Version.VersionId, [other] = second.Version.VersionId },
                [new(f.Source, targets[0]), new(other, targets[1])], (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(failSecond ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreEqual(failSecond ? "original" : "first-new", File.ReadAllText(Path.Combine(targets[0], "file")));
            Assert.AreEqual(failSecond ? "original" : "second-new", File.ReadAllText(Path.Combine(targets[1], "file")));
        }
        finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
    }
    [TestMethod]
    [DataRow("file-to-dir")]
    [DataRow("dir-to-file")]
    [DataRow("obsolete-dir")]
    [DataRow("empty-dir")]
    [DataRow("readonly")]
    public async Task FileDeltaHandlesStructuralChangesAndReadonlyRollback(string scenario)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var node = await f.AddAsync(null, dir => {
            if (scenario is "file-to-dir" or "empty-dir") Directory.CreateDirectory(Path.Combine(dir, "shape"));
            if (scenario == "file-to-dir") File.WriteAllText(Path.Combine(dir, "shape", "new"), "new");
            else if (scenario != "empty-dir") File.WriteAllText(Path.Combine(dir, "shape"), "new");
        });
        var target = Path.Combine(f.Root, "target"); Directory.CreateDirectory(target);
        if (scenario is "dir-to-file" or "obsolete-dir")
        {
            var old = Path.Combine(target, scenario == "obsolete-dir" ? "obsolete/sub" : "shape");
            Directory.CreateDirectory(old); File.WriteAllText(Path.Combine(old, "old"), "old");
        }
        else { File.WriteAllText(Path.Combine(target, "shape"), "old"); if (scenario == "readonly") File.SetAttributes(Path.Combine(target, "shape"), FileAttributes.ReadOnly); }
        using var context = new FolderRewind.History.Representation.ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, f.Engine,
            async token => new FolderRewind.History.Representation.RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, AllowDirectorySwap = false, VerificationContext = context };
        var result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target),
            (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.IsFalse(Directory.Exists(Path.Combine(target, "obsolete")));
        if (scenario is "file-to-dir" or "empty-dir") Assert.IsTrue(Directory.Exists(Path.Combine(target, "shape")));
        else Assert.AreEqual("new", File.ReadAllText(Path.Combine(target, "shape")));
        await f.History.DrainRestoreCleanupAsync();
        Assert.IsFalse(Directory.EnumerateDirectories(f.Root, ".folderrewind-restore-*").Any());
    }
    [TestMethod]
    [DataRow(HistoryRestoreApplyMode.Clean, true)]
    [DataRow(HistoryRestoreApplyMode.Clean, false)]
    [DataRow(HistoryRestoreApplyMode.Overwrite, false)]
    public async Task RealRestoreMovesDataAndPreservesUnrelatedOverwriteFileIdentity(HistoryRestoreApplyMode mode, bool swap)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var target = Path.Combine(f.Root, "target"); Directory.CreateDirectory(target);
        var untouched = Path.Combine(target, "session.lock"); File.WriteAllText(untouched, "unrelated");
        var id = WindowsRestoreFileSystem.Identity(untouched);
        using var context = new FolderRewind.History.Representation.ArchiveRestoreVerificationContext(RestoreArchivePreflightMode.Balanced);
        var result = await f.Restore(verification: context, targetVolume: true, allowSwap: swap).RestoreVersionAsync(
            f.Ours.NewVersions.Single().VersionId, new(f.Source, target), f.Before, mode);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(target, "a.txt")));
        Assert.AreEqual("base-b", File.ReadAllText(Path.Combine(target, "b.txt")));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsFalse(new FileInfo(Path.Combine(target, "a.txt")).GetAccessControl().AreAccessRulesProtected);
            Assert.IsFalse(new DirectoryInfo(target).GetAccessControl().AreAccessRulesProtected);
        }
        Assert.AreEqual(mode == HistoryRestoreApplyMode.Overwrite, File.Exists(untouched));
        if (mode == HistoryRestoreApplyMode.Overwrite) Assert.AreEqual(id, WindowsRestoreFileSystem.Identity(untouched));
                await f.History.DrainRestoreCleanupAsync();
                Assert.IsFalse(Directory.EnumerateDirectories(f.Root, ".folderrewind-restore-*").Any());
        var diagnostic = HistoryOperationDiagnostics.GetRecent().Last();
        Assert.AreEqual(0L, diagnostic.Counters.GetValueOrDefault("copy-tree-bytes"));
        Assert.AreEqual(0L, diagnostic.Counters.GetValueOrDefault("7z-t"));
    }
    [TestMethod]
    public async Task ExceptionAfterMoveRollsBackAndForeignTargetBlocksRecoveryWithoutDeletingOriginal()
    {
        foreach (var foreign in new[] { false, true })
        {
            await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
            var target = Path.Combine(f.Root, "target"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "original"), "keep");
            try
            {
                HistoryRestoreFileSystemPlanStore.TestFault = point => {
                    if (point != "after-move-0") return;
                    if (foreign) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "external"), "foreign"); }
                    throw new IOException("injected");
                };
                var result = await f.Restore(targetVolume: true).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                    new(f.Source, target), f.Before, HistoryRestoreApplyMode.Clean);
                Assert.AreEqual(foreign ? HistoryRestoreStatus.MutationFailedRecoveryRequired : HistoryRestoreStatus.MutationFailedRolledBack, result.Status, result.Diagnostic);
                if (foreign)
                {
                    Assert.AreEqual("foreign", File.ReadAllText(Path.Combine(target, "external")));
                    Assert.AreEqual("keep", File.ReadAllText(Directory.GetFiles(f.Root, "original", SearchOption.AllDirectories).Single()));
                }
                else Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "original")));
            }
            finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
        }
    }
}
