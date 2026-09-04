using FolderRewind.History.Domain;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryConfigIdentityLookupTests
{
    [TestMethod]
    [DataRow("D")]
    [DataRow("N")]
    [DataRow("B")]
    [DataRow("P")]
    public void SnapshotIdentityResolvesPersistedGuidFormats(string format)
    {
        var id = Guid.NewGuid();
        var snapshotId = new HistoryConfigId(id.ToString("N"));
        Assert.IsTrue(snapshotId.Matches(id.ToString(format)));
        Assert.IsTrue(snapshotId.Matches(id.ToString(format).ToUpperInvariant()));
        Assert.IsFalse(snapshotId.Matches(Guid.NewGuid().ToString(format)));
    }

    [TestMethod]
    public void LegacyIdentifiersKeepCaseAndWhitespaceNormalization()
    {
        var id = new HistoryConfigId("legacy-profile");
        Assert.IsTrue(id.Matches(" Legacy-Profile "));
        Assert.IsFalse(id.Matches("other-profile"));
        Assert.IsFalse(id.Matches(null));
        Assert.IsFalse(id.Matches(""));
    }

    [TestMethod]
    public void RuntimeLookupUsesCanonicalIdentityOnTheOwnerThread()
    {
        var source = File.ReadAllText(Path.Combine(FolderManagerMvvmArchitectureTests.FindRoot(),
            "FolderRewind/History/Application/NativeHistoryCoreGateway.cs"));
        StringAssert.Contains(source, "identity.Matches(item.Id)");
        StringAssert.Contains(source, "await UiDispatcherService.RunOnUiAsync");
    }
}
