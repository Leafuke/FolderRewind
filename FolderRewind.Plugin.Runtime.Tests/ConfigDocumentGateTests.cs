using System.Text.Json.Nodes;
using FolderRewind.Plugin.Runtime.Configuration;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class ConfigDocumentGateTests
{
    [TestMethod]
    public void MissingSchemaVersionIsClassifiedAsLegacy()
    {
        var result = ConfigDocumentParser.Parse("{}"u8);

        Assert.AreEqual(ConfigDocumentKind.Legacy, result.Kind);
        Assert.AreEqual(0, result.SchemaVersion);
    }

    [TestMethod]
    public void MalformedDocumentRequiresRecovery()
    {
        var result = new ConfigDocumentGate().Prepare(Fixture("malformed.json"));

        Assert.AreEqual(ConfigDocumentGateStatus.RecoveryRequired, result.Status);
        Assert.AreEqual("config_json_malformed", result.DiagnosticCode);
    }

    [TestMethod]
    public void NewerSchemaRequiresRecoveryInsteadOfDowngrade()
    {
        var result = new ConfigDocumentGate().Prepare(Fixture("newer-schema.json"));

        Assert.AreEqual(ConfigDocumentGateStatus.RecoveryRequired, result.Status);
        Assert.AreEqual("config_schema_newer_than_host", result.DiagnosticCode);
    }

    [TestMethod]
    public void RepresentativeLegacyDocumentMapsKnownDataAndRetainsUnknownData()
    {
        var gate = new ConfigDocumentGate();

        var result = gate.Prepare(Fixture("legacy-representative.json"));

        Assert.AreEqual(ConfigDocumentGateStatus.Migrated, result.Status);
        var root = result.Document!;
        Assert.AreEqual(1, root["schemaVersion"]!.GetValue<int>());
        Assert.IsTrue(root["LegacyRootMarker"]!["future"]!.GetValue<bool>());

        var configs = root["BackupConfigs"]!.AsArray();
        var minecraft = configs[0]!.AsObject();
        Assert.AreEqual("com.folderrewind.minerewind", minecraft["Kind"]!["OwnerId"]!.GetValue<string>());
        Assert.AreEqual("minecraft-saves", minecraft["Kind"]!["KindId"]!.GetValue<string>());
        Assert.AreEqual("com.folderrewind.minerewind", minecraft["BackupScope"]!["OwnerId"]!.GetValue<string>());
        Assert.AreEqual("selected-regions", minecraft["BackupScope"]!["ScopeId"]!.GetValue<string>());
        Assert.AreEqual(
            LegacySourceIdentityV1.CreateSourceId(
                "minecraft-config",
                "C:\\Games\\.minecraft\\saves\\World",
                0).ToString("D"),
            minecraft["SourceFolders"]![0]!["Id"]!.GetValue<string>());
        Assert.AreEqual(42, minecraft["SourceFolders"]![0]!["FutureFolderValue"]!.GetValue<int>());
        Assert.AreEqual(
            "1.21.8",
            minecraft["ProviderStates"]!["com.folderrewind.minerewind"]!["Data"]!["MinecraftVersion"]!.GetValue<string>());
        Assert.AreEqual("preset-1", minecraft["HostOrigin"]!["TemplateId"]!.GetValue<string>());
        Assert.AreEqual(
            "preserve-me",
            minecraft["LegacyPreservation"]!["OriginalExtendedProperties"]!["FutureMineRewindValue"]!.GetValue<string>());

        var plugins = root["GlobalSettings"]!["Plugins"]!;
        Assert.IsTrue(plugins["EnabledIntent"]!["com.folderrewind.minerewind"]!.GetValue<bool>());
        Assert.IsTrue(plugins["TypedSettings"]!["com.folderrewind.minerewind"]!["AutoDiscoverSaves"]!.GetValue<bool>());
        Assert.IsFalse(plugins["TypedSettings"]!["com.folderrewind.minerewind"]!["AutoCreateConfigs"]!.GetValue<bool>());
        Assert.AreEqual(
            "keep-me",
            plugins["TypedSettings"]!["com.folderrewind.minerewind"]!["UnknownSetting"]!.GetValue<string>());

        var core = configs[1]!.AsObject();
        Assert.AreEqual("folderrewind.core", core["Kind"]!["OwnerId"]!.GetValue<string>());
        Assert.AreEqual("default", core["Kind"]!["KindId"]!.GetValue<string>());

        var preset = root["Templates"]![0]!.AsObject();
        Assert.AreEqual(1, preset["SchemaVersion"]!.GetValue<int>());
        Assert.AreEqual("com.folderrewind.minerewind", preset["Kind"]!["OwnerId"]!.GetValue<string>());
        Assert.IsNotNull(preset["ProviderDefaults"]);
        Assert.AreEqual("com.folderrewind.minerewind", preset["RequiredPluginIds"]![0]!.GetValue<string>());
    }

    [TestMethod]
    public void UnknownLegacyDataIsPreservedWithWarnings()
    {
        var result = new ConfigDocumentGate().Prepare(Fixture("legacy-unknown.json"));

        Assert.AreEqual(ConfigDocumentGateStatus.Migrated, result.Status);
        Assert.IsNotEmpty(result.Warnings!);
        var config = result.Document!["BackupConfigs"]![0]!.AsObject();
        Assert.AreEqual("folderrewind.core", config["Kind"]!["OwnerId"]!.GetValue<string>());
        Assert.AreEqual(
            "Third Party Save",
            config["LegacyPreservation"]!["OriginalConfigType"]!.GetValue<string>());
        Assert.AreEqual(
            "opaque",
            config["LegacyPreservation"]!["OriginalExtendedProperties"]!["VendorPrivate"]!.GetValue<string>());
        Assert.AreEqual(3, result.Document!["UnknownTopLevel"]!.AsArray().Count);
    }

    [TestMethod]
    public void MigratedDocumentIsIdempotentAndFolderIdIsStable()
    {
        var gate = new ConfigDocumentGate();
        var first = gate.Prepare(Fixture("legacy-representative.json"));
        var folderId = first.Document!["BackupConfigs"]![0]!["SourceFolders"]![0]!["Id"]!.GetValue<string>();

        var second = gate.Prepare(first.Utf8Json!);

        Assert.AreEqual(ConfigDocumentGateStatus.Current, second.Status);
        Assert.AreEqual(folderId, second.Document!["BackupConfigs"]![0]!["SourceFolders"]![0]!["Id"]!.GetValue<string>());
        CollectionAssert.AreEqual(first.Utf8Json!, second.Utf8Json!);
    }

    [TestMethod]
    public void CurrentDocumentWithDuplicateFolderIdsGetsNewNativeIdentity()
    {
        const string duplicate = """
            {
              "schemaVersion": 1,
              "GlobalSettings": {},
              "BackupConfigs": [
                {
                  "Id": "a",
                  "Kind": { "OwnerId": "folderrewind.core", "KindId": "default" },
                  "SourceFolders": [
                    { "Id": "11111111-1111-1111-1111-111111111111", "Path": "A", "ProviderStates": {} },
                    { "Id": "11111111-1111-1111-1111-111111111111", "Path": "B", "ProviderStates": {} }
                  ],
                  "ProviderStates": {},
                  "BackupScope": { "OwnerId": "", "ScopeId": "", "Parameters": {} }
                }
              ]
            }
            """;

        var replacement = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var result = new ConfigDocumentGate(new LegacyConfigMigrator(() => replacement))
            .Prepare(System.Text.Encoding.UTF8.GetBytes(duplicate));

        Assert.AreEqual(ConfigDocumentGateStatus.Migrated, result.Status);
        Assert.AreEqual(
            replacement.ToString("D"),
            result.Document!["BackupConfigs"]![0]!["SourceFolders"]![1]!["Id"]!.GetValue<string>());
        Assert.IsNotEmpty(result.Warnings!);
    }

    [TestMethod]
    public void MissingLegacySourceIdIsIndependentOfRandomGuidProvider()
    {
        var first = new ConfigDocumentGate(
                new LegacyConfigMigrator(() => Guid.Parse("11111111-1111-1111-1111-111111111111")))
            .Prepare(Fixture("legacy-representative.json"));
        var second = new ConfigDocumentGate(
                new LegacyConfigMigrator(() => Guid.Parse("22222222-2222-2222-2222-222222222222")))
            .Prepare(Fixture("legacy-representative.json"));

        Assert.AreEqual(
            first.Document!["BackupConfigs"]![0]!["SourceFolders"]![0]!["Id"]!.GetValue<string>(),
            second.Document!["BackupConfigs"]![0]!["SourceFolders"]![0]!["Id"]!.GetValue<string>());
    }

    private static byte[] Fixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
