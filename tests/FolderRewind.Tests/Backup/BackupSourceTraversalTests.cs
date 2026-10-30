using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class BackupSourceTraversalTests
{
    [TestMethod]
    public void StrictScanDoesNotEnterAnInaccessibleDirectoryOutsideSourceScope()
    {
        WithSource((root, deny) =>
        {
            using var denied = deny("excluded");
            var scope = new BackupSourceScope { Mode = BackupSourceScopeMode.Include, IncludePatterns = new() { "allowed/**" } };
            var files = BackupSourceFileEnumerator.EnumerateUnordered(root, scope, requireComplete: true).ToArray();
            CollectionAssert.AreEquivalent(new[] { "allowed/nested/save.dat" }, files.Select(f => f.RelativePath).ToArray());
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileAndDirectoryInventoriesPruneExcludedSubtrees(bool whitelist)
    {
        WithSource((root, deny) =>
        {
            using var denied = deny("excluded");
            var boundary = EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.All, [],
                whitelist ? EffectiveBoundaryFilterMode.Whitelist : EffectiveBoundaryFilterMode.Blacklist,
                [new(whitelist ? "./allowed" : "./excluded", PathFilterTarget.Directory)], false);
            var scope = new HistoryRestoreTreeScope(new(SourceId.New(), root, boundary));
            var files = BackupSourceFileEnumerator.EnumerateUnordered(root, null,
                file => scope.Includes(Path.GetRelativePath(root, file).Replace('\\', '/')),
                requireComplete: true, mayDescend: scope.MayDescend).ToArray();
            CollectionAssert.AreEquivalent(new[] { "allowed/nested/save.dat" }, files.Select(f => f.RelativePath).ToArray());
            var directories = SourceDirectorySnapshot.Read(root, relative => scope.Includes(relative), files.Select(f => f.RelativePath), default,
                includeDirectory: relative => scope.Includes(relative, isDirectory: true), mayDescend: scope.MayDescend);
            CollectionAssert.AreEquivalent(new[] { "allowed", "allowed/nested", "allowed/empty" }, directories.ToArray());
            CollectionAssert.AreEquivalent(new[] { "allowed", "allowed/nested", "allowed/empty", "allowed/nested/save.dat" },
                scope.Enumerate(root).Select(e => e.RelativePath).ToArray());
        });
    }

    [TestMethod]
    public void DirectoryInventoryPrunesSourceScopeWithoutLosingEmptyDirectories()
    {
        WithSource((root, deny) =>
        {
            using var denied = deny("excluded");
            var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.Include, ["allowed/**"],
                EffectiveBoundaryFilterMode.Blacklist, [], false);
            var scope = new HistoryRestoreTreeScope(new(SourceId.New(), root, boundary));
            var directories = SourceDirectorySnapshot.Read(root, relative => scope.Includes(relative), ["allowed/nested/save.dat"], default,
                includeDirectory: relative => scope.Includes(relative, isDirectory: true), mayDescend: scope.MayDescend);
            CollectionAssert.AreEquivalent(new[] { "allowed", "allowed/nested", "allowed/empty" }, directories.ToArray());
        });
    }

    [TestMethod]
    [DataRow("./allowed/nested/save.dat", PathFilterTarget.File)]
    [DataRow("allowed/nested/*.dat", PathFilterTarget.File)]
    [DataRow("allowed/nested", PathFilterTarget.Directory)]
    public void AnchoredWhitelistRetainsParentsThatCanReachAMatch(string pattern, PathFilterTarget target)
    {
        WithSource((root, deny) =>
        {
            using var denied = deny("excluded");
            var matcher = PathRuleMatcher.CreateForBackupRules([new(pattern, target)], root, root, false);
            var files = BackupSourceFileEnumerator.EnumerateUnordered(root, null, path => matcher.IsMatch(path), requireComplete: true,
                mayDescend: relative => matcher.MayContainDescendant(Path.Combine(root, relative))).ToArray();
            CollectionAssert.AreEquivalent(new[] { "allowed/nested/save.dat" }, files.Select(f => f.RelativePath).ToArray());
        });
    }

    [TestMethod]
    [DataRow("save.dat")]
    [DataRow("regex:save\\.dat$")]
    [DataRow("**/save.dat")]
    public void UnanchoredWhitelistDoesNotHideAnUnreadablePotentialMatch(string pattern)
    {
        WithSource((root, deny) =>
        {
            using var denied = deny("excluded");
            var matcher = PathRuleMatcher.CreateForBackupRules([new(pattern, PathFilterTarget.File)], root, root, true);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => BackupSourceFileEnumerator.EnumerateUnordered(root, null,
                file => matcher.IsMatch(file), requireComplete: true,
                mayDescend: relative => matcher.MayContainDescendant(Path.Combine(root, relative))).ToArray());
        });
    }

    [TestMethod]
    public void StrictScanAndDirectoryInventoryStillFailForAnUnreadableManagedDirectory()
    {
        WithSource((root, deny) =>
        {
            using var denied = deny("allowed/nested");
            var sourceScope = new BackupSourceScope { Mode = BackupSourceScopeMode.Include, IncludePatterns = new() { "allowed/**" } };
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => BackupSourceFileEnumerator.EnumerateUnordered(root, sourceScope, requireComplete: true).ToArray());
            var scope = new HistoryRestoreTreeScope(new(SourceId.New(), root));
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => SourceDirectorySnapshot.Read(root, _ => true, [], default, mayDescend: scope.MayDescend));
        });
    }

    private static void WithSource(Action<string, Func<string, IDisposable>> action)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Requires Windows directory ACLs.");
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FolderRewind-traversal-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Assert.IsTrue(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(Path.Combine(root, "allowed", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "allowed", "empty"));
        Directory.CreateDirectory(Path.Combine(root, "excluded"));
        File.WriteAllText(Path.Combine(root, "allowed", "nested", "save.dat"), "selected");
        File.WriteAllText(Path.Combine(root, "excluded", "other.dat"), "excluded");
        try { action(root, relative => new DeniedDirectory(Path.Combine(root, relative))); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DeniedDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly string _original;
        public DeniedDirectory(string path)
        {
            _directory = new(path);
            var security = _directory.GetAccessControl(AccessControlSections.Access);
            _original = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            security.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            try
            {
                _directory.SetAccessControl(security);
                Assert.ThrowsExactly<UnauthorizedAccessException>(() => _directory.EnumerateFileSystemInfos().ToArray());
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(_original, AccessControlSections.Access);
            _directory.SetAccessControl(restored);
        }
    }
}
