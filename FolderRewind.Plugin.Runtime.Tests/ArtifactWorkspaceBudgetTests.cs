using System.Security.AccessControl;
using System.Security.Principal;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ArtifactWorkspaceBudgetTests
{
    [TestMethod]
    public void SameVolumeAliasesAndConcurrentOperationsShareOneReservation()
    {
        var budget = new ArtifactSpaceBudget(path => new(path.StartsWith("other", StringComparison.Ordinal) ? "two" : "one", 100));
        using (budget.Reserve(new ArtifactSpaceRequest("a", 60)))
        {
            Assert.ThrowsExactly<IOException>(() => budget.Reserve(new ArtifactSpaceRequest("alias", 41)));
            Assert.ThrowsExactly<IOException>(() => budget.Reserve(new ArtifactSpaceRequest("alias", 20), new ArtifactSpaceRequest("same-volume", 21)));
            using var independent = budget.Reserve(new ArtifactSpaceRequest("other-volume", 100));
            using var remainder = budget.Reserve(new ArtifactSpaceRequest("a", 40));
        }
        using var whole = budget.Reserve(new ArtifactSpaceRequest("alias", 100));
    }

    [TestMethod]
    public void ExtractionQuotaCountsDirectoriesAndAllDecodedNodes()
    {
        Assert.AreEqual(4, ArtifactExtractionBudget.CountEntries(["a/b/one", "a/two"]));
        var budget = new ArtifactExtractionBudget(5, 3);
        budget.Reserve(2, 2);
        Assert.ThrowsExactly<InvalidDataException>(() => budget.Reserve(4, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => budget.Reserve(1, 2));
        budget.Reserve(3, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ArtifactExtractionBudget(17L * 1024 * 1024 * 1024));
    }

    [TestMethod]
    public async Task TemporarySnapshotRecoveryKeepsLiveOwnersAndUnknownLegacyDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "PluginTemporaryOwners", Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = await OwnedPluginTemporaryStorage.CreateAsync(root);
            var live = await first.CreateDirectoryAsync(default); await File.WriteAllTextAsync(Path.Combine(live, "world"), "snapshot");
            var legacy = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(legacy);
            await using var second = await OwnedPluginTemporaryStorage.CreateAsync(root);
            Assert.IsTrue(Directory.Exists(live));
            await first.DisposeAsync();
            await using var recovered = await OwnedPluginTemporaryStorage.CreateAsync(root);
            Assert.IsFalse(Directory.Exists(live));
            Assert.IsTrue(Directory.Exists(legacy));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PlaintextWorkspaceHasProtectedOwnerPermissionsAndIsRemovedByOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArtifactPrivate", Guid.NewGuid().ToString("N"));
        try
        {
            HostArtifactWorkspace.CreatePrivateDirectory(root);
            File.WriteAllText(Path.Combine(root, "plaintext"), "isolated test");
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var security = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                Assert.IsTrue(security.AreAccessRulesProtected);
                Assert.AreEqual(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                foreach (var rule in rules)
                { Assert.AreEqual(identity.User, rule.IdentityReference); Assert.IsFalse(rule.IsInherited); }
            }
            HostArtifactWorkspace.DeleteOwnedDirectory(root);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
