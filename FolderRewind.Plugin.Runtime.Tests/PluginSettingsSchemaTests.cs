using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Settings;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginSettingsSchemaTests
{
    private static readonly PluginId PluginId = new("com.folderrewind.settings-test");

    [TestMethod]
    public void AllV3SettingTypesValidateAndDefaultsAreApplied()
    {
        var schema = Parse("""
            {
              "schemaVersion": 1,
              "settings": [
                { "key": "name", "type": "string", "required": true },
                { "key": "enabled", "type": "boolean", "default": true },
                { "key": "count", "type": "integer", "default": 3 },
                { "key": "notes", "type": "multiline" },
                { "key": "folder", "type": "folderPath" },
                { "key": "file", "type": "filePath" },
                { "key": "mode", "type": "enum", "enumValues": ["safe", "fast"], "default": "safe" }
              ]
            }
            """);
        var candidate = Snapshot(new Dictionary<string, JsonElement>
        {
            ["name"] = Json("\"MineRewind\""),
            ["notes"] = Json("\"line one\\nline two\""),
            ["folder"] = Json("\"C:/Games\""),
            ["file"] = Json("\"C:/Games/options.txt\"")
        });

        var result = schema.Validate(candidate);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.NormalizedSettings.Values["enabled"].GetBoolean());
        Assert.AreEqual(3, result.NormalizedSettings.Values["count"].GetInt32());
        Assert.AreEqual("safe", result.NormalizedSettings.Values["mode"].GetString());
    }

    [TestMethod]
    public void RequiredTypeAndEnumViolationsAreErrors()
    {
        var schema = Parse("""
            {
              "schemaVersion": 1,
              "settings": [
                { "key": "required", "type": "string", "required": true },
                { "key": "count", "type": "integer" },
                { "key": "mode", "type": "enum", "enumValues": ["safe", "fast"] }
              ]
            }
            """);
        var candidate = Snapshot(new Dictionary<string, JsonElement>
        {
            ["count"] = Json("1.5"),
            ["mode"] = Json("\"unknown\"")
        });

        var result = schema.Validate(candidate);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEquivalent(
            new[] { "required", "count", "mode" },
            result.Issues.Where(issue => issue.Severity == DiagnosticSeverity.Error).Select(issue => issue.Key).ToArray());
    }

    [TestMethod]
    public void UnknownLegacyValuesArePreservedWithWarning()
    {
        var schema = Parse("""{ "schemaVersion": 1, "settings": [] }""");
        var candidate = Snapshot(new Dictionary<string, JsonElement> { ["FutureSetting"] = Json("42") });

        var result = schema.Validate(candidate);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(42, result.NormalizedSettings.Values["FutureSetting"].GetInt32());
        Assert.IsTrue(result.Issues.Any(issue => issue.Code == "settings.unknown_preserved"));
    }

    [TestMethod]
    public void LocalizedPresentationMetadataIsParsedWithoutAffectingValidation()
    {
        var schema = Parse("""
            {
              "schemaVersion": 1,
              "settings": [
                {
                  "key": "enabled",
                  "type": "boolean",
                  "default": true,
                  "displayName": "Enabled",
                  "description": "Default description",
                  "localizedDisplayName": { "zh-CN": "启用" },
                  "localizedDescription": { "zh-CN": "中文说明" }
                }
              ]
            }
            """);

        var definition = schema.Settings.Single();

        Assert.AreEqual("Enabled", definition.DisplayName);
        Assert.AreEqual("启用", definition.LocalizedDisplayName["zh-CN"]);
        Assert.AreEqual("中文说明", definition.LocalizedDescription["zh-CN"]);
        Assert.IsTrue(schema.Validate(Snapshot(new Dictionary<string, JsonElement>())).IsValid);
    }

    [TestMethod]
    [DataRow("{ \"schemaVersion\": 2, \"settings\": [] }")]
    [DataRow("{ \"schemaVersion\": 1, \"settings\": [{ \"key\": \"x\", \"type\": \"number\" }] }")]
    [DataRow("{ \"schemaVersion\": 1, \"settings\": [{ \"key\": \"x\", \"type\": \"enum\", \"enumValues\": [] }] }")]
    [DataRow("{ \"schemaVersion\": 1, \"settings\": [{ \"key\": \"same\", \"type\": \"string\" }, { \"key\": \"same\", \"type\": \"string\" }] }")]
    [DataRow("{ \"schemaVersion\": 1, \"settings\": [{ \"key\": \"x\", \"type\": \"string\", \"localizedDisplayName\": \"invalid\" }] }")]
    public void InvalidStaticSchemaIsRejected(string json)
        => Assert.ThrowsExactly<InvalidDataException>(() => Parse(json));

    private static PluginSettingsSchema Parse(string json)
        => PluginSettingsSchema.Parse(Encoding.UTF8.GetBytes(json));

    private static PluginSettingsSnapshot Snapshot(IReadOnlyDictionary<string, JsonElement> values)
        => new(PluginId, values);

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
