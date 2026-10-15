using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Runtime.Configuration;
using FolderRewind.Services;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudLegacy196CompatibilityTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Legacy196", name);
    [TestMethod]
    public void ReleasedConfigurationKeepsLogicalIdentityAndAbsoluteRemoteSuffix()
    {
        var bytes = File.ReadAllBytes(Fixture("config.json"));
        var prepared = new ConfigDocumentGate().Prepare(bytes);
        Assert.IsTrue(prepared.IsReady);
        using var source = JsonDocument.Parse(bytes); using var upgraded = JsonDocument.Parse(prepared.Utf8Json!);
        var original = source.RootElement.GetProperty("BackupConfigs")[0];
        var current = upgraded.RootElement.GetProperty("BackupConfigs")[0];
        Assert.AreEqual(original.GetProperty("Id").GetString(), current.GetProperty("Id").GetString());
        Assert.AreEqual(original.GetProperty("SourceFolders")[0].GetProperty("Id").GetString(), current.GetProperty("SourceFolders")[0].GetProperty("Id").GetString());
        var path = current.GetProperty("Cloud").GetProperty("RemoteBasePath").GetString()!;
        Assert.AreEqual(("old_remote", "/Original/FolderRewind"), CloudRemotePath.Split(path));
        Assert.IsTrue(RcloneConnectionService.InspectRemotes(Fixture("rclone.conf")).Remotes.Any(r => r.Name == "old_remote"));
    }
    [TestMethod]
    public async Task ReleasedHistoryPackImportsWithoutRewritingPublishedBytes()
    {
        var descriptor = HistoryRepositoryDescriptor.Parse(File.ReadAllBytes(Fixture("repository.json")));
        var bytes = File.ReadAllBytes(Fixture("66666666666646668666666666666666.frpack"));
        var read = new HistoryPackCodec().Decode(bytes);
        new HistoryRepositoryValidator(new HistoryPackCodec()).Validate(descriptor.ConfigId, [read]);
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindLegacy196", Guid.NewGuid().ToString("N"));
        try
        {
            using var repository = new FileHistoryRepository(descriptor.ConfigId, new(root));
            await repository.InitializeAsync(); await repository.ImportAsync([bytes]);
            CollectionAssert.AreEqual(bytes, (await repository.ReadAllPacksAsync()).Single().OriginalBytes);
            Assert.AreEqual(HistoryRepositoryDescriptor.CurrentFormatVersion, descriptor.FormatVersion);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
