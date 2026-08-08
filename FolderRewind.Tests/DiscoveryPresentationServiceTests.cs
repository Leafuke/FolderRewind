using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using System.Collections.ObjectModel;

namespace FolderRewind.Tests;

[TestClass]
public sealed class DiscoveryPresentationServiceTests
{
    [TestMethod]
    public void UnsupportedResourcesAreNeverSelectedByDefault()
    {
        var registry = Resource("registry", BackupResourceSupportState.UnsupportedRegistry, selected: false);
        var supported = Resource("save", BackupResourceSupportState.Supported, selected: true);

        Assert.IsFalse(DiscoveryPresentationService.IsSelectedByDefault(registry));
        Assert.IsTrue(DiscoveryPresentationService.IsSelectedByDefault(supported));
    }

    [TestMethod]
    public void ExistingDefinitionReportsOnlyActuallyNewResourceIds()
    {
        var game = Game(Resource("save", BackupResourceSupportState.Supported, selected: true));
        var existing = ExistingOrigin("save");

        Assert.AreEqual(
            DiscoveryCandidateStatus.UpToDate,
            DiscoveryPresentationService.GetStatus(game, new[] { existing }));

        game.BackupSets[0].Resources.Add(Resource("config", BackupResourceSupportState.Supported, selected: true));
        Assert.AreEqual(
            DiscoveryCandidateStatus.NewResources,
            DiscoveryPresentationService.GetStatus(game, new[] { existing }));
    }

    [TestMethod]
    public void FiltersUseAliasesStoreAndStatusTogether()
    {
        var game = Game(Resource("save", BackupResourceSupportState.Supported, selected: true));
        Assert.IsTrue(DiscoveryPresentationService.Matches(
            game,
            DiscoveryCandidateStatus.New,
            "alternate",
            GameStore.Steam,
            DiscoveryCandidateStatus.New));
        Assert.IsFalse(DiscoveryPresentationService.Matches(
            game,
            DiscoveryCandidateStatus.New,
            "alternate",
            GameStore.Epic,
            DiscoveryCandidateStatus.New));
    }

    private static BackupResourceCandidate Resource(
        string id,
        BackupResourceSupportState support,
        bool selected)
    {
        return new BackupResourceCandidate
        {
            ResourceId = id,
            ProviderId = "ludusavi",
            DisplayName = id,
            SupportState = support,
            FixedRoot = $@"C:\Games\{id}",
            IsSelectedByDefault = selected
        };
    }

    private static DiscoveredGameCandidate Game(BackupResourceCandidate resource)
    {
        return new DiscoveredGameCandidate
        {
            StableKey = "ludusavi:test-game",
            Definition = new GameDefinition
            {
                ProviderId = "ludusavi",
                DefinitionId = "test-game",
                DisplayName = "Test Game",
                Aliases = new[] { "Alternate title" }
            },
            Installations =
            {
                new GameInstallation
                {
                    InstallationId = "steam:1",
                    Store = GameStore.Steam,
                    StoreGameId = "1"
                }
            },
            BackupSets =
            {
                new BackupSetCandidate
                {
                    StableKey = "test-game:default",
                    DisplayName = "Test Game",
                    Resources = { resource }
                }
            }
        };
    }

    private static DiscoveryOrigin ExistingOrigin(params string[] resourceIds)
    {
        return new DiscoveryOrigin
        {
            ProviderId = "ludusavi",
            DefinitionId = "test-game",
            ResourceIds = new ObservableCollection<string>(resourceIds)
        };
    }
}
