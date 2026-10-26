using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Configuration;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class TypedFilterMigrationTests
{
    [TestMethod]
    public void SchemaOneUpgradePreservesUserRulesAndDoesNotRerunLegacyCloudMigration()
    {
        var gate = new ConfigDocumentGate();
        var root = gate.Prepare("{}"u8).Document!;
        root["schemaVersion"] = 1;
        root["Legacy182UpgradePending"] = false;
        root["BackupConfigs"] = JsonNode.Parse("""
            [{"Id":"filters","Kind":{"OwnerId":"folderrewind.core","KindId":"default"},"SourceFolders":[],
              "ProviderStates":{},"Cloud":{"Enabled":true,"RemoteBasePath":"remote:existing"},
              "Filters":{"Blacklist":["session.lock","cache/**"],"BackupWhitelist":["*.mca"],"RestoreWhitelist":["nested/*.dat"]}}]
            """);
        var result = gate.Prepare(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.AreEqual(ConfigDocumentGateStatus.Migrated, result.Status, result.DiagnosticMessage);
        Assert.AreEqual(2, result.Document!["schemaVersion"]!.GetValue<int>());
        var config = result.Document["BackupConfigs"]![0]!;
        Assert.IsTrue(config["Cloud"]!["Enabled"]!.GetValue<bool>());
        Assert.AreEqual("remote:existing", config["Cloud"]!["RemoteBasePath"]!.GetValue<string>());
        foreach (var list in new[] { "Blacklist", "BackupWhitelist", "RestoreWhitelist" })
            foreach (var value in config["Filters"]![list]!.AsArray())
            {
                var rule = JsonSerializer.Deserialize<PathFilterRule>(value!.ToJsonString())!;
                Assert.AreEqual(1, rule.SemanticsVersion);
                Assert.AreEqual(PathFilterTarget.Any, rule.Target);
            }
        Assert.AreEqual(ConfigDocumentGateStatus.Current, gate.Prepare(result.Utf8Json!).Status);
    }

    [TestMethod]
    public void InvalidTypedRuleIsNotSilentlyDowngraded()
    {
        var gate = new ConfigDocumentGate();
        var root = gate.Prepare("{}"u8).Document!;
        root["BackupConfigs"] = JsonNode.Parse("""
            [{"Id":"filters","Kind":{"OwnerId":"folderrewind.core","KindId":"default"},"SourceFolders":[],
              "ProviderStates":{},"Filters":{"Blacklist":[{"Pattern":"session.lock","Target":"Unknown","SemanticsVersion":2}]}}]
            """);
        Assert.AreEqual(ConfigDocumentGateStatus.RecoveryRequired, gate.Prepare(Encoding.UTF8.GetBytes(root.ToJsonString())).Status);
    }

    [TestMethod]
    public void PresetMigrationPreservesBothLegacyAndTypedRulesThroughExportAndImport()
    {
        var gate = new ConfigDocumentGate();
        var root = gate.Prepare("{}"u8).Document!;
        root["schemaVersion"] = 1;
        root["Templates"] = JsonNode.Parse("""
            [{"SchemaVersion":1,"Kind":{"OwnerId":"folderrewind.core","KindId":"default"},"ProviderDefaults":{},
              "Filters":{"Blacklist":["*.tmp",{"Pattern":"./cache/**","Target":"Directory","SemanticsVersion":2}],
                         "BackupWhitelist":["*.mca"],"RestoreWhitelist":["playerdata"]}}]
            """);
        var migrated = gate.Prepare(Encoding.UTF8.GetBytes(root.ToJsonString()));
        Assert.AreEqual(ConfigDocumentGateStatus.Migrated, migrated.Status, migrated.DiagnosticMessage);
        var imported = gate.Prepare(migrated.Utf8Json!);
        Assert.AreEqual(ConfigDocumentGateStatus.Current, imported.Status, imported.DiagnosticMessage);
        var preset = imported.Document!["Templates"]![0]!;
        Assert.AreEqual(2, preset["SchemaVersion"]!.GetValue<int>());
        var rules = JsonSerializer.Deserialize<PathFilterRule[]>(preset["Filters"]!["Blacklist"]!.ToJsonString())!;
        CollectionAssert.AreEqual(new[] { PathFilterRule.Legacy("*.tmp"), new("./cache/**", PathFilterTarget.Directory) }, rules);
        foreach (var name in new[] { "BackupWhitelist", "RestoreWhitelist" })
            Assert.AreEqual(1, preset["Filters"]![name]![0]!["SemanticsVersion"]!.GetValue<int>());
    }
}
