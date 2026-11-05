using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ConfigPersistencePolicyTests
{
    [TestMethod]
    public void ScopePersistenceKeepsFirstSpellingAndOrderAndDoesNotRewriteAnAlreadyNormalizedCollection()
    {
        var scope = new BackupSourceScope { IncludePatterns = [" region\\** ", "REGION/**", "", "  ", "saves/**"] };
        ConfigPersistencePolicy.NormalizeSourceScopeForPersistence(scope);
        CollectionAssert.AreEqual(new[] { "region/**", "saves/**" }, scope.IncludePatterns);
        var normalized = scope.IncludePatterns;
        ConfigPersistencePolicy.NormalizeSourceScopeForPersistence(scope);
        Assert.AreSame(normalized, scope.IncludePatterns);
    }

    [TestMethod]
    public void OrdinaryPersistenceRejectsInvalidAndGloballyDuplicateIdentitiesWithoutChangingAnyId()
    {
        foreach (var invalid in new[] { "", "invalid", Guid.Empty.ToString() })
        {
            var folder = new ManagedFolder { Id = invalid };
            var app = App(folder);
            Assert.ThrowsExactly<ConfigSourceIdentityException>(() => ConfigPersistencePolicy.PrepareSourceIdentities(app, false));
            Assert.AreEqual(invalid, folder.Id);
        }
        var identity = Guid.NewGuid();
        var first = new ManagedFolder { Id = identity.ToString("N") };
        var second = new ManagedFolder { Id = identity.ToString("D").ToUpperInvariant() };
        var duplicate = App(first);
        duplicate.BackupConfigs.Add(new() { SourceFolders = [second] });
        var original = second.Id;
        var error = Assert.ThrowsExactly<ConfigSourceIdentityException>(() => ConfigPersistencePolicy.PrepareSourceIdentities(duplicate, false));
        Assert.IsTrue(error.Duplicate);
        Assert.AreEqual(original, second.Id);
        Assert.AreEqual(identity.ToString("N"), first.Id);
    }

    [TestMethod]
    public void LoadingRepairPreservesValidFirstIdentityAndRepairsOnlyInvalidOrDuplicateSources()
    {
        var first = new ManagedFolder();
        var original = first.Id;
        var duplicate = new ManagedFolder { Id = original };
        var invalid = new ManagedFolder { Id = "invalid" };
        var app = App(first, duplicate, invalid);
        ConfigPersistencePolicy.PrepareSourceIdentities(app, true);
        Assert.AreEqual(original, first.Id);
        Assert.AreNotEqual(original, duplicate.Id);
        Assert.AreNotEqual("invalid", invalid.Id);
        Assert.AreEqual(3, app.BackupConfigs.SelectMany(item => item.SourceFolders).Select(item => Guid.Parse(item.Id)).Distinct().Count());
        ConfigPersistencePolicy.PrepareSourceIdentities(app, false);
    }

    private static AppConfig App(params ManagedFolder[] folders) => new()
        { BackupConfigs = [new BackupConfig { SourceFolders = new(folders) }] };
}
