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
    [DataRow("{\"BackupVerificationLevel\":2}")]
    [DataRow("{\"RestorePreflightMode\":-1}")]
    [DataRow("{\"BackupVerificationLevel\":null}")]
    [DataRow("{\"RestorePreflightMode\":\"Strict\"}")]
    [DataRow("{\"QuickRestoreSafetySnapshotEnabled\":1}")]
    [DataRow("{\"QuickRestoreSafetySnapshotEnabled\":null}")]
    public void InvalidExplicitPolicyIsRejected(string json) => Assert.Throws<JsonException>(() => Read(json));
    [TestMethod]
    public void SourceGeneratedRoundTripPreservesPreferencesAndNormalizesAliases()
    {
        var input = Read("{\"BackupVerificationLevel\":1,\"RestorePreflightMode\":0,\"QuickRestoreSafetySnapshotEnabled\":false,\"SafeRestoreEnabled\":false,\"CompressionLevel\":8,\"CpuThreads\":3}");
        var json = JsonSerializer.Serialize(input, ArchiveSettingsTestJsonContext.Default.ArchiveSettings);
        var copy = Read(json);
        Assert.AreEqual(BackupArchiveVerificationLevel.Strict, copy.BackupVerificationLevel);
        Assert.AreEqual(RestoreArchivePreflightMode.Balanced, copy.RestorePreflightMode);
        Assert.IsFalse(copy.QuickRestoreSafetySnapshotEnabled);
        Assert.IsTrue(copy.SafeRestoreEnabled);
        Assert.IsFalse(copy.VerifyArchiveBeforeRestore);
        Assert.AreEqual(8, copy.CompressionLevel); Assert.AreEqual(3, copy.CpuThreads);
    }
    private static ArchiveSettings Read(string json) => JsonSerializer.Deserialize(json, ArchiveSettingsTestJsonContext.Default.ArchiveSettings)!;
}
