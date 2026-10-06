using FolderRewind.Plugin.Runtime.Configuration;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class LegacySourceIdentityV1Tests
{
    [TestMethod]
    public void GoldenVectors_FreezeSourceAndHistoryIdentityContract()
    {
        var sourceId = LegacySourceIdentityV1.CreateSourceId(
            " minecraft-config ",
            " C:\\Games\\.minecraft\\saves\\World\\ ",
            0);
        var historyId = LegacySourceIdentityV1.CreateHistoryObjectId(
            "minecraft-config",
            "C:/Games/.minecraft/saves/World",
            "[Full][2026-08-27_12-00-00]World.7z",
            new DateTime(2026, 8, 27, 4, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual("a5d16928-7e89-5d5d-9996-8c43bc0dc967", sourceId.ToString("D"));
        Assert.AreEqual("8964d886-c53c-5e18-81b1-d6f500cb98ae", historyId.ToString("D"));
    }

    [TestMethod]
    public void CanonicalizationIsCultureAndSlashIndependent()
    {
        var first = LegacySourceIdentityV1.CreateSourceId("Config", "c:\\Data\\Save\\", 0);
        var second = LegacySourceIdentityV1.CreateSourceId(" config ", "C:/Data//Save", 0);

        Assert.AreEqual(first, second);
        Assert.AreNotEqual(first, LegacySourceIdentityV1.CreateSourceId("config", "C:/Data/Save", 1));
    }
}
