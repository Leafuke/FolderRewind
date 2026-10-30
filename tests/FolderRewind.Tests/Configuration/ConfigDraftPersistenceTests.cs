using FolderRewind.Services;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.History.Cloud;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ConfigDraftPersistenceTests
{
    [TestMethod]
    public async Task ConsumedDraftCannotDeleteANewerSavedGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindDraftGeneration", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "draft.json");
        try
        {
            var old = await DraftFileGeneration.SaveAsync(path, "old"u8.ToArray());
            Assert.IsFalse(await DraftFileGeneration.DiscardIfCurrentAsync(path, null));
            var current = await DraftFileGeneration.SaveAsync(path, "new"u8.ToArray());
            Assert.IsFalse(await DraftFileGeneration.DiscardIfCurrentAsync(path, old));
            Assert.AreEqual("new", await File.ReadAllTextAsync(path));
            Assert.IsTrue(await DraftFileGeneration.DiscardIfCurrentAsync(path, current));
            Assert.IsFalse(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RuntimeCloudStatusCanRebaseButActualSettingsRemainConflicts()
    {
        const string baseline = """{"ConfigRevision":"a","SourceFolders":[{"Id":"x","LastBackupTime":"old"}],"Cloud":{"Enabled":true,"LastRunUtc":"old","LastExitCode":0,"LastErrorMessage":""},"Filters":{"Blacklist":[]}}""";
        var runtime = baseline.Replace("\"LastRunUtc\":\"old\"", "\"LastRunUtc\":\"now\"").Replace("\"LastExitCode\":0", "\"LastExitCode\":5");
        Assert.AreNotEqual(baseline, runtime); // The former full-snapshot check rejects this background update.
        Assert.IsTrue(ConfigDraftComparison.OnlyRuntimeFactsChanged(baseline, runtime));
        Assert.IsTrue(ConfigDraftComparison.OnlyRuntimeFactsChanged(baseline, baseline.Replace("\"LastBackupTime\":\"old\"", "\"LastBackupTime\":\"now\"")));
        Assert.IsFalse(ConfigDraftComparison.OnlyRuntimeFactsChanged(baseline, runtime.Replace("\"Enabled\":true", "\"Enabled\":false")));
        Assert.IsFalse(ConfigDraftComparison.OnlyRuntimeFactsChanged(baseline, runtime.Replace("\"Blacklist\":[]", "\"Blacklist\":[\"skip.tmp\"]")));
        Assert.IsFalse(ConfigDraftComparison.OnlyRuntimeFactsChanged(baseline, runtime.Replace("\"ConfigRevision\":\"a\"", "\"ConfigRevision\":\"b\"")));
        CollectionAssert.AreEqual(new[] { "Cloud" }, ConfigDraftComparison.ChangedSections(baseline, runtime));
    }

}
