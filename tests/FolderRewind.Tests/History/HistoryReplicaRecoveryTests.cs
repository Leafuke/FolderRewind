using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryReplicaRecoveryTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindReplicaRecovery", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private async Task<(HistoryRuntime Runtime, VersionRepresentation Representation, string Backup)> CreateAsync(bool hash = false)
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N")); var source = SourceId.New();
        var runtime = new HistoryRuntime(new FileHistoryRepository(id, new(Path.Combine(_root, "history")))); await runtime.InitializeAsync();
        var version = new SourceVersion(VersionId.New(), id, source, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Recovered, [], new("source", "source"), null, HistoryProvenance.Native("test"));
        var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull,
            "7z", [], MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["fileName"] = "original.7z" });
        var codec = new HistoryPackCodec(); var facts = new List<object> { version, representation };
        var backup = Path.Combine(_root, "backup"); Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "original.7z"), "verified-payload"u8.ToArray()); File.WriteAllBytes(Path.Combine(backup, "extra.7z"), "extra-payload"u8.ToArray());
        if (hash) facts.Add(new StorageReplica(ReplicaId.New(), representation.RepresentationId, ReplicaProviderKind.Cloud,
            "payload", 16, Convert.ToHexString(SHA256.HashData("verified-payload"u8.ToArray())).ToLowerInvariant(), HistoryProvenance.Native("test")));
        await runtime.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, facts.Select(fact => codec.CreateObject(fact))));
        await runtime.EnsureIndexCurrentAsync(); runtime.MigrationState.MarkImported();
        await new HistoryMigrationInitializationService(runtime).InitializeAsync([source]); return (runtime, representation, backup);
    }
    [TestMethod] public async Task OldExportRequiresConfirmationAndRestoresOnlyItsDeclaredArchive()
    {
        var fixture = await CreateAsync(); await using var runtime = fixture.Runtime;
        var service = new HistoryReplicaRecoveryService(runtime); var preview = await service.InspectAsync(fixture.Backup);
        Assert.HasCount(1, preview.Candidates); Assert.IsFalse(preview.Candidates[0].IdentityVerified);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ApplyAsync(preview, preview.Candidates, new Archive(), false));
        var result = await service.ApplyAsync(preview, preview.Candidates, new Archive(), true);
        Assert.AreEqual(1, result.RegisteredReplicas); Assert.AreEqual(HistoryReadiness.Ready, result.Versions.Single().Readiness);
        var repeated = await service.InspectAsync(fixture.Backup); Assert.AreEqual(0, (await service.ApplyAsync(repeated, repeated.Candidates, new Archive(), true)).RegisteredReplicas);
        Assert.HasCount(1, (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }
    [TestMethod] public async Task KnownStorageHashRejectsCorruptedAndSameNamedUnrelatedPayloads()
    {
        var fixture = await CreateAsync(true); await using var runtime = fixture.Runtime;
        var duplicate = Path.Combine(fixture.Backup, "other"); Directory.CreateDirectory(duplicate); File.WriteAllBytes(Path.Combine(duplicate, "original.7z"), "unrelated"u8.ToArray());
        var service = new HistoryReplicaRecoveryService(runtime); var preview = await service.InspectAsync(fixture.Backup);
        Assert.HasCount(1, preview.Candidates); Assert.IsTrue(preview.Candidates[0].IdentityVerified);
        await File.WriteAllTextAsync(preview.Candidates[0].Path, "changed-after-preview");
        await Assert.ThrowsExactlyAsync<IOException>(() => service.ApplyAsync(preview, preview.Candidates, new Archive(), false));
        Assert.IsEmpty((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }
    [TestMethod] public async Task CorruptArchiveAndCanceledVerificationNeverPublishALocator()
    {
        var fixture = await CreateAsync(); await using var runtime = fixture.Runtime;
        var service = new HistoryReplicaRecoveryService(runtime); var preview = await service.InspectAsync(fixture.Backup);
        var result = await service.ApplyAsync(preview, preview.Candidates, new Archive(false), true);
        Assert.AreEqual(0, result.RegisteredReplicas); Assert.HasCount(1, result.Failures);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ApplyAsync(preview, preview.Candidates, new Archive(), true, token: cancel.Token));
        Assert.IsEmpty((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
    }
    [TestMethod] public async Task ConflictingCatalogRevisionAndUnsafeHintsAreRejected()
    {
        var fixture = await CreateAsync(); await using var runtime = fixture.Runtime;
        var service = new HistoryReplicaRecoveryService(runtime); var preview = await service.InspectAsync(fixture.Backup);
        await runtime.LocalReplicaCatalogStore.SaveAsync(new(runtime.ConfigId, 1, []), 0);
        await Assert.ThrowsExactlyAsync<DeviceLocalStateConflictException>(() => service.ApplyAsync(preview, preview.Candidates, new Archive(), true));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.InspectAsync(fixture.Backup, [new(fixture.Representation.RepresentationId, "../escape.7z", 1, "hash")]));
    }
    private sealed class Archive(bool valid = true) : IArchiveRepresentationBackend
    {
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token) => ValueTask.FromResult(new PayloadVerificationResult(valid, "test", valid ? "" : "corrupt"));
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token) => ValueTask.CompletedTask;
    }
}
