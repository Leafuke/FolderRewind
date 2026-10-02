using FolderRewind.Services.KnotLink;
using FolderRewind.Plugin.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        Assert.AreEqual("3.0.0", manifest.ManifestVersion);
        Assert.AreEqual(KnotLinkFuncListService.DefaultAppId, manifest.OpenSocket["backup"].AppId);
        Assert.AreEqual("static", manifest.OpenSocket["backup"].Args["cmd"].Type);
        Assert.AreEqual("BACKUP", manifest.OpenSocket["backup"].Args["cmd"].Value);
        CollectionAssert.AreEquivalent(
            new[] { "full", "smart" },
            manifest.OpenSocket["backup"].Args["backup_mode"].Options!.Select(option => option[1]).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "LZMA2", "Deflate", "BZip2", "zstd" },
            manifest.OpenSocket["backup"].Args["compression_method"].Options!.Select(option => option[1]).ToArray());
        Assert.IsTrue(manifest.OpenSocket["backup"].Args.ContainsKey("compression_level"));
        Assert.IsFalse(manifest.OpenSocket["backup"].Args.ContainsKey("force_full"));
        Assert.IsFalse(manifest.OpenSocket["backup_all"].Args.ContainsKey("force_full"));
        Assert.AreEqual("command_completed", manifest.Signal["command_completed"].Returns["event"].Verification);

        using var json = JsonDocument.Parse(KnotLinkFuncListService.Serialize(manifest));
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.GetProperty("openSocket").GetProperty("backup").GetProperty("returns").ValueKind);
        Assert.AreEqual(JsonValueKind.Object, json.RootElement.GetProperty("signal").GetProperty("backup_success").GetProperty("returns").ValueKind);
    }

    [TestMethod]
    public void V3CommandMerge_IsDeterministicAndCoreWinsCollisions()
    {
        var manifest = KnotLinkFuncListService.BuildCore();
        KnotLinkFuncListService.MergePluginCommands(
            manifest,
            "app",
            "socket",
            [
                (new PluginId("plugin.z"), new KnotLinkCommandDescriptor[]
                {
                    new("Z_PLUGIN_FUNCTION", "Plugin function")
                }),
                (new PluginId("plugin.a"), new KnotLinkCommandDescriptor[]
                {
                    new("BACKUP", "Must not replace core"),
                    new("A_PLUGIN_FUNCTION", "First plugin function")
                })
            ]);

        Assert.AreEqual("Start a backup for one managed folder.", manifest.OpenSocket["backup"].Description);
        Assert.AreEqual("app", manifest.OpenSocket["a_plugin_function"].AppId);
        Assert.AreEqual("socket", manifest.OpenSocket["z_plugin_function"].OpenSocketId);
        CollectionAssert.AreEqual(
            manifest.OpenSocket.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            manifest.OpenSocket.Keys.ToArray());
    }

    [TestMethod]
    public void SemanticVariantsInheritCoreArgumentsWithoutLosingConversationOrColliding()
    {
        var manifest = KnotLinkFuncListService.BuildCore();
        var variants = KnotLinkCoreCommands.FolderCommands.Select(command => new KnotLinkCommandDescriptor(command, "Current world")
        {
            FunctionName = "minerewind_" + command.ToLowerInvariant() + "_current_save", IsTargetSelector = true,
            RequiredArguments = new Dictionary<string, string> { ["current_save"] = "true" }
        }).ToArray();
        KnotLinkFuncListService.MergePluginCommands(manifest, "app", "socket", [(new PluginId("com.folderrewind.minerewind"), variants)]);
        foreach (var command in KnotLinkCoreCommands.FolderCommands)
        {
            var variant = manifest.OpenSocket["minerewind_" + command.ToLowerInvariant() + "_current_save"];
            var core = manifest.OpenSocket[command.ToLowerInvariant()];
            Assert.AreEqual(command, variant.Args["cmd"].Value);
            Assert.AreEqual("true", variant.Args["current_save"].Value);
            CollectionAssert.AreEquivalent(core.Args.Keys.Where(key => key is not "config_id" and not "folder").Append("current_save").ToArray(), variant.Args.Keys.ToArray());
        }
        Assert.AreEqual("clean", manifest.OpenSocket["restore"].Args["mode"].DefaultValue);
        Assert.IsNull(manifest.OpenSocket["restore"].Args["preserve_player_data"].DefaultValue);
        foreach (var command in new[] { "backup", "backup_all", "auto_backup" })
        {
            Assert.IsNull(manifest.OpenSocket[command].Args["backup_mode"].DefaultValue);
            Assert.IsNull(manifest.OpenSocket[command].Args["compression_method"].DefaultValue);
            Assert.IsTrue(manifest.OpenSocket[command].Args.ContainsKey("compression_level"));
        }
    }

    [TestMethod]
    public void PluginSignalsEnrichExistingEventsAndPublishNewEvents()
    {
        var manifest = KnotLinkFuncListService.BuildCore();
        KnotLinkFuncListService.MergePluginSignals(manifest, "app", "signal", new("com.folderrewind.minerewind"),
            [new("restore_finished", "Finished", new Dictionary<string, string> { ["status"] = "Mutation status" }),
             new("rejoin_world", "Rejoin", new Dictionary<string, string> { ["world"] = "World name" })]);
        Assert.IsTrue(manifest.Signal["restore_finished"].Returns.ContainsKey("folder"));
        Assert.IsTrue(manifest.Signal["restore_finished"].Returns.ContainsKey("status"));
        Assert.AreEqual("rejoin_world", manifest.Signal["rejoin_world"].Returns["event"].Verification);
        KnotLinkFuncListService.MergePluginSignals(manifest, "app", "signal", new("plugin.other"),
            [new("rejoin_world", "Collision", new Dictionary<string, string> { ["unexpected"] = "Must not merge" })]);
        Assert.IsFalse(manifest.Signal["rejoin_world"].Returns.ContainsKey("unexpected"));
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
