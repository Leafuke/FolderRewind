using FolderRewind.History.Application;
using FolderRewind.History.Storage;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("Recovery")]
[DoNotParallelize]
public sealed class HistoryRestoreCompatibilityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CyclicOrDisconnectedRootJunctionNeverBecomesANewOrdinaryTarget(bool cycle)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-disconnected-root-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var link = Path.Combine(root, "logical"); var target = cycle ? link : Path.Combine(root, "missing");
        if (!cycle) Directory.CreateDirectory(target);
        CreateDirectoryLink(link, target, false);
        try
        {
            if (!cycle) Directory.Delete(target);
            Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.ResolvePath(link));
            Assert.AreNotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));
        }
        finally { Directory.Delete(link); Directory.Delete(root, true); }
    }
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task AdsAreAllowedAndOriginalStreamsSurviveRollback(bool swap, bool fail)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var original = Path.Combine(f.Target, "a.txt");
        File.WriteAllText(original, "local-original");
        File.WriteAllText(original + ":metadata", "original-stream");
        File.WriteAllText(f.Target + ":metadata", "root-stream");
        File.WriteAllText(f.Root + ":metadata", "ancestor-stream");
        var protectedRoot = Path.Combine(f.Root, "protected"); Directory.CreateDirectory(protectedRoot);
        File.WriteAllText(protectedRoot + ":metadata", "protected-stream");
        try
        {
            if (fail) HistoryRestoreFileSystemPlanStore.TestFault = point => { if (point == "after-move-0") throw new IOException("injected"); };
            var restore = f.Restore(targetVolume: true, allowSwap: swap, protectedRoots: [protectedRoot]);
            var result = await restore.RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(fail ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreEqual(fail ? "local-original" : "ours-a", File.ReadAllText(original));
            if (fail)
            {
                Assert.AreEqual("original-stream", File.ReadAllText(original + ":metadata"));
                Assert.AreEqual("root-stream", File.ReadAllText(f.Target + ":metadata"));
            }
            else Assert.Throws<FileNotFoundException>(() => File.ReadAllText(original + ":metadata"));
            Assert.AreEqual("ancestor-stream", File.ReadAllText(f.Root + ":metadata"));
            Assert.AreEqual("protected-stream", File.ReadAllText(protectedRoot + ":metadata"));
        }
        finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task HardLinksAreReplacedByPathWithoutChangingOutsideContentOrReadonly(bool readOnly, bool fail)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var file = Path.Combine(f.Target, "a.txt");
        var outside = Path.Combine(f.Root, "outside-link");
        var second = Path.Combine(f.Target, "another-link");
        File.WriteAllText(file, "shared-original");
        Assert.IsTrue(CreateHardLinkW(outside, file, IntPtr.Zero));
        Assert.IsTrue(CreateHardLinkW(second, file, IntPtr.Zero));
        if (readOnly) File.SetAttributes(file, FileAttributes.ReadOnly);
        var identity = WindowsRestoreFileSystem.Identity(file);
        try
        {
            if (fail) HistoryRestoreFileSystemPlanStore.TestFault = point => { if (point == "after-move-0") throw new IOException("injected"); };
            var result = await f.Restore(targetVolume: true, allowSwap: false).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                new(f.Source, f.Target), f.Before, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(fail ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreEqual("shared-original", File.ReadAllText(outside));
            Assert.AreEqual(identity, WindowsRestoreFileSystem.Identity(outside));
            Assert.AreEqual(readOnly, (File.GetAttributes(outside) & FileAttributes.ReadOnly) != 0);
            Assert.AreEqual(fail ? "shared-original" : "ours-a", File.ReadAllText(file));
        }
        finally
        {
            HistoryRestoreFileSystemPlanStore.TestFault = null;
            File.SetAttributes(outside, FileAttributes.Normal);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RedirectedRootsKeepTheirLogicalEntry(bool symbolic, bool swap)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var link = Path.Combine(f.Root, "logical-world");
        CreateDirectoryLink(link, f.Target, symbolic);
        try
        {
            var result = await f.Restore(targetVolume: true, allowSwap: swap).RestoreVersionAsync(f.Ours.NewVersions.Single().VersionId,
                new(f.Source, link), f.Before, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreNotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));
            Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(link, "a.txt")));
            using var actual = new WindowsPhysicalDirectoryLease(f.Target, false);
            using var alias = new WindowsPhysicalDirectoryLease(link, false);
            Assert.AreEqual(actual.Identity, alias.Identity);
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void ProtectedAliasesAreComparedPhysicallyAndLinkEntriesCannotBeRetargeted()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-binding-" + Guid.NewGuid().ToString("N"));
        var actual = Path.Combine(root, "actual"); Directory.CreateDirectory(actual);
        var link = Path.Combine(root, "LocalState"); CreateDirectoryLink(link, actual, false);
        try
        {
            using (var held = new WindowsDirectoryReadLease(Path.Combine(link, "payload")))
                Assert.Throws<IOException>(() => Directory.Delete(link));
            Assert.Throws<IOException>(() => HistoryRestoreWorkspacePlanner.Plan(FolderRewind.History.Domain.HistoryTransactionId.New(),
                [new(FolderRewind.History.Domain.SourceId.New(), actual)], [link]));
        }
        finally { Directory.Delete(link); Directory.Delete(root, true); }
    }

    internal static void CreateDirectoryLink(string link, string target, bool symbolic)
    {
        if (symbolic)
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (UnauthorizedAccessException) { Assert.Inconclusive("Directory symbolic links require Developer Mode or the creation privilege."); }
            return;
        }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, output + error);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExcludedSubtreesArePrunedAndWhitelistAncestorsRemainTraversable(bool includedScope)
    {
        await using var f = new HistoryChainRewriteFixture(); await f.InitializeAsync();
        var boundary = new EffectiveSourceBoundarySnapshot(includedScope ? EffectiveBoundaryScopeMode.Include : EffectiveBoundaryScopeMode.All,
            includedScope ? ["managed/**"] : [], EffectiveBoundaryFilterMode.Blacklist, ["cache"], false);
        var node = await f.AddAsync(null, dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "managed", "empty"));
            File.WriteAllText(Path.Combine(dir, "managed", "file"), "restored");
        }, boundary: boundary);
        var target = Path.Combine(f.Root, "target"); Directory.CreateDirectory(Path.Combine(target, "managed"));
        File.WriteAllText(Path.Combine(target, "managed", "file"), "original");
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep"), "outside-original");
        var excluded = Path.Combine(target, "cache"); CreateDirectoryLink(excluded, outside, false);
        var original = WindowsRestoreFileSystem.Identity(Path.Combine(outside, "keep"));
        using var verification = new ArchiveRestoreVerificationContext(FolderRewind.Models.RestoreArchivePreflightMode.Balanced);
        var restore = new HistoryRestoreService(f.History, f.Engine,
            async token => new RepresentationEnvironment((await f.History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, VerificationContext = verification };
        try
        {
            using (var current = new HistoryWorkingTreeReadLease(new(f.Source, target, boundary))) current.Validate();
            var result = await restore.RestoreVersionAsync(node.Version.VersionId, new(f.Source, target, boundary),
                (await f.History.WorkspaceStore.LoadAsync()).Value!, HistoryRestoreApplyMode.Clean);
            Assert.AreEqual(HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreEqual("restored", File.ReadAllText(Path.Combine(target, "managed", "file")));
            Assert.IsTrue(Directory.Exists(Path.Combine(target, "managed", "empty")));
            Assert.AreEqual(original, WindowsRestoreFileSystem.Identity(Path.Combine(outside, "keep")));
            Assert.AreEqual("outside-original", File.ReadAllText(Path.Combine(excluded, "keep")));
            Assert.AreEqual(HistoryWorkingStateComparisonStatus.Equal, (await new HistoryExactWorkingStateProbe(f.History, restore)
                .CompareAsync(new(f.Source, target, boundary), (await f.History.WorkspaceStore.LoadAsync()).Value!.GetSourceState(f.Source), default)).Status);
        }
        finally { Directory.Delete(excluded); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
}
