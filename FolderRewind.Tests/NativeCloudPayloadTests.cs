using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeCloudPayloadTests
{
    [TestMethod]
    public async Task SuccessfulDownloadReleasesEquivalentStoppedAttemptsAndPreservesActiveOrForeignAttempts()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var remote = new MemoryTransport();
        Assert.IsTrue((await f.UploadAsync(f.Job("a"), remote)).Complete);
        var recovered = new NativeArtifactStore(Path.Combine(f.Root, "recovered"), new ZipCodec());
        var native = new NativeArtifactReplicaService(recovered, f.Replicas(remote));
        NativeArtifactTransfer Attempt(string representation)
        {
            var replica = ReplicaId.New().ToString();
            return new("download-" + replica, f.ArtifactRoot, representation, replica, false);
        }
        var stopped = Attempt(f.Representation.RepresentationId.ToString());
        var active = Attempt(f.Representation.RepresentationId.ToString());
        var foreign = Attempt(RepresentationId.New().ToString());
        foreach (var attempt in new[] { stopped, active, foreign }) await recovered.SaveTransferAsync(attempt, default);
        var directory = Path.Combine(recovered.Root, "transfers");
        using (var live = new FileStream(Path.Combine(directory, active.Id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await native.DownloadAsync(f.Representation, remote.Manifests.Values.Single(), f.ConfigId, f.Source.Value, default);
            Assert.AreEqual(HistoryReplicaOperationStatus.Succeeded, result.Status, result.Diagnostic);
            CollectionAssert.AreEquivalent(new[] { active.Id, foreign.Id }, recovered.Transfers().Select(t => t.Id).ToArray());
        }
        Assert.AreEqual(HistoryReplicaOperationStatus.Succeeded,
            (await native.DownloadAsync(f.Representation, remote.Manifests.Values.Single(), f.ConfigId, f.Source.Value, default)).Status);
        Assert.AreEqual(foreign.Id, recovered.Transfers().Single().Id);
    }

    [TestMethod]
    public async Task JobPersistsOneIdentityBeforeObjectPublicationAndRetryRepairsMissingClosureEvidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var remote = new MemoryTransport();
        var job = f.Job("a");
        remote.BeforeObjectWrite = () =>
        {
            var intent = f.History.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId).UploadIntents.Single();
            Assert.AreEqual(job.UploadIntents.Single().ReplicaId, intent.ReplicaId);
            Assert.IsNotNull(intent.Manifest);
        };
        var first = await f.UploadAsync(job, remote);
        Assert.IsTrue(first.Complete); Assert.AreEqual(2, remote.ObjectWrites); Assert.AreEqual(1, remote.PayloadWrites);
        Assert.AreEqual(job.UploadIntents.Single().ReplicaId, (await f.History.Query.GetStorageReplicasAsync(f.Representation.RepresentationId)).Single().ReplicaId);
        Assert.AreEqual(HistoryCloudItemState.Reused, (await f.UploadAsync(job, remote)).Items.Single().State);
        remote.Receipts.Remove(remote.Objects.Keys.First());
        Assert.IsTrue((await f.UploadAsync(job, remote)).Complete);
        Assert.AreEqual(3, remote.ObjectWrites, "A descriptor alone cannot prove closure availability.");
        remote.Objects.Remove(remote.Objects.Keys.First());
        Assert.IsTrue((await f.UploadAsync(job, remote)).Complete);
        Assert.AreEqual(4, remote.ObjectWrites);
    }

    [TestMethod]
    public async Task TwoTargetsUseDifferentJobReplicasAndAFrozenTargetCannotBeRebound()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var a = f.Job("a"); var b = f.Job("b"); var first = new MemoryTransport(); var second = new MemoryTransport();
        Assert.IsTrue((await f.UploadAsync(a, first)).Complete);
        Assert.IsTrue((await f.UploadAsync(b, second)).Complete);
        Assert.AreNotEqual(a.UploadIntents.Single().ReplicaId, b.UploadIntents.Single().ReplicaId);
        Assert.AreEqual(2, second.ObjectWrites);
        var replica = f.Replicas(first);
        var protocol = new NativeArtifactReplicaService(f.Store, replica).ForJob(f.History,
            a with { Target = a.Target with { RemoteRoot = "other" } }, (_, _) => Task.FromResult<string?>(null), f.ConfigId);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => protocol.PrepareAsync(f.Representation, a.UploadIntents.Single().ReplicaId, default));
        Assert.HasCount(2, await f.History.Query.GetStorageReplicasAsync(f.Representation.RepresentationId));
    }

    [TestMethod]
    public async Task PublishedJobIntentCannotDropItsManifestOrAcquireAnotherReplicaIdentity()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var job = f.Job("a"); var remote = new MemoryTransport();
        Assert.IsTrue((await f.UploadAsync(job, remote)).Complete);
        var current = f.History.CloudJobs.Read().Jobs.Single(); var intent = current.UploadIntents.Single();
        Assert.ThrowsExactly<InvalidOperationException>(() => f.History.CloudJobs.Update(current with
            { UploadIntents = [intent with { Manifest = null }] }));
        Assert.ThrowsExactly<InvalidOperationException>(() => f.History.CloudJobs.Update(current with
            { UploadIntents = [new(intent.RepresentationId, ReplicaId.New(), null)] }));
        Assert.IsNotNull(f.History.CloudJobs.Read().Jobs.Single().UploadIntents.Single().Manifest);
        var succeeded = current with { Phase = CloudJobPhase.Succeeded, Protection = CloudProtectionDisposition.Released };
        f.History.CloudJobs.Update(succeeded);
        Assert.ThrowsExactly<InvalidOperationException>(() => f.History.CloudJobs.Update(succeeded with { Phase = CloudJobPhase.RetryableFailure }));
    }

    [TestMethod]
    public async Task CancellationKeepsTransferRootUntilStoppedJobIsExplicitlyAbandoned()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var job = f.Job("a");
        using var cancellation = new CancellationTokenSource();
        var remote = new MemoryTransport { AfterObjectWrite = cancellation.Cancel };
        var canceled = await f.UploadAsync(job, remote, cancellation.Token);
        Assert.IsTrue(canceled.Canceled); Assert.IsEmpty(remote.Manifests);
        Assert.IsEmpty(await f.History.Query.GetStorageReplicasAsync(f.Representation.RepresentationId));
        Assert.IsEmpty(await f.Store.CollectAsync([], default), "The canceled transfer must retain its complete local retry closure.");
        var intent = Directory.EnumerateFiles(Path.Combine(f.Store.Root, "transfers"), "*.json").Single();
        Assert.IsFalse(ArtifactMetadataFile.ReadJson<NativeArtifactTransfer>(intent, 1024 * 1024)!.Complete);
        var current = f.History.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId);
        f.History.CloudJobs.Update(current with { Phase = CloudJobPhase.Canceled, Protection = CloudProtectionDisposition.Abandoned });
        await NativeArtifactReplicaService.CompleteJobAsync(f.Store, job.JobId, default);
        Assert.HasCount(2, await f.Store.CollectAsync([], default));
    }

    [TestMethod]
    public async Task AvailabilityReuseDoesNotClaimDeepIntegrityAndCorruptRestoreCannotRegisterLocalBytes()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var remote = new MemoryTransport(); var job = f.Job("a");
        Assert.IsTrue((await f.UploadAsync(job, remote)).Complete);
        remote.Objects.Values.First()[0] ^= 0x40; // Same length, receipt intentionally unchanged.
        var reused = await f.UploadAsync(job, remote);
        Assert.IsTrue(reused.Complete); Assert.AreEqual(HistoryCloudItemState.Reused, reused.Items.Single().State);
        var local = new NativeArtifactStore(Path.Combine(f.Root, "recovered"), new ZipCodec());
        var native = new NativeArtifactReplicaService(local, f.Replicas(remote));
        var result = await native.DownloadAsync(f.Representation, remote.Manifests.Values.Single(), f.History.ConfigId.Value, f.Source.Value, default);
        Assert.AreEqual(HistoryReplicaOperationStatus.Failed, result.Status);
        Assert.IsEmpty((await local.Ledger.LoadAsync()).Artifacts);
        Assert.IsEmpty((await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }

    [TestMethod]
    public async Task RemoteOnlyPreparationDownloadsAndRegistersTheNativeRootRatherThanTheDescriptor()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var remote = new MemoryTransport(); var job = f.Job("a");
        Assert.IsTrue((await f.UploadAsync(job, remote)).Complete);
        var recovered = new NativeArtifactStore(Path.Combine(f.Root, "recovered"), new ZipCodec());
        var native = new NativeArtifactReplicaService(recovered, f.Replicas(remote));
        // Reuse does not depend on a local ledger or local ciphertext.
        var service = new HistoryCloudBackupService(f.History, remote, _ => Task.FromResult(Metadata()));
        var reuse = await service.UploadClosureAsync([f.Representation.RepresentationId], (_, _) => Task.FromResult<string?>(null),
            payloads: native.ForJob(f.History, job, (_, _) => Task.FromResult<string?>(null), f.ConfigId));
        Assert.IsTrue(reuse.Complete);
        var result = await native.DownloadAsync(f.Representation, remote.Manifests.Values.Single(), f.ConfigId, f.Source.Value, default);
        Assert.AreEqual(HistoryReplicaOperationStatus.Succeeded, result.Status, result.Diagnostic);
        await using (await recovered.OpenAsync(f.ArtifactRoot, default)) { }
        var entry = (await f.History.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries.Single();
        Assert.AreEqual(recovered.PayloadPath((await recovered.Ledger.LoadAsync()).Artifacts.Single(n => n.ArtifactId == f.ArtifactRoot)), entry.Locator.AbsolutePath);
        Assert.IsFalse(entry.Locator.AbsolutePath.EndsWith(".bundle", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("descriptor")]
    [DataRow("manifest")]
    [DataRow("metadata")]
    public async Task PublicationBoundaryFailuresRetryTheSameDurableReplica(string boundary)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var job = f.Job("a");
        var remote = new MemoryTransport { FailAt = boundary };
        var failed = await f.UploadAsync(job, remote);
        Assert.IsFalse(failed.Complete);
        remote.FailAt = null;
        Assert.IsTrue((await f.UploadAsync(job, remote)).Complete);
        Assert.HasCount(1, remote.Manifests);
        Assert.AreEqual(job.UploadIntents.Single().ReplicaId, remote.Manifests.Keys.Single());
        Assert.HasCount(1, await f.History.Query.GetStorageReplicasAsync(f.Representation.RepresentationId));
        Assert.AreEqual(2, remote.ObjectWrites);
    }

    [TestMethod]
    public async Task RealRcloneLocalBackendRoundtripsNativeClosureAndRejectsOversizedDescriptorReadback()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        await using var f = new Fixture(); await f.InitializeAsync();
        var conf = Path.Combine(f.Root, "rclone.conf"); await File.WriteAllTextAsync(conf, "[test]\ntype = local\n");
        var remoteRoot = Path.Combine(f.Root, "remote");
        using var context = new RcloneExecutionContext(executable, conf, f.Root, "test:remote", Path.Combine(f.Root, "tasks"));
        var transport = new RcloneNativeHistoryTransport(context, 30); var job = f.Job(context.RemoteRoot);
        var uploaded = await f.UploadAsync(job, transport);
        Assert.IsTrue(uploaded.Complete, JsonSerializer.Serialize(uploaded));
        Assert.AreEqual(HistoryCloudItemState.Reused, (await f.UploadAsync(job, transport)).Items.Single().State);
        var manifest = await transport.ReadManifestAsync(job.UploadIntents.Single().ReplicaId, default);
        var recovered = new NativeArtifactStore(Path.Combine(f.Root, "recovered"), new ZipCodec());
        var result = await new NativeArtifactReplicaService(recovered, f.Replicas(transport))
            .DownloadAsync(f.Representation, manifest!, f.ConfigId, f.Source.Value, default);
        Assert.AreEqual(HistoryReplicaOperationStatus.Succeeded, result.Status, result.Diagnostic);
        var payload = Path.Combine(remoteRoot, "_folderrewind", "replicas", manifest!.ReplicaId.ToString(), "payload");
        await using (var output = new FileStream(payload, FileMode.Append)) await output.WriteAsync(new byte[65536]);
        Assert.IsFalse((await transport.VerifyRemoteAsync(manifest, default)).Success);
    }

    private static HistoryMetadataSyncResult Metadata(bool success = true) => new(success ? HistoryMetadataSyncStatus.Succeeded : HistoryMetadataSyncStatus.Failed, 0, 0, [], [], success ? "" : "injected metadata failure");

    internal sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "NativeCloudPayload", Guid.NewGuid().ToString("N"));
        public string ConfigId { get; } = Guid.NewGuid().ToString("D");
        public SourceId Source { get; } = SourceId.New();
        public ArtifactId ArtifactRoot { get; } = new(Guid.NewGuid());
        public HistoryRuntime History { get; private set; } = null!;
        public NativeArtifactStore Store { get; private set; } = null!;
        public VersionRepresentation Representation { get; private set; } = null!;
        private HistoryCommitPack _pack = null!;
        public async Task InitializeAsync(bool supported = true)
        {
            History = new(new FileHistoryRepository(new(ConfigId), new(Path.Combine(Root, "history")))); await History.InitializeAsync();
            await History.LocalReplicaCatalogStore.SaveAsync(new(History.ConfigId, 0, []), -1);
            Store = new(Path.Combine(Root, "store"), new ZipCodec());
            var nodes = new List<ArtifactLedgerEntry>();
            foreach (var id in new[] { new ArtifactId(Guid.NewGuid()), ArtifactRoot })
            {
                var logical = Path.Combine(Root, "input"); Directory.CreateDirectory(logical); await File.WriteAllTextAsync(Path.Combine(logical, "file"), id.ToString());
                await using var staging = Store.Ledger.CreateStagingArea(Guid.NewGuid().ToString("N"));
                var allocation = await staging.CreateArtifactAsync(default);
                await using (var output = await staging.OpenWriteAsync(allocation.Staging, "file", default))
                    await output.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(logical, "file")));
                var facts = (await staging.SealAsync("artifacts"))[allocation.Staging];
                var directory = Path.Combine(Store.Root, "artifacts", id.Value.ToString("N")); HostArtifactWorkspace.CreatePrivateDirectory(directory);
                var payload = Path.Combine(directory, "payload.7z"); await new ZipCodec().PackAsync(logical, payload, false, default);
                await File.WriteAllBytesAsync(Path.Combine(directory, "envelope.json"), JsonSerializer.SerializeToUtf8Bytes(new NativeEnvelope(1, false)));
                var bytes = await File.ReadAllBytesAsync(payload);
                nodes.Add(new(id, new(new("plugin.test"), "native"), 1, new(new("plugin.test"), "restore"), ConfigId, Source.Value,
                    "artifacts/" + id.Value.ToString("N"), facts.LogicalSha256, facts.LogicalSize, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
                    ArtifactCompleteness.Complete, CoreCaptureMode.Full, nodes.Select(n => n.ArtifactId).ToArray(), Guid.NewGuid().ToString("N"),
                    ArtifactAvailability.Available, ArtifactAvailability.Missing) { LogicalEntryCount = 1 });
            }
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            await File.WriteAllTextAsync(Path.Combine(Store.Root, ".folderrewind", "artifact-ledger.v2.json"), JsonSerializer.Serialize(
                new ArtifactLedgerDocument(ArtifactLedgerValidator.CurrentSchemaVersion, new(Guid.NewGuid().ToString("N")), nodes), options));
            var version = new SourceVersion(VersionId.New(), History.ConfigId, Source, [], DateTimeOffset.UtcNow, null, CaptureScope.FullSource,
                CaptureOutcome.Captured, [], new("test", "isolated"), null, HistoryProvenance.Native("test"));
            Representation = new(RepresentationId.New(), version.VersionId, RepresentationKind.PluginArtifact, "native", [], MaterializationFidelity.Exact,
                null, null, new Dictionary<string, string> { ["artifactRootId"] = ArtifactRoot.Value.ToString("N"), ["storageSha256"] = nodes.Last().StorageSha256,
                    ["nativeArtifactContractVersion"] = supported ? "2" : "1", ["incrementalEvidence"] = supported ? "2" : "1",
                    ["pluginId"] = "plugin.test", ["nativeArtifactEnvelope"] = "1" });
            var codec = new HistoryPackCodec();
            _pack = new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, [codec.CreateObject(version), codec.CreateObject(Representation)]);
            await History.Repository.CommitAsync(_pack); await History.EnsureIndexCurrentAsync();
        }
        public HistoryCloudJob Job(string target)
        {
            var job = History.CloudJobs.RegisterManual(_pack, new("fixture", "revision", "binding", target, History.ConfigId.Value), [Representation.RepresentationId]);
            if (job.UploadIntents.IsEmpty) { job = job with { UploadIntents = [new(Representation.RepresentationId, ReplicaId.New(), null)] }; History.CloudJobs.Update(job); }
            return job;
        }
        public HistoryReplicaSyncService Replicas(IHistoryReplicaTransport transport) => new(History, transport,
            _ => Task.FromResult(Metadata(transport is not MemoryTransport { FailAt: "metadata" })), new NoRetirement());
        public Task<HistoryCloudBackupResult> UploadAsync(HistoryCloudJob job, IHistoryReplicaTransport transport, CancellationToken token = default)
        {
            var service = new HistoryCloudBackupService(History, transport, _ => Task.FromResult(Metadata(transport is not MemoryTransport { FailAt: "metadata" })));
            return service.UploadClosureAsync([Representation.RepresentationId], (_, _) => Task.FromResult<string?>(null), token,
                _ => job.UploadIntents.Single().ReplicaId, async (manifest, ct) =>
                {
                    await using var held = await History.MutationGate.EnterAsync(ct);
                    var current = History.CloudJobs.Read().Jobs.Single(j => j.JobId == job.JobId); var intent = current.UploadIntents.Single();
                    if (intent.Manifest is { } previous && !HistoryCloudReplicaValidity.Matches(manifest, previous)) throw new InvalidDataException("Changed descriptor.");
                    History.CloudJobs.Update(current with { UploadIntents = [intent with { Manifest = manifest }] });
                }, new NativeArtifactReplicaService(Store, Replicas(transport)).ForJob(History, job, (_, _) => Task.FromResult<string?>(null), ConfigId));
        }
        public async ValueTask DisposeAsync() { if (History is not null) await History.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    internal sealed class ZipCodec : IBoundedArtifactEnvelopeCodec
    {
        public Task PackAsync(string source, string payload, bool encrypted, CancellationToken token) { ZipFile.CreateFromDirectory(source, payload); return Task.CompletedTask; }
        public Task UnpackAsync(string payload, string target, bool encrypted, CancellationToken token) => throw new AssertFailedException("Bounded decode required.");
        public Task UnpackAsync(string payload, string target, bool encrypted, long maximumBytes, ArtifactExtractionBudget budget, CancellationToken token)
        {
            using var zip = ZipFile.OpenRead(payload);
            var bytes = zip.Entries.Sum(e => e.Length); if (bytes > maximumBytes) throw new InvalidDataException();
            budget.Reserve(bytes, ArtifactExtractionBudget.CountEntries(zip.Entries.Select(e => e.FullName)));
            ZipFile.ExtractToDirectory(payload, target); return Task.CompletedTask;
        }
    }
    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    { public Task EnsureRetirementSafeAsync(StorageReplica replica, bool releaseVersion, CancellationToken token) => throw new AssertFailedException(); }

    private sealed class MemoryTransport : IHistoryReplicaTransport, INativeArtifactObjectTransport
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public HashSet<string> Receipts { get; } = [];
        public Dictionary<ReplicaId, byte[]> Payloads { get; } = [];
        public Dictionary<ReplicaId, HistoryReplicaManifest> Manifests { get; } = [];
        public int ObjectWrites { get; private set; }
        public int PayloadWrites { get; private set; }
        public Action? BeforeObjectWrite { get; set; }
        public Action? AfterObjectWrite { get; set; }
        public string? FailAt { get; set; }
        public Task<bool> HasVerifiedObjectAsync(string key, long size, string hash, CancellationToken token)
            => Task.FromResult(Objects.TryGetValue(key, out var bytes) && bytes.LongLength == size && Receipts.Contains(key));
        public Task UploadObjectAsync(string key, string path, long size, string hash, ArtifactTransferBudget budget, CancellationToken token)
        { BeforeObjectWrite?.Invoke(); Objects[key] = File.ReadAllBytes(path); Receipts.Add(key); ObjectWrites++; AfterObjectWrite?.Invoke(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public async Task DownloadObjectAsync(string key, string path, long expectedLength, ArtifactTransferBudget budget, CancellationToken token)
        { await using var target = File.Create(path); await budget.CopyAsync(new MemoryStream(Objects[key]), target, expectedLength, token); }
        public Task UploadAsync(ReplicaId id, string path, CancellationToken token)
        { PayloadWrites++; Payloads[id] = File.ReadAllBytes(path); if (FailAt == "descriptor") throw new IOException("injected descriptor publication failure"); return Task.CompletedTask; }
        public Task<HistoryReplicaVerification> VerifyRemoteAsync(ReplicaId id, CancellationToken token)
        { var bytes = Payloads[id]; return Task.FromResult(new HistoryReplicaVerification(true, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "")); }
        public Task CommitManifestOnceAsync(HistoryReplicaManifest manifest, CancellationToken token)
        { Manifests.TryAdd(manifest.ReplicaId, manifest); if (FailAt == "manifest") throw new IOException("injected manifest publication failure"); return Task.CompletedTask; }
        public Task<HistoryReplicaManifest?> ReadManifestAsync(ReplicaId id, CancellationToken token) => Task.FromResult(Manifests.GetValueOrDefault(id));
        public Task DownloadAsync(HistoryReplicaManifest manifest, string path, CancellationToken token) => File.WriteAllBytesAsync(path, Payloads[manifest.ReplicaId], token);
        public Task DeletePhysicalAsync(HistoryReplicaManifest manifest, CancellationToken token) => throw new AssertFailedException();
    }
}
