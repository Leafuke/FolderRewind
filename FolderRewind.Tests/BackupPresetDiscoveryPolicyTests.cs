using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupPresetDiscoveryPolicyTests
{
    [TestMethod]
    public void ApplicationPolicyClassifiesInlineProviderMixedAndInvalidSources()
    {
        Assert.AreEqual(
            BackupPresetApplicationMode.Inline,
            BackupPresetApplicationPolicy.Classify(true, []));
        Assert.AreEqual(
            BackupPresetApplicationMode.ProviderTargeted,
            BackupPresetApplicationPolicy.Classify(false, [new(false, true)]));
        Assert.AreEqual(
            BackupPresetApplicationMode.Inline,
            BackupPresetApplicationPolicy.Classify(false, [new(true, true)]));
        Assert.AreEqual(
            BackupPresetApplicationMode.Invalid,
            BackupPresetApplicationPolicy.Classify(false, [new(false, false)]));
    }

    [TestMethod]
    public void DefinitionMatchingIgnoresProviderCaseButPreservesDefinitionCase()
    {
        Assert.IsTrue(DiscoveryDefinitionMatchPolicy.Matches(
            "COM.Example.Provider",
            "game-id",
            "com.example.provider",
            "game-id"));
        Assert.IsFalse(DiscoveryDefinitionMatchPolicy.Matches(
            "com.example.provider",
            "GAME-ID",
            "com.example.provider",
            "game-id"));
        Assert.IsFalse(DiscoveryDefinitionMatchPolicy.Matches(
            string.Empty,
            "game-id",
            "com.example.provider",
            "game-id"));
    }
}
