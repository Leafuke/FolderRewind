using FolderRewind.Models;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiscoveryCandidateMergerTests
{
    [TestMethod]
    public void StrongStoreIdentityMergesInstallationsAcrossProviders()
    {
        var first = CreateGame("ludusavi:hades", "Hades", "70", GameStore.Steam, "C:\\Steam\\Hades");
        var second = CreateGame("store:hades", "Hades game", "70", GameStore.Standalone, "D:\\Games\\Hades");

        var merged = DiscoveryCandidateMerger.Merge(new[]
        {
            Result("ludusavi", first),
            Result("launcher", second)
        });

        Assert.HasCount(1, merged);
        Assert.HasCount(2, merged[0].Installations);
    }

    [TestMethod]
    public void CommaSeparatedUpstreamStoreIdsAreMatchedIndividually()
    {
        var first = CreateGame("ludusavi:game", "Game", "69,70", GameStore.Steam, "C:\\Steam\\Game");
        var second = CreateGame("launcher:game", "Game", "70", GameStore.Steam, "D:\\Steam\\Game");

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("provider", first, second) });

        Assert.HasCount(1, merged);
    }

    [TestMethod]
    public void SpecializedResourceSuppressesOnlyOverlappingGenericResource()
    {
        var generic = CreateResource(
            "ludusavi:saves",
            "ludusavi",
            "C:\\Games\\Example\\saves",
            specialized: false,
            priority: 10);
        var genericConfig = CreateResource(
            "ludusavi:config",
            "ludusavi",
            "C:\\Games\\Example\\config",
            specialized: false,
            priority: 10);
        var specialized = CreateResource(
            "minerewind:saves",
            "minerewind",
            "C:\\Games\\Example\\saves",
            specialized: true,
            priority: 100);

        var game = CreateGame("game:example", "Example", "10", GameStore.Steam, "C:\\Games\\Example");
        game.BackupSets[0].Resources.Add(generic);
        game.BackupSets[0].Resources.Add(genericConfig);
        game.BackupSets[0].Resources.Add(specialized);

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("combined", game) });
        var resources = merged[0].BackupSets[0].Resources;

        Assert.IsTrue(resources.Single(item => item.ResourceId == generic.ResourceId).IsSuppressed);
        Assert.AreEqual("minerewind", resources.Single(item => item.ResourceId == generic.ResourceId).SuppressedByProviderId);
        Assert.IsFalse(resources.Single(item => item.ResourceId == genericConfig.ResourceId).IsSuppressed);
        Assert.IsFalse(resources.Single(item => item.ResourceId == specialized.ResourceId).IsSuppressed);
    }

    [TestMethod]
    public void MergedUiGameKeepsProviderSetIdentitiesSeparate()
    {
        var ludusavi = CreateGame("ludusavi:game", "Game", "42", GameStore.Steam, "C:\\Games\\Game");
        ludusavi.BackupSets[0].Identity.ProviderId = "ludusavi";
        var specialized = CreateGame("specialized:game", "Game", "42", GameStore.Steam, "C:\\Games\\Game");
        specialized.BackupSets[0].Identity.ProviderId = "specialized";

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("all", ludusavi, specialized) });

        Assert.HasCount(1, merged);
        Assert.HasCount(2, merged[0].BackupSets);
        CollectionAssert.AreEquivalent(
            new[] { "ludusavi", "specialized" },
            merged[0].BackupSets.Select(set => set.Identity.ProviderId).ToArray());
    }

    [TestMethod]
    public void NestedSpecializedRangeWarnsWithoutSuppressingGenericRange()
    {
        var generic = CreateResource(
            "ludusavi:all",
            "ludusavi",
            "C:\\Games\\Example",
            specialized: false,
            priority: 10);
        var specialized = CreateResource(
            "specialized:saves",
            "specialized",
            "C:\\Games\\Example\\saves",
            specialized: true,
            priority: 100);
        var game = CreateGame("game:overlap", "Example", "10", GameStore.Steam, "C:\\Games\\Example");
        game.BackupSets[0].Resources.Add(generic);
        game.BackupSets.Add(new BackupSetCandidate
        {
            StableKey = "specialized",
            Identity = new DiscoverySetIdentity
            {
                ProviderId = "specialized",
                DefinitionId = "example",
                SetId = "main"
            },
            DisplayName = "Specialized",
            Resources = { specialized }
        });

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("combined", game) });

        Assert.IsFalse(generic.IsSuppressed);
        Assert.IsFalse(specialized.IsSuppressed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(generic.ConflictWarning));
        Assert.IsFalse(string.IsNullOrWhiteSpace(specialized.ConflictWarning));
    }

    [TestMethod]
    public void SimilarDisplayNameWithoutStrongEvidenceDoesNotMerge()
    {
        var first = CreateGame("one", "The Game", "1", GameStore.Steam, "C:\\One");
        var second = CreateGame("two", "The Game", "2", GameStore.Steam, "C:\\Two");

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("provider", first, second) });

        Assert.HasCount(2, merged);
    }

    [TestMethod]
    public void AliasOnlyLowEvidenceDoesNotMerge()
    {
        var first = CreateGame("one", "Game One", string.Empty, GameStore.Unknown, string.Empty, new[] { "Known Alias" });
        var second = CreateGame("two", "Known Alias", string.Empty, GameStore.Unknown, string.Empty);

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("provider", first, second) });

        Assert.HasCount(2, merged);
    }

    [TestMethod]
    public void CaseDistinctDefinitionsFromSameProviderDoNotAliasMerge()
    {
        var upper = CreateGame(
            "ludusavi:AFTERLIFE (2021)",
            "AFTERLIFE (2021)",
            string.Empty,
            GameStore.Unknown,
            "C:\\Games\\Upper",
            new[] { "AFTERLIFE" });
        var titleCase = CreateGame(
            "ludusavi:Afterlife",
            "Afterlife",
            string.Empty,
            GameStore.Unknown,
            "C:\\Games\\TitleCase");
        upper.BackupSets[0].Resources.Add(CreateResource(
            "upper",
            "test",
            "C:\\Games\\Upper",
            specialized: false,
            priority: 10));
        titleCase.BackupSets[0].Resources.Add(CreateResource(
            "title-case",
            "test",
            "C:\\Games\\TitleCase",
            specialized: false,
            priority: 10));

        var merged = DiscoveryCandidateMerger.Merge(new[] { Result("test", upper, titleCase) });

        Assert.HasCount(2, merged);
    }

    private static DiscoveryProviderResult Result(string providerId, params DiscoveredGameCandidate[] candidates) =>
        new()
        {
            ProviderId = providerId,
            Candidates = candidates
        };

    private static DiscoveredGameCandidate CreateGame(
        string key,
        string name,
        string steamId,
        GameStore store,
        string path,
        IReadOnlyList<string>? aliases = null)
    {
        return new DiscoveredGameCandidate
        {
            StableKey = key,
            Definition = new GameDefinition
            {
                ProviderId = "test",
                DefinitionId = key,
                DisplayName = name,
                Aliases = aliases ?? Array.Empty<string>(),
                ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["steam"] = steamId
                }
            },
            Installations = new List<GameInstallation>
            {
                new()
                {
                    InstallationId = $"{store}:{steamId}:{path}",
                    Store = store,
                    StoreGameId = steamId,
                    BasePath = path,
                    RootPath = Path.GetDirectoryName(path) ?? string.Empty,
                    InstalledGameName = Path.GetFileName(path)
                }
            },
            BackupSets = new List<BackupSetCandidate>
            {
                new()
                {
                    StableKey = "main",
                    Identity = new DiscoverySetIdentity
                    {
                        ProviderId = "test",
                        DefinitionId = key,
                        SetId = "main",
                        ExternalIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["steam"] = steamId
                        }
                    },
                    DisplayName = name
                }
            }
        };
    }

    private static BackupResourceCandidate CreateResource(
        string id,
        string provider,
        string root,
        bool specialized,
        int priority)
    {
        return new BackupResourceCandidate
        {
            ResourceId = id,
            ProviderId = provider,
            ProviderPriority = priority,
            IsSpecializedProvider = specialized,
            DisplayName = id,
            Kind = BackupResourceKind.Directory,
            FixedRoot = root,
            Evidence = new[]
            {
                new DiscoveryEvidence
                {
                    Confidence = DiscoveryConfidence.High,
                    Kind = "test",
                    Description = "test"
                }
            }
        };
    }
}
