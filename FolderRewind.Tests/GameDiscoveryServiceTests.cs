using FolderRewind.Models;
using FolderRewind.Services.Discovery;

namespace FolderRewind.Tests;

[TestClass]
public sealed class GameDiscoveryServiceTests
{
    [TestMethod]
    public async Task ProviderFailureBecomesDiagnosticWithoutDroppingOtherResults()
    {
        var service = new GameDiscoveryService(new IGameDiscoveryProvider[]
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
        var service = new GameDiscoveryService(new IGameDiscoveryProvider[]
        {
            new FakeProvider("same", 1, new DiscoveryProviderResult { ProviderId = "same" }),
            new FakeProvider("same", 5, CreateResult("same"))
        });

        var result = await service.DiscoverAsync(new DiscoveryRequest(), null, CancellationToken.None);

        Assert.HasCount(1, result.Candidates);
    }

    [TestMethod]
    public async Task TargetedRequestReportsUnavailableProviderWithoutScanningOthers()
    {
        var unrelated = new RecordingProvider("other");
        var service = new GameDiscoveryService([unrelated]);

        var result = await service.DiscoverAsync(
            new DiscoveryRequest
            {
                Mode = DiscoveryRequestMode.PresetTargeted,
                Definitions =
                [
                    new DiscoveryDefinitionReference
                    {
                        ProviderId = "com.example.missing",
                        DefinitionId = "game"
                    }
                ]
            },
            null,
            CancellationToken.None);

        Assert.AreEqual(0, unrelated.CallCount);
        var diagnostic = result.Diagnostics.Single();
        Assert.AreEqual("provider-unavailable", diagnostic.Code);
        Assert.AreEqual("com.example.missing", diagnostic.ProviderId);
    }

    [TestMethod]
    public async Task TargetedRequestKeepsSpecificCompositionDiagnosticInsteadOfGenericOne()
    {
        var service = new GameDiscoveryService(
            Array.Empty<IGameDiscoveryProvider>(),
            [
                new DiscoveryDiagnostic
                {
                    Severity = DiscoveryDiagnosticSeverity.Error,
                    Code = "definition-catalog-unavailable",
                    Message = "upgrade",
                    ProviderId = "com.example.legacy"
                }
            ]);

        var result = await service.DiscoverAsync(
            new DiscoveryRequest
            {
                Mode = DiscoveryRequestMode.PresetTargeted,
                Definitions =
                [
                    new DiscoveryDefinitionReference
                    {
                        ProviderId = "com.example.legacy",
                        DefinitionId = "game"
                    }
                ]
            },
            null,
            CancellationToken.None);

        Assert.HasCount(1, result.Diagnostics);
        Assert.AreEqual("definition-catalog-unavailable", result.Diagnostics[0].Code);
    }

    [TestMethod]
    public async Task TargetedPluginProviderRunsWithoutInvokingLudusavi()
    {
        var plugin = new RecordingProvider("com.example.plugin", CreateResult("com.example.plugin"));
        var ludusavi = new RecordingProvider("ludusavi");
        var service = new GameDiscoveryService([ludusavi, plugin]);

        var result = await service.DiscoverAsync(
            new DiscoveryRequest
            {
                Mode = DiscoveryRequestMode.PresetTargeted,
                Definitions =
                [
                    new DiscoveryDefinitionReference
                    {
                        ProviderId = "COM.EXAMPLE.PLUGIN",
                        DefinitionId = "game"
                    }
                ]
            },
            null,
            CancellationToken.None);

        Assert.AreEqual(1, plugin.CallCount);
        Assert.AreEqual(0, ludusavi.CallCount);
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

    private sealed class FakeProvider : IGameDiscoveryProvider
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

    private sealed class ThrowingProvider : IGameDiscoveryProvider
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

    private sealed class RecordingProvider : IGameDiscoveryProvider
    {
        private readonly DiscoveryProviderResult _result;

        public RecordingProvider(string id, DiscoveryProviderResult? result = null)
        {
            Descriptor = new DiscoveryProviderDescriptor
            {
                Id = id,
                DisplayName = id
            };
            _result = result ?? new DiscoveryProviderResult { ProviderId = id };
        }

        public int CallCount { get; private set; }
        public DiscoveryProviderDescriptor Descriptor { get; }

        public Task<DiscoveryProviderResult> DiscoverAsync(
            DiscoveryRequest request,
            IProgress<DiscoveryProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }
}
