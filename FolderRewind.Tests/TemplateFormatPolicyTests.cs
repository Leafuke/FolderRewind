using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class TemplateFormatPolicyTests
{
    [TestMethod]
    public void CurrentEnvelopeIsAccepted()
    {
        Assert.IsTrue(TemplateFormatPolicy.IsCurrentEnvelope("FolderRewindTemplate", "1.0"));
    }

    [TestMethod]
    public void BackupPresetEnvelopeIsAccepted()
    {
        Assert.IsTrue(TemplateFormatPolicy.IsBackupPresetEnvelope("FolderRewindBackupPreset", "2.0"));
        Assert.IsFalse(TemplateFormatPolicy.IsBackupPresetEnvelope("FolderRewindTemplate", "1.0"));
    }

    [TestMethod]
    [DataRow(null, "1.0")]
    [DataRow("", "1.0")]
    [DataRow("FolderRewindTemplate", null)]
    [DataRow("FolderRewindTemplate", "")]
    [DataRow("folderrewindtemplate", "1.0")]
    [DataRow("FolderRewindTemplate", "1.1")]
    public void NonCanonicalEnvelopeIsRejected(string? magic, string? schemaVersion)
    {
        Assert.IsFalse(TemplateFormatPolicy.IsCurrentEnvelope(magic, schemaVersion));
    }

    [TestMethod]
    public void CurrentOfficialIndexIsAccepted()
    {
        using var document = JsonDocument.Parse(
            """{"schemaVersion":"1.0","templates":[]}""");

        Assert.IsTrue(TemplateFormatPolicy.IsCurrentOfficialIndex(document.RootElement));
    }

    [TestMethod]
    [DataRow("""[]""")]
    [DataRow("""{"templates":[]}""")]
    [DataRow("""{"schemaVersion":"1.1","templates":[]}""")]
    [DataRow("""{"schemaVersion":"1.0","templates":{}}""")]
    public void NonCanonicalOfficialIndexIsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.IsFalse(TemplateFormatPolicy.IsCurrentOfficialIndex(document.RootElement));
    }

    [TestMethod]
    public void CurrentBackupPresetIndexIsAccepted()
    {
        using var document = JsonDocument.Parse(
            """{"magic":"FolderRewindBackupPresetIndex","schemaVersion":"2.0","presets":[]}""");

        Assert.IsTrue(TemplateFormatPolicy.IsCurrentBackupPresetIndex(document.RootElement));
    }

    [TestMethod]
    [DataRow("""{"magic":"FolderRewindBackupPresetIndex","schemaVersion":"1.0","presets":[]}""")]
    [DataRow("""{"magic":"FolderRewindBackupPresetIndex","schemaVersion":"2.0","presets":{}}""")]
    [DataRow("""{"schemaVersion":"2.0","presets":[]}""")]
    public void NonCanonicalBackupPresetIndexIsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.IsFalse(TemplateFormatPolicy.IsCurrentBackupPresetIndex(document.RootElement));
    }
}
