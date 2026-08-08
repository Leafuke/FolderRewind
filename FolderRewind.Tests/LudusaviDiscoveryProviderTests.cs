using FolderRewind.Models;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LudusaviDiscoveryProviderTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindLudusaviDiscoveryTests", Guid.NewGuid().ToString("N"));
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
    public void ResolverPreservesFutureSlotsWithoutEnumeratingFiles()
    {
        var saves = Path.Combine(_root, "Studio", "Example", "Saves");
        Directory.CreateDirectory(saves);
        var resolver = CreateResolver();
        var resource = FileResource("<winAppData>/Studio/Example/Saves/**/*.sav");

        var resolved = resolver.Resolve(resource, installation: null, storeUserId: string.Empty);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.GetFullPath(saves), resolved.FixedRoot);
        CollectionAssert.AreEqual(new[] { "**/*.sav" }, resolved.IncludePatterns.ToArray());
        Assert.AreEqual(BackupResourceKind.FileSet, resolved.Kind);
    }

    [TestMethod]
    public void GlobMatcherUsesLiteralSeparatorsAndCaseInsensitiveCharacterClasses()
    {
        Assert.IsTrue(LudusaviGlobMatcher.IsMatch("Saves/SlotA.SAV", "saves/slot[AB].sav"));
        Assert.IsFalse(LudusaviGlobMatcher.IsMatch("Saves/nested/SlotA.sav", "Saves/*.sav"));
        Assert.IsTrue(LudusaviGlobMatcher.IsMatch("Saves/nested/SlotA.sav", "Saves/**/*.sav"));
        Assert.IsFalse(LudusaviGlobMatcher.IsSafeRelativePattern("../outside/*.sav"));
    }

    [TestMethod]
    public async Task ProviderMergesStrongStoreMatchesAndKeepsRegistryVisible()
    {
        var steamRoot = Path.Combine(_root, "SteamHades");
        var gogRoot = Path.Combine(_root, "GogHades");
        Directory.CreateDirectory(Path.Combine(steamRoot, "Saves"));
        Directory.CreateDirectory(Path.Combine(gogRoot, "Saves"));
        File.WriteAllText(Path.Combine(steamRoot, "Saves", "steam.sav"), "steam");
        File.WriteAllText(Path.Combine(gogRoot, "Saves", "gog.sav"), "gog");
        var game = new LudusaviCompiledGame
        {
            DefinitionId = "Hades",
            DisplayName = "Hades",
            ExternalIds = new Dictionary<string, string>
            {
                ["steam"] = "1145360",
                ["gog"] = "123456"
            },
            Files = new[] { FileResource("<root>/Saves/*.sav") },
            Registry = new[]
            {
                new LudusaviCompiledResource
                {
                    ResourceId = "registry",
                    Kind = BackupResourceKind.Registry,
                    Expression = "HKEY_CURRENT_USER/Software/Supergiant/Hades"
                }
            }
        };
        var installations = new[]
        {
            Installation(GameStore.Steam, "1145360", steamRoot),
            Installation(GameStore.Gog, "123456", gogRoot)
        };
        var provider = CreateProvider(new[] { game }, installations);

        var result = await provider.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        Assert.HasCount(1, result.Candidates);
        Assert.HasCount(2, result.Candidates[0].Installations);
        Assert.HasCount(3, result.Candidates[0].BackupSets[0].Resources);
        var registry = result.Candidates[0].BackupSets[0].Resources.Single(item => item.Kind == BackupResourceKind.Registry);
        Assert.AreEqual(BackupResourceSupportState.UnsupportedRegistry, registry.SupportState);
        Assert.IsFalse(registry.IsSelectedByDefault);
        Assert.IsTrue(result.Candidates[0].BackupSets[0].Resources
            .Where(item => item.Kind == BackupResourceKind.FileSet)
            .All(item => item.IsSelectedByDefault && item.Confidence == DiscoveryConfidence.High));
    }

    [TestMethod]
    public async Task ProviderDoesNotProbeDefinitionsWithoutInstallationEvidence()
    {
        var stable = Path.Combine(_root, "Studio", "Example");
        Directory.CreateDirectory(stable);
        File.WriteAllText(Path.Combine(stable, "save.dat"), "save");
        var provider = CreateProvider(
            new[]
            {
                new LudusaviCompiledGame
                {
                    DefinitionId = "Example",
                    DisplayName = "Example",
                    Files = new[]
                    {
                        FileResource("<winAppData>/Studio/Example/*.dat"),
                        FileResource("<home>/**/unsafe.sav")
                    }
                }
            },
            Array.Empty<DetectedGameInstallation>());

        var progress = new RecordingProgress();
        var result = await provider.DiscoverAsync(new DiscoveryRequest(), progress, CancellationToken.None);

        Assert.IsEmpty(result.Candidates);
        Assert.AreEqual(0, result.Statistics.DefinitionsConsidered);
        Assert.IsFalse(progress.Values.Any(item => item.Phase == "resources"));
    }

    [TestMethod]
    public async Task ZeroInstallationsDoNotEnumerateManifestDefinitions()
    {
        var provider = CreateProvider(
            new ThrowingGameList(),
            Array.Empty<DetectedGameInstallation>());

        var result = await provider.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        Assert.IsEmpty(result.Candidates);
        Assert.AreEqual(0, result.Statistics.DefinitionsConsidered);
    }

    [TestMethod]
    public async Task InstalledGameSelectsExistingRootWithoutMatchingFiles()
    {
        var installRoot = Path.Combine(_root, "EmptyGame");
        var savesRoot = Path.Combine(installRoot, "Saves");
        Directory.CreateDirectory(savesRoot);
        var game = new LudusaviCompiledGame
        {
            DefinitionId = "EmptyGame",
            DisplayName = "Empty Game",
            ExternalIds = new Dictionary<string, string> { ["steam"] = "42" },
            Files = new[] { FileResource("<root>/Saves/*.sav") }
        };
        var provider = CreateProvider(
            new[] { game },
            new[] { Installation(GameStore.Steam, "42", installRoot, "Empty Game") });

        var result = await provider.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        var resource = result.Candidates.Single().BackupSets.Single().Resources.Single();
        Assert.IsTrue(resource.FixedRootExists);
        Assert.IsTrue(resource.IsSelectedByDefault);
    }

    private LudusaviDiscoveryProvider CreateProvider(
        IReadOnlyList<LudusaviCompiledGame> games,
        IReadOnlyList<DetectedGameInstallation> installations)
    {
        var index = new LudusaviCompiledIndex
        {
            SourceSha256 = new string('a', 64),
            Games = games
        };
        return new LudusaviDiscoveryProvider(
            _ => Task.FromResult<(
                LudusaviManifestCacheMetadata Metadata,
                LudusaviCompiledIndex Index)?>(
                (new LudusaviManifestCacheMetadata { GenerationId = new string('a', 64) }, index)),
            new FakeInstallationDiscovery(installations),
            CreateResolver());
    }

    private LudusaviPathExpressionResolver CreateResolver() => new(new LudusaviPathEnvironment
    {
        Home = _root,
        AppData = _root,
        LocalAppData = _root,
        LocalAppDataLow = _root,
        Documents = _root,
        Public = _root,
        ProgramData = _root,
        WindowsDirectory = _root,
        UserName = "tester"
    });

    private static LudusaviCompiledResource FileResource(string expression) => new()
    {
        ResourceId = expression,
        Kind = BackupResourceKind.FileSet,
        Expression = expression,
        Tags = new[] { "save" }
    };

    private static DetectedGameInstallation Installation(
        GameStore store,
        string id,
        string path,
        string displayName = "Hades") => new()
    {
        Store = store,
        StoreGameId = id,
        DisplayName = displayName,
        InstallPath = path,
        LibraryRoot = Path.GetDirectoryName(path) ?? string.Empty
    };

    private sealed class FakeInstallationDiscovery(
        IReadOnlyList<DetectedGameInstallation> installations) : ILauncherInstallationDiscoveryService
    {
        public IReadOnlyList<DetectedGameInstallation> Scan(
            IReadOnlyDictionary<GameStore, IReadOnlyList<string>> configuredRoots,
            IReadOnlyList<string>? disabledAutoRoots = null) => installations;
    }

    private sealed class RecordingProgress : IProgress<DiscoveryProgress>
    {
        public List<DiscoveryProgress> Values { get; } = new();
        public void Report(DiscoveryProgress value) => Values.Add(value);
    }

    private sealed class ThrowingGameList : IReadOnlyList<LudusaviCompiledGame>
    {
        public int Count => throw new AssertFailedException("Definitions must not be inspected when no installations exist.");
        public LudusaviCompiledGame this[int index] => throw new AssertFailedException("Definitions must not be inspected when no installations exist.");
        public IEnumerator<LudusaviCompiledGame> GetEnumerator() =>
            throw new AssertFailedException("Definitions must not be inspected when no installations exist.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
