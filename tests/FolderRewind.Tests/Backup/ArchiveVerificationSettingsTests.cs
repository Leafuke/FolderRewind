using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Models;

namespace FolderRewind.Tests;

[JsonSerializable(typeof(ArchiveSettings))]
internal partial class ArchiveSettingsTestJsonContext : JsonSerializerContext;

[TestClass]
public sealed class ArchiveVerificationSettingsTests
{
    [TestMethod]
    public void NewAndExistingObjectsDefaultToBalanced()
    {
        var fresh = new ArchiveSettings();
        Assert.AreEqual(BackupArchiveVerificationLevel.Balanced, fresh.BackupVerificationLevel);
        Assert.AreEqual(RestoreArchivePreflightMode.Balanced, fresh.RestorePreflightMode);
        Assert.IsTrue(fresh.QuickRestoreSafetySnapshotEnabled);
        Assert.AreEqual(RestoreArchivePreflightMode.Balanced, Read("{}").RestorePreflightMode);
        var missingSectionDefault = new ArchiveSettings();
        missingSectionDefault.ApplyDeserializedInputMigration();
        Assert.AreEqual(RestoreArchivePreflightMode.Balanced, missingSectionDefault.RestorePreflightMode);
    }
    [TestMethod]
    [DataRow("{\"VerifyArchiveBeforeRestore\":true}", 0)]
    [DataRow("{\"VerifyArchiveBeforeRestore\":false}", 0)]
    [DataRow("{\"RestorePreflightMode\":0,\"VerifyArchiveBeforeRestore\":true}", 0)]
    [DataRow("{\"VerifyArchiveBeforeRestore\":false,\"RestorePreflightMode\":1}", 1)]
    public void LegacyMappingAndNewFieldPrecedenceAreOrderIndependent(string json, int expected)
        => Assert.AreEqual((RestoreArchivePreflightMode)expected, Read(json).RestorePreflightMode);

    [TestMethod]
    [DataRow("{}", BackupSourceReadMode.Automatic)]
    [DataRow("{\"CopySourceBeforeArchive\":true}", BackupSourceReadMode.AlwaysCopy)]
    [DataRow("{\"CopySourceBeforeArchive\":false}", BackupSourceReadMode.DirectOnly)]
    [DataRow("{\"SourceReadMode\":0,\"CopySourceBeforeArchive\":true}", BackupSourceReadMode.Automatic)]
    [DataRow("{\"CopySourceBeforeArchive\":false,\"SourceReadMode\":1}", BackupSourceReadMode.AlwaysCopy)]
    public void LegacyCopyPreferenceMigratesWithoutWritingASecondSetting(string json, BackupSourceReadMode expected)
    {
        var settings = Read(json);
        Assert.AreEqual(expected, settings.SourceReadMode);
        var serialized = JsonSerializer.Serialize(settings, ArchiveSettingsTestJsonContext.Default.ArchiveSettings);
        using var document = JsonDocument.Parse(serialized);
        Assert.IsFalse(document.RootElement.TryGetProperty("CopySourceBeforeArchive", out _));
        Assert.AreEqual(expected, Read(serialized).SourceReadMode);
    }

    [TestMethod]
    [DataRow("{\"BackupVerificationLevel\":2}")]
    [DataRow("{\"RestorePreflightMode\":-1}")]
    [DataRow("{\"BackupVerificationLevel\":null}")]
    [DataRow("{\"RestorePreflightMode\":\"Strict\"}")]
    [DataRow("{\"QuickRestoreSafetySnapshotEnabled\":1}")]
    [DataRow("{\"QuickRestoreSafetySnapshotEnabled\":null}")]
    public void InvalidExplicitPolicyIsRejected(string json) => Assert.Throws<JsonException>(() => Read(json));
    [TestMethod]
    public void SourceGeneratedRoundTripPreservesPreferencesAndIgnoresRetiredAliases()
    {
        var input = Read("""
            {"BackupVerificationLevel":1,"RestorePreflightMode":0,"QuickRestoreSafetySnapshotEnabled":false,
             "SafeRestoreEnabled":false,"VerifyArchiveBeforeRestore":true,"CompressionLevel":8,"CpuThreads":3,
             "FuturePreference":{"enabled":true}}
            """);
        var json = JsonSerializer.Serialize(input, ArchiveSettingsTestJsonContext.Default.ArchiveSettings);
        var copy = Read(json);
        Assert.AreEqual(BackupArchiveVerificationLevel.Strict, copy.BackupVerificationLevel);
        Assert.AreEqual(RestoreArchivePreflightMode.Balanced, copy.RestorePreflightMode);
        Assert.IsFalse(copy.QuickRestoreSafetySnapshotEnabled);
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("SafeRestoreEnabled", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("VerifyArchiveBeforeRestore", out _));
        Assert.IsTrue(copy.SchemaExtensions["FuturePreference"].GetProperty("enabled").GetBoolean());
        Assert.AreEqual(8, copy.CompressionLevel); Assert.AreEqual(3, copy.CpuThreads);
    }
    private static ArchiveSettings Read(string json) => JsonSerializer.Deserialize(json, ArchiveSettingsTestJsonContext.Default.ArchiveSettings)!;
}
