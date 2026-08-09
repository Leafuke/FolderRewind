using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using System.Text;

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
        CollectionAssert.AreEqual(new[] { "**/*.sav", "**/*.sav/**" }, resolved.IncludePatterns.ToArray());
        Assert.AreEqual(BackupResourceKind.FileSet, resolved.Kind);
    }

    [TestMethod]
    public void ResolverUsesExplicitRootBaseAndGameAndTreatsPlaceholderGlobsLiterally()
    {
        var storeRoot = Path.Combine(_root, "SteamLibrary");
        var basePath = Path.Combine(storeRoot, "steamapps", "common", "[Preview] Game");
        var installation = new DetectedGameInstallation
        {
            Store = GameStore.Steam,
            StoreGameId = "42",
            RootPath = storeRoot,
            BasePath = basePath,
            InstalledGameName = "[Preview] Game"
        };
        var resolver = CreateResolver();

        var fromBase = resolver.Resolve(FileResource("<base>/../Shared/*.sav"), installation, string.Empty);
        var fromRootAndGame = resolver.Resolve(FileResource("<root>/Shared/<game>/*.sav"), installation, string.Empty);
        var exactGame = resolver.Resolve(FileResource("<home>/<game>"), installation, string.Empty);

        Assert.IsNotNull(fromBase);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(basePath, "..", "Shared")), fromBase.FixedRoot);
        Assert.IsNotNull(fromRootAndGame);
        Assert.AreEqual(Path.Combine(storeRoot, "Shared", "[Preview] Game"), fromRootAndGame.FixedRoot);
        Assert.IsNotNull(exactGame);
        CollectionAssert.AreEqual(
            new[] { "[[]Preview] Game", "[[]Preview] Game/**" },
            exactGame.IncludePatterns.ToArray());
        Assert.IsTrue(LudusaviGlobMatcher.IsMatch("[Preview] Game/save.dat", exactGame.IncludePatterns[1]));
        Assert.IsFalse(LudusaviGlobMatcher.IsMatch("P Game/save.dat", exactGame.IncludePatterns[1]));
    }

    [TestMethod]
    public void ResolverKeepsFutureExactPathAsExactAndRecursiveRules()
    {
        var resolver = CreateResolver();
        var resolved = resolver.Resolve(
            FileResource("<winAppData>/FutureGame/Saves"),
            installation: null,
            storeUserId: string.Empty);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(Path.Combine(_root, "FutureGame"), resolved.FixedRoot);
        CollectionAssert.AreEqual(new[] { "Saves", "Saves/**" }, resolved.IncludePatterns.ToArray());
    }

    [TestMethod]
    public void ResolverGradesBroadAndVolumeRoots()
    {
        var resolver = CreateResolver();
        var broad = resolver.Resolve(FileResource("<home>/*"), null, string.Empty);
        var volume = resolver.Resolve(
            FileResource(Path.Combine(Path.GetPathRoot(_root)!, "*.sav")),
            null,
            string.Empty);

        Assert.IsNotNull(broad);
        Assert.AreEqual(LudusaviPathSafety.RequiresConfirmation, broad.Safety);
        Assert.IsNotNull(volume);
        Assert.AreEqual(LudusaviPathSafety.Blocked, volume.Safety);
    }

    [TestMethod]
    public void GlobMatcherUsesLiteralSeparatorsAndCaseInsensitiveCharacterClasses()
    {
        Assert.IsTrue(LudusaviGlobMatcher.IsMatch("Saves/SlotA.SAV", "saves/slot[AB].sav"));
        Assert.IsFalse(LudusaviGlobMatcher.IsMatch("Saves/nested/SlotA.sav", "Saves/*.sav"));
        Assert.IsTrue(LudusaviGlobMatcher.IsMatch("Saves/nested/SlotA.sav", "Saves/**/*.sav"));
        Assert.IsTrue(LudusaviGlobMatcher.IsMatch("account/slot.sav", "*/*.sav"));
        Assert.IsFalse(LudusaviGlobMatcher.IsMatch("account/nested/slot.sav", "*/*.sav"));
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
                ["steamExtra"] = "1145360",
                ["gogExtra"] = "123456"
            },
            Files = new[] { FileResource("<base>/Saves/*.sav") },
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
            Files = new[] { FileResource("<base>/Saves/*.sav") }
        };
        var provider = CreateProvider(
            new[] { game },
            new[] { Installation(GameStore.Steam, "42", installRoot, "Empty Game") });

        var result = await provider.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        var resource = result.Candidates.Single().BackupSets.Single().Resources.Single();
        Assert.IsTrue(resource.FixedRootExists);
        Assert.IsTrue(resource.IsSelectedByDefault);
    }

    [TestMethod]
    public async Task InstallDirectoryHintsMatchSteamAndGogWithoutStrongIds()
    {
        var steamRoot = Path.Combine(_root, "SteamFolder");
        var gogRoot = Path.Combine(_root, "GogFolder");
        Directory.CreateDirectory(Path.Combine(steamRoot, "Saves"));
        Directory.CreateDirectory(Path.Combine(gogRoot, "Saves"));
        var games = new[]
        {
            new LudusaviCompiledGame
            {
                DefinitionId = "Steam Extended Edition",
                DisplayName = "Steam Extended Edition",
                InstallDirectoryHints = new[] { "SteamFolder" },
                Files = new[] { FileResource("<base>/Saves/*.sav") }
            },
            new LudusaviCompiledGame
            {
                DefinitionId = "GOG Extended Edition",
                DisplayName = "GOG Extended Edition",
                InstallDirectoryHints = new[] { "GogFolder" },
                Files = new[] { FileResource("<base>/Saves/*.sav") }
            }
        };
        var installations = new[]
        {
            Installation(GameStore.Steam, "999999", steamRoot, "Localized Steam Name"),
            Installation(GameStore.Gog, string.Empty, gogRoot, "Localized GOG Name")
        };
        var provider = CreateProvider(games, installations);

        var result = await provider.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        Assert.HasCount(2, result.Candidates);
        Assert.IsTrue(result.Candidates.All(candidate =>
            candidate.Installations.Single().Evidence.Single().Confidence == DiscoveryConfidence.Medium));
        Assert.IsTrue(result.Candidates.SelectMany(candidate => candidate.BackupSets)
            .SelectMany(set => set.Resources)
            .All(resource => resource.IsSelectedByDefault));
    }

    [TestMethod]
    public async Task MonumentValleyEpicInstallUsesDirectoryHintAndWildcardUserId()
    {
        var installRoot = Path.Combine(_root, "MonumentValley2");
        var gameDataRoot = Path.Combine(
            _root,
            "AppData",
            "LocalLow",
            "ustwo games",
            "Monument Valley 2");
        var cloudRoot = Path.Combine(gameDataRoot, "CloudSave");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(cloudRoot);
        using var manifest = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            "Monument Valley 2: Panoramic Edition":
              files:
                "<home>/AppData/LocalLow/ustwo games/Monument Valley 2/CloudSave/<storeUserId>/*.sav":
                  tags: [save]
                  when:
                    - os: windows
                      store: epic
                "<home>/AppData/LocalLow/ustwo games/Monument Valley 2/UserData_<storeUserId>":
                  tags: [save]
                  when:
                    - os: windows
              installDir:
                Monument Valley 2: {}
            """));
        var compiled = new LudusaviManifestCompiler().Compile(
            manifest,
            null,
            null,
            "fixture",
            CancellationToken.None);
        var provider = CreateProvider(
            compiled.Games,
            new[]
            {
                Installation(
                    GameStore.Epic,
                    "d2e5e3fe19f24372a67c44f931a26740",
                    installRoot,
                    "《Monument Valley II》")
            });

        var result = await provider.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        var candidate = result.Candidates.Single();
        Assert.AreEqual(DiscoveryConfidence.Medium, candidate.Installations.Single().Evidence.Single().Confidence);
        var resources = candidate.BackupSets.Single().Resources;
        Assert.HasCount(2, resources);
        var cloud = resources.Single(resource => resource.FixedRoot == Path.GetFullPath(cloudRoot));
        CollectionAssert.AreEqual(new[] { "*/*.sav", "*/*.sav/**" }, cloud.IncludePatterns.ToArray());
        Assert.IsTrue(cloud.FixedRootExists);
        Assert.IsTrue(cloud.IsSelectedByDefault);
        var userData = resources.Single(resource => resource.FixedRoot == Path.GetFullPath(gameDataRoot));
        CollectionAssert.AreEqual(new[] { "UserData_*", "UserData_*/**" }, userData.IncludePatterns.ToArray());
        Assert.IsTrue(userData.Evidence.Single().Description.Contains("single path-segment wildcard", StringComparison.Ordinal));
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
        RootPath = Path.GetDirectoryName(path) ?? string.Empty,
        BasePath = path,
        InstalledGameName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
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
