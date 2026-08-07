using FolderRewind.Models;
using FolderRewind.Services.Discovery;
using FolderRewind.Services.Plugins;

namespace FolderRewind.Tests;

[TestClass]
public sealed class GameDiscoveryServiceTests
{
    [TestMethod]
    public async Task ProviderFailureBecomesDiagnosticWithoutDroppingOtherResults()
    {
        var service = new GameDiscoveryService(new IFolderRewindDiscoveryProvider[]
        {
            new FakeProvider("working", 10, CreateResult("working")),
            new ThrowingProvider("broken", 20)
        });

        var result = await service.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        Assert.HasCount(1, result.Candidates);
        Assert.HasCount(1, result.Diagnostics);
        Assert.AreEqual("provider-failed", result.Diagnostics[0].Code);
        Assert.AreEqual("broken", result.Diagnostics[0].ProviderId);
    }

    [TestMethod]
    public async Task DuplicateProviderIdUsesHighestPriorityImplementation()
    {
        var service = new GameDiscoveryService(new IFolderRewindDiscoveryProvider[]
        {
            new FakeProvider("same", 1, new DiscoveryProviderResult { ProviderId = "same" }),
            new FakeProvider("same", 5, CreateResult("same"))
        });

        var result = await service.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        Assert.HasCount(1, result.Candidates);
    }

    private static DiscoveryProviderResult CreateResult(string providerId)
    {
        return new DiscoveryProviderResult
        {
            ProviderId = providerId,
            Candidates = new[]
            {
                new DiscoveredGameCandidate
                {
                    StableKey = $"{providerId}:game",
                    Definition = new GameDefinition
                    {
                        ProviderId = providerId,
                        DefinitionId = "game",
                        DisplayName = "Game"
                    }
                }
            }
        };
    }

    private sealed class FakeProvider : IFolderRewindDiscoveryProvider
    {
        private readonly DiscoveryProviderResult _result;

        public FakeProvider(string id, int priority, DiscoveryProviderResult result)
        {
            Descriptor = new DiscoveryProviderDescriptor
            {
                Id = id,
                DisplayName = id,
                Priority = priority
            };
            _result = result;
        }

        public DiscoveryProviderDescriptor Descriptor { get; }

        public Task<DiscoveryProviderResult> DiscoverAsync(
            DiscoveryRequest request,
            IProgress<DiscoveryProgress>? progress,
            CancellationToken cancellationToken) => Task.FromResult(_result);
    }

    private sealed class ThrowingProvider : IFolderRewindDiscoveryProvider
    {
        public ThrowingProvider(string id, int priority)
        {
            Descriptor = new DiscoveryProviderDescriptor
            {
                Id = id,
                DisplayName = id,
                Priority = priority
            };
        }

        public DiscoveryProviderDescriptor Descriptor { get; }

        public Task<DiscoveryProviderResult> DiscoverAsync(
            DiscoveryRequest request,
            IProgress<DiscoveryProgress>? progress,
            CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
