using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryJointTransactionRecoveryTests
{
    [TestMethod]
    public async Task NewTargetOwnershipMarkerIsExcludedFromVerifiedMergeState()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindNewMergeTarget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new HistoryConfigId(Guid.NewGuid().ToString("N"));
            var source = SourceId.New();
            await using var history = new HistoryRuntime(new FileHistoryRepository(config,
                new HistoryRepositoryPaths(Path.Combine(root, "repo"))));
            await history.InitializeAsync();
            var current = new HistoryWorkspace(config, 0, null, null, []);
            var desired = new HistoryWorkspace(config, 1, null, null, []);
            await history.WorkspaceStore.SaveAsync(current, -1);
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "merged.dat"), "merged");
            var digest = (await MergeTreeManifest.ReadAsync(staging, _ => true, default)).Digest;
            var version = new SourceVersion(VersionId.New(), config, source, [], DateTimeOffset.UtcNow, null,
                CaptureScope.FullSource, CaptureOutcome.Captured, [], new SourceDescriptorSnapshot("new", "new"),
                digest, HistoryProvenance.Native("test"));
            var binding = new HistoryRestoreSourceBinding(source, Path.Combine(root, "new-target"));
            var restore = new HistoryRestoreService(history, new RepresentationRuntime([new UnusedRepresentationHandler()]),
                _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
                new FileSystemHistoryRestoreMutationBackend());

            var result = await restore.ExecuteMutationAsync(
                [new(binding, version, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, staging,
                    MergeTreeManifest.Empty.Digest, digest)], current, desired, default);

            Assert.AreEqual(HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreEqual("merged", await File.ReadAllTextAsync(Path.Combine(binding.TargetDirectory, "merged.dat")));
            Assert.IsEmpty(Directory.EnumerateFiles(binding.TargetDirectory, ".folderrewind-restore-*.owner"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class UnusedRepresentationHandler : IRepresentationHandler
    {
        public bool CanHandle(VersionRepresentation representation) => false;
        public ValueTask<RepresentationAssessment> AssessAsync(RepresentationAssessmentContext context, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
            => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task StartupUsesPackDurabilityToChooseRollbackOrForwardRecovery(bool packCommitted, bool catalogSaved)
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindJointRecovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new HistoryConfigId(Guid.NewGuid().ToString("N")); var source = SourceId.New(); var untouched = SourceId.New();
            var paths = new HistoryRepositoryPaths(Path.Combine(root, "repo"));
            var original = new HistoryWorkspace(config, 0, null, null, []);
            var desired = new HistoryWorkspace(config, 1, null, null, []);
            var version = new SourceVersion(VersionId.New(), config, source, [], DateTimeOffset.UtcNow, null, CaptureScope.FullSource,
                CaptureOutcome.Captured, [], new SourceDescriptorSnapshot("test", "test"), null, HistoryProvenance.Native("test"));
            var representation = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull,
                "zip", [], MaterializationFidelity.Exact, null, null, null);
            var codec = new HistoryPackCodec(); var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
                [codec.CreateObject(version), codec.CreateObject(representation)]);
            var desiredCatalog = new LocalReplicaCatalog(config, 1, [new LocalReplicaCatalogEntry(representation.RepresentationId,
                LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(Path.Combine(root, "payload.zip")), DateTimeOffset.UtcNow)]);
            var target = Path.Combine(root, "target"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "value"), "old");
            var untouchedTarget = Path.Combine(root, "untouched");
            var staging = Path.Combine(root, "stage"); Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "value"), "new");
            await using (var history = new HistoryRuntime(new FileHistoryRepository(config, paths)))
            {
                await history.InitializeAsync(); await history.WorkspaceStore.SaveAsync(original, -1);
                await history.LocalReplicaCatalogStore.SaveAsync(new(config, 0, []), -1);
                var backend = new FileSystemHistoryRestoreMutationBackend(); var binding = new HistoryRestoreSourceBinding(source, target);
                var snapshot = backend.PlanRollback(binding, pack.TransactionId);
                var unstarted = backend.PlanRollback(new(untouched, untouchedTarget), pack.TransactionId);
                await backend.PrepareRollbackAsync(snapshot, default);
                await backend.ApplyAsync(binding, staging, HistoryRestoreApplyMode.Clean, snapshot, default);
                // An unrelated writer creates a never-started target. Recovery must not remove it.
                Directory.CreateDirectory(untouchedTarget); File.WriteAllText(Path.Combine(untouchedTarget, "keep"), "keep");
                var journal = new HistoryRestoreTransactionJournal(pack.TransactionId, HistoryRestoreTransactionPhase.Mutating,
                    original, desired, [staging], [snapshot, unstarted], [source], [source], codec.Encode(pack), desiredCatalog, 0);
                new HistoryRestoreTransactionJournalStore(history, backend).Save(journal);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => { await using var lease = await history.MutationGate.EnterAsync(); });
                if (packCommitted) await history.Repository.CommitAsync(pack);
                if (catalogSaved) await history.LocalReplicaCatalogStore.SaveAsync(desiredCatalog, 0);
            }
            await using (var recovered = new HistoryRuntime(new FileHistoryRepository(config, paths)))
            {
                await recovered.InitializeAsync();
                Assert.AreEqual(packCommitted ? "new" : "old", File.ReadAllText(Path.Combine(target, "value")));
                Assert.AreEqual("keep", File.ReadAllText(Path.Combine(untouchedTarget, "keep")));
                Assert.AreEqual(packCommitted ? 1L : 0L, (await recovered.WorkspaceStore.LoadAsync()).Value!.StateRevision);
                Assert.AreEqual(packCommitted ? 1L : 0L, (await recovered.LocalReplicaCatalogStore.LoadAsync()).Value!.CatalogRevision);
                await using var lease = await recovered.MutationGate.EnterAsync();
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
