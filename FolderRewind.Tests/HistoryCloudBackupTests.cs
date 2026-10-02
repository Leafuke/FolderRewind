using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryCloudBackupTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindCloudBackupTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }
    private async Task<(HistoryRuntime Runtime, RepresentationId Base, RepresentationId Delta, Dictionary<RepresentationId, string> Paths)> FixtureAsync()
    {
        var runtime = new HistoryRuntime(new FileHistoryRepository(new(Guid.NewGuid().ToString("N")), new(Path.Combine(_root, "history"))));
        await runtime.InitializeAsync();
        var source = SourceId.New(); var first = VersionId.New(); var second = VersionId.New();
        var provenance = HistoryProvenance.Native("test");
        var a = new SourceVersion(first, runtime.ConfigId, source, [], DateTimeOffset.UtcNow, null, CaptureScope.FullSource, CaptureOutcome.Recovered, [], new("a", ""), "a", provenance);
        var b = new SourceVersion(second, runtime.ConfigId, source, [first], DateTimeOffset.UtcNow, null, CaptureScope.FullSource, CaptureOutcome.Recovered, [], new("b", ""), "b", provenance);
        var full = new VersionRepresentation(RepresentationId.New(), first, RepresentationKind.CoreFull, "7z", [], MaterializationFidelity.Exact, null, null, null);
        var delta = new VersionRepresentation(RepresentationId.New(), second, RepresentationKind.CoreSmartDelta, "7z", [full.RepresentationId], MaterializationFidelity.Exact, null, null, null);
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(a), codec.CreateObject(b), codec.CreateObject(full), codec.CreateObject(delta)]));
        await runtime.EnsureIndexCurrentAsync();
        var fullPath = Path.Combine(_root, "full.7z"); var deltaPath = Path.Combine(_root, "delta.7z");
        File.WriteAllText(fullPath, "base-payload"); File.WriteAllText(deltaPath, "delta-payload");
        return (runtime, full.RepresentationId, delta.RepresentationId, new() { [full.RepresentationId] = fullPath, [delta.RepresentationId] = deltaPath });
    }
    private static HistoryMetadataSyncResult Metadata(bool success) => new(success ? HistoryMetadataSyncStatus.Succeeded : HistoryMetadataSyncStatus.Failed, 0, 0, [], [], success ? "" : "metadata unavailable");

    [TestMethod]
    public async Task FinalMetadataFailureDoesNotReportCompletionAndRetryDoesNotReuploadData()
    {
        var fixture = await FixtureAsync(); await using var runtime = fixture.Runtime;
        var transport = new MemoryReplicaTransport(); var calls = 0;
        var service = new HistoryCloudBackupService(runtime, transport, _ => Task.FromResult(Metadata(++calls != 3)));
        Task<string?> Local(RepresentationId id, CancellationToken token) => Task.FromResult<string?>(fixture.Paths.GetValueOrDefault(id));
        var first = await service.UploadClosureAsync([fixture.Delta], Local);
        Assert.AreEqual(2, transport.UploadCount);
        Assert.IsTrue(first.Items.All(item => item.State == HistoryCloudItemState.Uploaded));
        Assert.IsFalse(first.Complete);
        Assert.IsFalse(first.Metadata!.Succeeded);
        var retry = await service.UploadClosureAsync([fixture.Delta], Local);
        Assert.IsTrue(retry.Complete);
        Assert.AreEqual(2, transport.UploadCount);
        Assert.IsTrue(retry.Items.All(item => item.State == HistoryCloudItemState.Reused));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task CancellationDuringUploadDoesNotLockOutTheNextTask(int cancellationPoint)
    {
        var fixture = await FixtureAsync(); await using var runtime = fixture.Runtime;
        using var cancellation = new CancellationTokenSource();
        var barrier = new CancellationBarrier();
        var transport = new MemoryReplicaTransport { CancellationPoint = cancellationPoint, Barrier = barrier };
        var service = new HistoryCloudBackupService(runtime, transport, async token =>
        {
            if (transport.CancellationPoint == 3) await barrier.WaitAsync(token);
            return Metadata(true);
        });
        Task<string?> Local(RepresentationId id, CancellationToken token) => Task.FromResult<string?>(fixture.Paths.GetValueOrDefault(id));
        var operation = service.UploadClosureAsync([fixture.Delta], Local, cancellation.Token);
        HistoryCloudBackupResult canceled;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            canceled = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            barrier.Release.TrySetResult();
            // Drain before disposing Runtime or deleting payloads, including on assertion timeout.
            await operation;
        }
        Assert.IsTrue(canceled.Canceled);
        Assert.IsFalse(canceled.Complete);
        var durable = await runtime.Query.GetStorageReplicasAsync(fixture.Base);
        Assert.HasCount(cancellationPoint == 3 ? 1 : 0, durable);
        foreach (var path in fixture.Paths.Values)
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await using var lease = await runtime.MutationGate.EnterAsync(timeout.Token);
        }
        transport.CancellationPoint = 0;
        using var retryCancellation = new CancellationTokenSource();
        var retryTask = service.UploadClosureAsync([fixture.Delta], Local, retryCancellation.Token);
        HistoryCloudBackupResult retry;
        try { retry = await retryTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { retryCancellation.Cancel(); await retryTask; }
        Assert.IsTrue(retry.Complete);
        var after = await runtime.Query.GetStorageReplicasAsync(fixture.Base);
        Assert.HasCount(1, after);
        if (cancellationPoint == 3) Assert.AreEqual(durable.Single().ReplicaId, after.Single().ReplicaId);
    }

    [TestMethod]
    public async Task ClosureUploadsDependenciesAndRetryReusesVerifiedPayloadAfterMetadataFailure()
    {
        var fixture = await FixtureAsync(); await using var runtime = fixture.Runtime;
        var transport = new MemoryReplicaTransport(); bool failMetadata = true;
        var service = new HistoryCloudBackupService(runtime, transport, _ => Task.FromResult(Metadata(!failMetadata)));
        Task<string?> Local(RepresentationId id, CancellationToken token) => Task.FromResult<string?>(fixture.Paths.GetValueOrDefault(id));
        var interrupted = await service.UploadClosureAsync([fixture.Delta], Local);
        Assert.IsFalse(interrupted.Complete);
        Assert.AreEqual(1, transport.UploadCount);
        failMetadata = false;
        var retry = await service.UploadClosureAsync([fixture.Delta], Local);
        Assert.IsTrue(retry.Complete);
        Assert.AreEqual(2, transport.UploadCount);
        Assert.AreEqual(HistoryCloudItemState.Reused, retry.Items[0].State);
        Assert.AreEqual(fixture.Base, retry.Items[0].RepresentationId);
        Assert.AreEqual(fixture.Delta, retry.Items[1].RepresentationId);
    }

    [TestMethod]
    public async Task CorruptedUploadCannotPublishSuccessfulReplicaOrCloudCompletion()
    {
        var fixture = await FixtureAsync(); await using var runtime = fixture.Runtime;
        var transport = new MemoryReplicaTransport { Corrupt = true };
        var service = new HistoryCloudBackupService(runtime, transport, _ => Task.FromResult(Metadata(true)));
        var result = await service.UploadClosureAsync([fixture.Delta], (id, token) => Task.FromResult<string?>(fixture.Paths.GetValueOrDefault(id)));
        Assert.IsFalse(result.Complete);
        Assert.AreEqual(0, transport.ManifestCount);
        Assert.IsEmpty(await runtime.Query.GetStorageReplicasAsync(fixture.Base));
        Assert.HasCount(2, result.Missing);
    }

    [TestMethod]
    public async Task MetadataAloneCannotMakeMissingLocalClosureComplete()
    {
        var fixture = await FixtureAsync(); await using var runtime = fixture.Runtime;
        var transport = new MemoryReplicaTransport();
        var service = new HistoryCloudBackupService(runtime, transport, _ => Task.FromResult(Metadata(true)));
        var result = await service.UploadClosureAsync([fixture.Delta], (id, token) => Task.FromResult<string?>(null));
        Assert.IsFalse(result.Complete);
        Assert.AreEqual(0, transport.UploadCount);
        Assert.IsNull(result.Metadata);
    }

    private sealed class MemoryReplicaTransport : IHistoryReplicaTransport
    {
        private readonly Dictionary<ReplicaId, byte[]> _payloads = [];
        public int UploadCount { get; private set; }
        public int ManifestCount { get; private set; }
        public bool Corrupt { get; set; }
        public int CancellationPoint { get; set; }
        public CancellationBarrier? Barrier { get; init; }
        public async Task UploadAsync(ReplicaId id, string path, CancellationToken token)
        {
            UploadCount++;
            if (CancellationPoint == 1) await Barrier!.WaitAsync(token);
            _payloads[id] = Corrupt ? [0] : File.ReadAllBytes(path);
        }
        public async Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId id, CancellationToken token)
        {
            if (CancellationPoint == 2) await Barrier!.WaitAsync(token);
            var bytes = _payloads[id];
            return new HistoryReplicaVerification(true, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "");
        }
        public Task CommitManifestOnceAsync(HistoryReplicaManifest manifest, CancellationToken token) { ManifestCount++; return Task.CompletedTask; }
        public Task DownloadAsync(HistoryReplicaManifest manifest, string path, CancellationToken token) => throw new InvalidOperationException();
        public Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class CancellationBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }
}
