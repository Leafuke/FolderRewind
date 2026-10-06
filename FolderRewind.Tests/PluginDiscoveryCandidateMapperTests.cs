using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Discovery;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PluginDiscoveryCandidateMapperTests
{
    private static readonly PluginId PluginId = new("com.example.discovery");
    private static readonly DiscoveryProviderId ProviderId = new("com.example.provider");
    private static readonly StateOwnerId StateOwnerId = new("com.example.state");
    private static readonly ConfigKindRef Kind = new(new OwnerId("com.example.discovery"), "game-saves");
    private static readonly DiscoveryDefinitionDescriptor Definition = new(
        "example-game",
        "Example Game",
        ["Example"],
        new Dictionary<string, string>());

    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindPluginDiscoveryMapperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void StableIdsNormalizeWindowsCaseAndTrailingSeparators()
    {
        var first = Map(CreateCandidate(_root));
        var variant = OperatingSystem.IsWindows() ? _root.ToUpperInvariant() : _root;
        var second = Map(CreateCandidate(variant + Path.DirectorySeparatorChar));

        Assert.AreEqual("plugin-game:v1:com.example.provider:example-game", first.StableKey);
        Assert.AreEqual("world-1", first.BackupSets.Single().Identity.SetId);
        Assert.AreEqual(
            first.BackupSets.Single().Resources.Single().ResourceId,
            second.BackupSets.Single().Resources.Single().ResourceId);
        Assert.AreEqual(
            Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar),
            first.BackupSets.Single().Resources.Single().FixedRoot,
            ignoreCase: OperatingSystem.IsWindows());
    }

    [TestMethod]
    public void MappingDeepCopiesConfigAndFolderProviderStates()
    {
        DiscoveredGameCandidate mapped;
        using (var configJson = JsonDocument.Parse("""{"config":"value"}"""))
        using (var folderJson = JsonDocument.Parse("""{"folder":"value"}"""))
        {
            mapped = Map(CreateCandidate(_root, configJson.RootElement, folderJson.RootElement));
        }

        var set = mapped.BackupSets.Single();
        var resource = set.Resources.Single();
        Assert.AreEqual("plugin:com.example.discovery@1.2.3", set.DiscoveryRevision);
        Assert.AreEqual(DiscoveryConfidence.High, resource.Confidence);
        Assert.IsTrue(resource.IsSelectedByDefault);
        Assert.AreEqual("com.example.discovery", set.PluginDraftContext!.Kind.OwnerId);
        Assert.AreEqual(
            "value",
            set.PluginDraftContext.ConfigProviderStates[StateOwnerId.Value].Data.GetProperty("config").GetString());
        Assert.AreEqual(
            "value",
            set.PluginDraftContext.FolderProviderStatesByResourceId[resource.ResourceId][StateOwnerId.Value]
                .Data.GetProperty("folder").GetString());
    }

    [TestMethod]
    public void RelativeFolderIsRetainedAsBlockedUnselectedResource()
    {
        var mapped = Map(CreateCandidate("relative\\world"));

        var resource = mapped.BackupSets.Single().Resources.Single();
        Assert.AreEqual(BackupResourceSupportState.InvalidPath, resource.SupportState);
        Assert.IsFalse(resource.FixedRootExists);
        Assert.IsFalse(resource.IsSelectedByDefault);
    }

    [TestMethod]
    public void FilesystemRootRemainsAQualifiedUnsafeRoot()
    {
        var filesystemRoot = Path.GetPathRoot(_root)!;
        var mapped = Map(CreateCandidate(filesystemRoot));

        var resource = mapped.BackupSets.Single().Resources.Single();
        Assert.AreEqual(filesystemRoot, resource.FixedRoot, ignoreCase: OperatingSystem.IsWindows());
        Assert.AreEqual(BackupResourceSupportState.UnsafeRoot, resource.SupportState);
        Assert.IsFalse(resource.IsSelectedByDefault);
    }

    [TestMethod]
    public void UnsupportedCandidateShapeProducesStableDiagnostic()
    {
        var diagnostics = new List<DiscoveryDiagnostic>();
        var mapped = PluginDiscoveryCandidateMapper.Map(
            PluginId,
            ProviderId,
            "plugin:com.example.discovery@1.2.3",
            new FolderRewind.Plugin.Abstractions.DiscoveryCandidate("world-1", "World", Array.Empty<ConfigDraft>()),
            Definition,
            diagnostics);

        Assert.IsNull(mapped);
        Assert.AreEqual("plugin-candidate-shape-unsupported", diagnostics.Single().Code);
    }

    private static DiscoveredGameCandidate Map(FolderRewind.Plugin.Abstractions.DiscoveryCandidate candidate)
    {
        var diagnostics = new List<DiscoveryDiagnostic>();
        var mapped = PluginDiscoveryCandidateMapper.Map(
            PluginId,
            ProviderId,
            "plugin:com.example.discovery@1.2.3",
            candidate,
            Definition,
            diagnostics);
        Assert.IsNotNull(mapped, string.Join(Environment.NewLine, diagnostics.Select(value => value.Message)));
        return mapped;
    }

    private static FolderRewind.Plugin.Abstractions.DiscoveryCandidate CreateCandidate(
        string path,
        JsonElement configState = default,
        JsonElement folderState = default)
    {
        var configStates = configState.ValueKind == JsonValueKind.Undefined
            ? new Dictionary<StateOwnerId, ProviderStateDraft>()
            : new Dictionary<StateOwnerId, ProviderStateDraft>
            {
                [StateOwnerId] = new(StateOwnerId, 3, configState)
            };
        var folderStates = folderState.ValueKind == JsonValueKind.Undefined
            ? new Dictionary<StateOwnerId, ProviderStateDraft>()
            : new Dictionary<StateOwnerId, ProviderStateDraft>
            {
                [StateOwnerId] = new(StateOwnerId, 4, folderState)
            };
        return new FolderRewind.Plugin.Abstractions.DiscoveryCandidate(
            "world-1",
            "World",
            [
                new ConfigDraft(
                    Kind,
                    "World",
                    [new FolderDraft(path, "World saves", folderStates)],
                    configStates)
            ]);
    }
}
