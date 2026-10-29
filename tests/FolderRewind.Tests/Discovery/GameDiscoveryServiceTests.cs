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
        => CreateSingleResult(providerId);

    [TestMethod]
    public async Task TimeoutRetainsCompletedResultsWithoutWaitingForUncooperativeProvider()
    {
        var slow = new PendingProvider();
        var service = new GameDiscoveryService([new FakeProvider("fast", 1, CreateResult("fast")), slow]);
        var result = await service.DiscoverAsync(new(), null, CancellationToken.None, TimeSpan.FromMilliseconds(50));
        Assert.HasCount(1, result.Candidates);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "discovery-time-budget"));
        slow.Completion.SetException(new IOException("late failure"));
    }

    [TestMethod]
    public async Task CandidateBudgetReportsTruncation()
    {
        var service = new GameDiscoveryService([
            new FakeProvider("first", 1, CreateResult("first")),
            new FakeProvider("second", 1, CreateResult("second"))]);
        var result = await service.DiscoverAsync(new(), null, CancellationToken.None, maxCandidates: 1);
        Assert.HasCount(1, result.Candidates);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "discovery-result-budget"));
    }

    [TestMethod]
    public async Task UserCancellationDoesNotBecomeSuccessfulPartialScan()
    {
        var slow = new PendingProvider();
        var service = new GameDiscoveryService([slow]);
        using var cancellation = new CancellationTokenSource();
        var request = service.DiscoverAsync(new(), null, cancellation.Token, TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);
        slow.Completion.SetResult(new DiscoveryProviderResult { ProviderId = "slow" });
    }

    private sealed class PendingProvider : IGameDiscoveryProvider
    {
        public TaskCompletionSource<DiscoveryProviderResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DiscoveryProviderDescriptor Descriptor { get; } = new() { Id = "slow", DisplayName = "Slow" };
        public Task<DiscoveryProviderResult> DiscoverAsync(DiscoveryRequest request, IProgress<DiscoveryProgress>? progress, CancellationToken token) => Completion.Task;
    }

    private static DiscoveryProviderResult CreateSingleResult(string providerId)
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
