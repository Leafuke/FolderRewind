using FolderRewind.Plugin.Runtime.Configuration;
using System.Text.Json.Nodes;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class Legacy182CloudAndPluginTests
{
    [TestMethod]
    [DataRow(false, false)] [DataRow(false, true)] [DataRow(true, false)] [DataRow(true, true)]
    public void EffectivePluginSwitchIsGlobalAndIndividual(bool global, bool individual)
    {
        var original = Document();
        original["GlobalSettings"]!["Plugins"]!["Enabled"] = global;
        original["GlobalSettings"]!["Plugins"]!["PluginEnabled"]![ConfigSchema.MineRewindPluginId] = individual;
        var migrated = new LegacyConfigMigrator().Migrate(original).Document;
        Assert.AreEqual(global && individual, migrated["GlobalSettings"]!["Plugins"]!["EnabledIntent"]![ConfigSchema.MineRewindPluginId]!.GetValue<bool>());
        Assert.AreEqual(global, original["GlobalSettings"]!["Plugins"]!["Enabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public void CloudSettingsAreRemovedBeforeStartupAndCurrentConfigurationsAreNotReset()
    {
        var original = Document();
        var before = original.ToJsonString();
        var migrator = new LegacyConfigMigrator();
        var result = migrator.Migrate(original);
        Assert.AreEqual(before, original.ToJsonString());
        foreach (var container in new[] { "BackupConfigs", "Templates" })
        {
            var cloud = result.Document[container]![0]!["Cloud"]!.AsObject();
            Assert.IsFalse(cloud["Enabled"]!.GetValue<bool>());
            Assert.IsFalse(cloud.ContainsKey("ArgumentsTemplate"));
            Assert.IsFalse(cloud.ContainsKey("RcloneConfigPath"));
        }
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("自行创建新的云配置")));
        result.Document["BackupConfigs"]![0]!["Cloud"] = new JsonObject { ["Enabled"] = true, ["RemoteBasePath"] = "new:clean" };
        var current = migrator.Migrate(result.Document);
        Assert.IsTrue(current.Document["BackupConfigs"]![0]!["Cloud"]!["Enabled"]!.GetValue<bool>());
        Assert.AreEqual("new:clean", current.Document["BackupConfigs"]![0]!["Cloud"]!["RemoteBasePath"]!.GetValue<string>());
    }

    [TestMethod]
    public void MissingGlobalSwitchUsesReleasedDisabledDefault()
    {
        var original = Document();
        original["GlobalSettings"]!["Plugins"]!.AsObject().Remove("Enabled");
        var migrated = new LegacyConfigMigrator().Migrate(original).Document;
        Assert.IsFalse(migrated["GlobalSettings"]!["Plugins"]!["EnabledIntent"]![ConfigSchema.MineRewindPluginId]!.GetValue<bool>());
    }

    private static JsonObject Document() => JsonNode.Parse("""
        { "GlobalSettings": { "DefaultCloudRemoteBasePath":"old:legacy", "Plugins": {
            "Enabled":true, "PluginEnabled":{"com.folderrewind.minerewind":true} } },
          "BackupConfigs":[{"Id":"old-config", "ConfigType":"Default", "SourceFolders":[],
            "Cloud":{"Enabled":true,"ArgumentsTemplate":"old command", "RcloneConfigPath":"private"}}],
          "Templates":[{"BaseConfigType":"Minecraft Saves", "Cloud":{"Enabled":true,"ArgumentsTemplate":"old command"}}] }
        """)!.AsObject();
}
