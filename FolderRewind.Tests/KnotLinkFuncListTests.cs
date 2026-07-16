using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class KnotLinkFuncListTests
{
    [TestMethod]
    public void CoreManifest_UsesCurrentOfficialShapesAndDefaults()
    {
        var manifest = KnotLinkFuncListService.BuildCore();
        Assert.AreEqual("1.0", manifest.SpecVersion);
        Assert.AreEqual("1.0.0", manifest.ManifestVersion);
        Assert.AreEqual(KnotLinkFuncListService.DefaultAppId, manifest.OpenSocket["backup"].AppId);
        Assert.AreEqual("static", manifest.OpenSocket["backup"].Args["cmd"].Type);
        Assert.AreEqual("BACKUP", manifest.OpenSocket["backup"].Args["cmd"].Value);
        Assert.AreEqual("command_completed", manifest.Signal["command_completed"].Returns["event"].Verification);

        using var json = JsonDocument.Parse(KnotLinkFuncListService.Serialize(manifest));
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.GetProperty("openSocket").GetProperty("backup").GetProperty("returns").ValueKind);
        Assert.AreEqual(JsonValueKind.Object, json.RootElement.GetProperty("signal").GetProperty("backup_success").GetProperty("returns").ValueKind);
    }

    [TestMethod]
    public void PluginMerge_IsDeterministicAndCoreWinsCollisions()
    {
        var manifest = KnotLinkFuncListService.BuildCore();
        var contribution = new PluginKnotLinkCapabilityContribution
        {
            OpenSocket = new[]
            {
                new PluginKnotLinkOpenSocketCapability { Name = "backup" },
                new PluginKnotLinkOpenSocketCapability { Name = "z_plugin_function", Description = "Plugin function" }
            }
        };

        KnotLinkFuncListService.MergePluginContributions(
            manifest,
            "app",
            "socket",
            "signal",
            new[] { ("plugin.test", contribution) });

        Assert.AreEqual("Start a backup for one managed folder.", manifest.OpenSocket["backup"].Description);
        Assert.AreEqual("app", manifest.OpenSocket["z_plugin_function"].AppId);
        CollectionAssert.AreEqual(
            manifest.OpenSocket.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            manifest.OpenSocket.Keys.ToArray());
    }

    [TestMethod]
    public void MineRewind_DeclaresPureV2Capabilities()
    {
        var contribution = new MinecraftSavesPlugin().GetKnotLinkCapabilities();
        var names = contribution.OpenSocket.Select(item => item.Name).ToArray();

        CollectionAssert.IsSubsetOf(
            new[] { "backup_current", "list_backups_current", "restore_current_latest", "restore_current", "restore_current_with_data", "handshake_response", "world_saved", "world_save_and_exit_complete", "rejoin_result" },
            names);
    }

    [TestMethod]
    public void CapabilityResponse_ReturnsPercentEncodedRuntimeManifest()
    {
        var manifestJson = KnotLinkFuncListService.Serialize(KnotLinkFuncListService.BuildCore());
        var context = new KnotLinkCommandContext(KnotLinkCommandParser.Parse("cmd=get_capabilities"));
        var response = KnotLinkProtocolFormatter.FormatOk(context, new Dictionary<string, string?>
        {
            ["content_type"] = "application/json",
            ["encoding"] = "percent",
            ["manifest_version"] = KnotLinkFuncListService.ManifestVersion,
            ["func_list"] = manifestJson
        });
        var fields = KnotLinkKeyValueCodec.Parse(response).Values;
        Assert.AreEqual("ok", fields["status"]);
        Assert.AreEqual("application/json", fields["content_type"]);
        Assert.AreEqual("percent", fields["encoding"]);

        var manifest = JsonSerializer.Deserialize<KnotLinkFuncList>(fields["func_list"]);
        Assert.IsNotNull(manifest);
        Assert.IsTrue(manifest.OpenSocket.ContainsKey("get_capabilities"));
    }

    [TestMethod]
    public void StaticManifest_MatchesCodeGeneratedCoreManifest()
    {
        var path = Path.Combine(GetRepositoryRoot(), "FolderRewind", "funcList.json");
        using var expected = JsonDocument.Parse(KnotLinkFuncListService.Serialize(KnotLinkFuncListService.BuildCore()));
        using var actual = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
