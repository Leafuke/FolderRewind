using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System.Collections.Immutable;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupSizeStatisticsTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root = Path.Combine(Path.GetTempPath(), "BackupSizeTests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void UnknownZeroAndInvalidMetadataRemainDistinct()
    {
        Assert.IsNull(BackupSizeStatistics.Unknown.AddedStoredBytes);
        Assert.AreEqual(0L, new BackupSizeStatistics(0, 0, 0).LogicalContentBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BackupSizeStatistics(-1));
        var version = Version(new("sizes"), SourceId.New());
        var snapshot = VersionMetadataSnapshot.Create(version.VersionId, BackupSizeMetadata.ProducerId, BackupSizeMetadata.SchemaId, 1,
            JsonSerializer.SerializeToElement(new { addedStoredBytes = -1, logicalContentBytes = 0 }), version.CreatedAtUtc);
        var sizes = BackupSizeMetadata.ReadCapture(snapshot);
        Assert.IsNull(sizes.AddedStoredBytes);
        Assert.AreEqual(0L, sizes.LogicalContentBytes);
        var future = VersionMetadataSnapshot.Create(version.VersionId, BackupSizeMetadata.ProducerId, BackupSizeMetadata.SchemaId, 2,
            BackupSizeMetadata.CapturePayload(1, 2), version.CreatedAtUtc);
        Assert.AreEqual(BackupSizeStatistics.Unknown, BackupSizeMetadata.ReadCapture(future));
    }

    [TestMethod]
    [DataRow(RepresentationKind.CoreFull)]
    [DataRow(RepresentationKind.CoreSmartDelta)]
    [DataRow(RepresentationKind.CoreRolling)]
    public void CapturesMeasureFinalArchiveAndFullLogicalState(RepresentationKind kind)
    {
        var capture = Capture(kind, States(("changed", 15), ("unchanged", 700)));
        var snapshot = capture.VersionMetadataCandidates.Single().ToSnapshot(Version(new("sizes"), capture.SourceId));
        var sizes = BackupSizeMetadata.ReadCapture(snapshot);
        Assert.AreEqual(61L, sizes.AddedStoredBytes);
        Assert.AreEqual(715L, sizes.LogicalContentBytes);
        Assert.AreEqual(HistoryBackupTypePolicy.FromKind(kind), HistoryBackupTypePolicy.Resolve(snapshot, null));
        Assert.AreEqual(61L, BackupSizeMetadata.ReadBytes(capture.RepresentationCandidate!.Metadata, BackupSizeMetadata.StoredBytesKey));
    }

    [TestMethod]
    public void PartialCaptureAppliesChangesAndDeletionsBeforeCountingContent()
    {
        var source = SourceId.New();
        var baseline = new SourceCaptureBaseline(source, 0, VersionId.New(), RepresentationId.New(), RepresentationKind.CoreFull,
            "unused", 0, States(("outside", 900), ("changed", 2), ("deleted", 50)));
        var capture = Capture(RepresentationKind.CoreSmartDelta, States(("changed", 20)), baseline, CaptureScope.PartialSource, ["deleted"]);
        var sizes = BackupSizeMetadata.ReadCapture(capture.VersionMetadataCandidates.Single().ToSnapshot(Version(new("sizes"), source)));
        Assert.AreEqual(920L, sizes.LogicalContentBytes);
        Assert.AreEqual(61L, sizes.AddedStoredBytes);
    }

    [TestMethod]
    public void DeletionOnlyAndEmptyCapturesStillCountTheirStoredOutput()
    {
        var capture = Capture(RepresentationKind.CoreSmartDelta, States());
        var sizes = BackupSizeMetadata.ReadCapture(capture.VersionMetadataCandidates.Single().ToSnapshot(Version(new("sizes"), capture.SourceId)));
        Assert.AreEqual(0L, sizes.LogicalContentBytes);
        Assert.AreEqual(61L, sizes.AddedStoredBytes);
    }

    [TestMethod]
    public void ProviderReplacementInheritanceAndFailureNeverReplaceCurrentHostStatistics()
    {
        var capture = Capture(RepresentationKind.CoreFull, States(("file", 30)));
        var inheritedHost = new VersionMetadataCandidate(BackupSizeMetadata.ProducerId, BackupSizeMetadata.SchemaId, 1,
            BackupSizeMetadata.CapturePayload(999, 999));
        var provider = new VersionMetadataCandidate("test.plugin", "world", 1, JsonSerializer.SerializeToElement(new { name = "world" }));
        var merged = capture.WithVersionMetadata([provider, inheritedHost]);
        Assert.HasCount(2, merged.VersionMetadataCandidates);
        Assert.AreEqual(capture.VersionMetadataCandidates.Single(), merged.VersionMetadataCandidates.Single(m => m.ProducerPluginId == BackupSizeMetadata.ProducerId));
        var failedProvider = merged.WithVersionMetadata([], [new("plugin.version_metadata_capture_failed", HistoryDiagnosticSeverity.Warning, "test")]);
        Assert.HasCount(1, failedProvider.VersionMetadataCandidates);
        Assert.AreEqual(61L, failedProvider.VersionMetadataCandidates[0].Payload.GetProperty("addedStoredBytes").GetInt64());
    }

    [TestMethod]
    public void DependencyProjectionDeduplicatesSharedAncestorsAndDoesNotUseNativeBudget()
    {
        var version = Version(new("sizes"), SourceId.New());
        var shared = Representation(version, 100);
        var left = Representation(version, 10, [shared.RepresentationId]);
        var right = Representation(version, 20, [shared.RepresentationId]);
        var root = Representation(version, 1, [left.RepresentationId, right.RepresentationId]);
        var graph = new[] { shared, left, right, root }.ToDictionary(r => r.RepresentationId);
        Assert.AreEqual(131L, BackupSizeMetadata.DependencyBytes(root, graph));
        var native = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.PluginArtifact, "native", [],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["nativeClosureStorageBytes"] = "99999", [BackupSizeMetadata.StoredBytesKey] = "10" });
        Assert.IsNull(BackupSizeMetadata.DependencyBytes(native, graph));
        var invalid = Representation(version, null, [shared.RepresentationId]);
        Assert.IsNull(BackupSizeMetadata.DependencyBytes(invalid, graph));
    }

    [TestMethod]
    public void BackupTypePreservesTheCaptureTypeAcrossRepresentationRewritesAndSupportsOldMetadata()
    {
        var version = Version(new("type"), SourceId.New());
        var full = Representation(version, 100);
        var snapshot = VersionMetadataSnapshot.Create(version.VersionId, BackupSizeMetadata.ProducerId, BackupSizeMetadata.SchemaId, 1,
            BackupSizeMetadata.CapturePayload(10, 1000, HistoryBackupType.Smart), version.CreatedAtUtc);
        Assert.AreEqual(HistoryBackupType.Smart, HistoryBackupTypePolicy.Resolve(snapshot, full));
        Assert.AreEqual(HistoryBackupType.Full, HistoryBackupTypePolicy.Resolve(null, full));
        var native = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.PluginArtifact, "native", [],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string>());
        Assert.AreEqual(HistoryBackupType.Semantic, HistoryBackupTypePolicy.Resolve(null, native));
        var legacy = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.LegacyArchive, "zip", [],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["legacyBackupType"] = "Smart" });
        Assert.AreEqual(HistoryBackupType.Smart, HistoryBackupTypePolicy.Resolve(null, legacy));
    }

    [TestMethod]
    public async Task CommitProjectionIndexRebuildAndPackTransferKeepCapturedSizesWithoutLocalPayloads()
    {
        var config = new HistoryConfigId("durable-size");
        await using var runtime = await Runtime("first", config);
        var capture = Capture(RepresentationKind.CoreFull, States(("file", 900)));
        var batch = await Commit(runtime, capture);
        Assert.HasCount(1, batch.NewMetadataSnapshots);
        File.Delete(capture.PayloadCandidate!.FileName);
        await runtime.Index.RebuildAsync(await runtime.Repository.ReadAllPacksAsync());
        var summary = (await new HistoryPresentationQueryService(runtime).QueryAsync()).Timeline.Single();
        Assert.AreEqual(new BackupSizeStatistics(61, 900, 61), summary.SizeStatistics);
        await using var imported = await Runtime("imported", config);
        foreach (var pack in await runtime.Repository.ReadAllPacksAsync()) await imported.Repository.CommitAsync(pack.Pack);
        await imported.EnsureIndexCurrentAsync();
        var transferred = (await new HistoryPresentationQueryService(imported).QueryAsync()).Timeline.Single();
        Assert.AreEqual(summary.SizeStatistics, transferred.SizeStatistics);
        Assert.IsNull(transferred.LocalPath);
        // A replacement changes dependency costs, not the original capture's added bytes.
        var replacement = Representation(batch.NewVersions.Single(), 17);
        await Facts(imported, replacement);
        await Catalog(imported, (replacement, "replacement", 17));
        var rewritten = (await new HistoryPresentationQueryService(imported).QueryAsync()).Timeline.Single();
        Assert.AreEqual(new BackupSizeStatistics(61, 900, 17), rewritten.SizeStatistics);
        Assert.AreEqual(61L, BackupSizeMetadata.ReadCapture((await imported.Query.GetVersionMetadataSnapshotsAsync(batch.NewVersions[0].VersionId)).Single()).AddedStoredBytes);
        Assert.AreEqual(17L, BackupSizeMetadata.DependencyBytes(replacement, new Dictionary<RepresentationId, VersionRepresentation> { [replacement.RepresentationId] = replacement }));
    }

    [TestMethod]
    public async Task NoChangesRunHasZeroAddedStorageWhileItsVersionKeepsOriginalCaptureCost()
    {
        await using var runtime = await Runtime("reuse", new("reuse-size"));
        var capture = Capture(RepresentationKind.CoreFull, States(("file", 900)));
        await Commit(runtime, capture);
        var workspace = (await runtime.WorkspaceStore.LoadAsync()).Value!;
        await Commit(runtime, SourceCaptureResult.NoChanges(capture.SourceId, CaptureScope.FullSource,
            workspace.StateRevision, workspace.GetSourceState(capture.SourceId).BaseVersionId));
        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();
        var reused = snapshot.Runs.Single(run => run.Sources[0].Outcome == BackupRunSourceOutcome.Reused);
        Assert.AreEqual(new BackupSizeStatistics(0, 900, 61), reused.SourceSizeStatistics![capture.SourceId].Statistics);
        Assert.AreEqual(61L, snapshot.Timeline.Single().SizeStatistics!.AddedStoredBytes);
    }

    [TestMethod]
    [DataRow(RepresentationKind.CoreFull)]
    [DataRow(RepresentationKind.CoreSmartDelta)]
    [DataRow(RepresentationKind.CoreRolling)]
    [DataRow(RepresentationKind.LegacyArchive)]
    public async Task OldOrdinaryArchivesDisplayTheirCurrentFileLengthWithoutPublishingCaptureStatistics(RepresentationKind kind)
    {
        var config = new HistoryConfigId("local-archive-fallback");
        await using var runtime = await Runtime("local-fallback", config);
        var version = Version(config, SourceId.New(), RunId.New());
        var baseline = Version(config, version.SourceId);
        var full = Representation(baseline, null);
        var rep = new VersionRepresentation(RepresentationId.New(), version.VersionId, kind, "test",
            kind == RepresentationKind.CoreSmartDelta ? [full.RepresentationId] : [], MaterializationFidelity.Exact, null, null, []);
        var checkpoint = Checkpoint(version);
        var run = new BackupRun(version.CreatedByRunId!.Value, config, version.CreatedAtUtc, version.CreatedAtUtc,
            BackupInvocationKind.Manual, BackupRunOutcome.Completed,
            [new BackupRunSourceResult(version.SourceId, BackupRunSourceOutcome.Captured, version.VersionId, [], checkpoint.CheckpointId)], []);
        await Facts(runtime, version, rep, checkpoint, run, baseline, full);
        await Catalog(runtime, (full, "baseline", 4096), (rep, "archive", 7804));
        var service = new HistoryPresentationQueryService(runtime);

        var snapshot = await service.QueryAsync(version.SourceId);
        Assert.AreEqual(new BackupSizeStatistics(7804), snapshot.Timeline.Single().SizeStatistics);
        Assert.AreEqual(7804L, snapshot.Runs.Single().SourceSizeStatistics![version.SourceId].Statistics.AddedStoredBytes);
        Assert.IsEmpty(await runtime.Query.GetVersionMetadataSnapshotsAsync(version.VersionId));

        // The fallback observes the selected archive only, without listing contents or caching a historical cost.
        var path = Path.Combine(_root, "archive");
        File.WriteAllBytes(path, new byte[91]);
        File.Delete(Path.Combine(_root, "baseline"));
        Assert.AreEqual(91L, (await service.QueryAsync()).Timeline.Single().SizeStatistics!.AddedStoredBytes);
        File.WriteAllBytes(path, []);
        Assert.AreEqual(0L, (await service.QueryAsync()).Timeline.Single().SizeStatistics!.AddedStoredBytes);
        File.Delete(path);
        Assert.IsNull((await service.QueryAsync()).Timeline.Single().SizeStatistics!.AddedStoredBytes);
        Assert.IsEmpty(await runtime.Index.GetBackupSizeSnapshotsAsync());
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(19L)]
    public async Task RecordedArchiveSizeIncludingZeroTakesPrecedenceOverLocalFallback(long recorded)
    {
        var config = new HistoryConfigId("recorded-archive-size");
        await using var runtime = await Runtime("recorded-fallback", config);
        var version = Version(config, SourceId.New());
        var rep = Representation(version, null);
        var metadata = VersionMetadataSnapshot.Create(version.VersionId, BackupSizeMetadata.ProducerId, BackupSizeMetadata.SchemaId, 1,
            BackupSizeMetadata.CapturePayload(recorded, 100), version.CreatedAtUtc);
        await Facts(runtime, version, rep, Checkpoint(version), metadata);
        await Catalog(runtime, (rep, "recorded", 7804));
        var service = new HistoryPresentationQueryService(runtime);
        Assert.AreEqual(recorded, (await service.QueryAsync()).Timeline.Single().SizeStatistics!.AddedStoredBytes);
        File.Delete(Path.Combine(_root, "recorded"));
        Assert.AreEqual(recorded, (await service.QueryAsync()).Timeline.Single().SizeStatistics!.AddedStoredBytes);
    }

    [TestMethod]
    [DataRow(RepresentationKind.PluginArtifact, SourceVersionCreationKind.Capture)]
    [DataRow(RepresentationKind.CoreFull, SourceVersionCreationKind.Merge)]
    [DataRow(RepresentationKind.CoreFull, SourceVersionCreationKind.Recovery)]
    [DataRow(RepresentationKind.CoreFull, SourceVersionCreationKind.Import)]
    public async Task LocalArchiveFallbackDoesNotTreatNativeRootsOrNonCapturesAsNewOutput(
        RepresentationKind kind, SourceVersionCreationKind creationKind)
    {
        var config = new HistoryConfigId("excluded-archive-fallback");
        await using var runtime = await Runtime("excluded-fallback", config);
        var parent = Version(config, SourceId.New());
        var version = Version(config, parent.SourceId, creationKind: creationKind,
            parents: creationKind == SourceVersionCreationKind.Merge ? [parent.VersionId] : []);
        var rep = new VersionRepresentation(RepresentationId.New(), version.VersionId, kind, "test", [],
            MaterializationFidelity.Exact, null, null, []);
        await Facts(runtime, parent, version, rep, Checkpoint(version));
        await Catalog(runtime, (rep, "excluded", 61));
        Assert.IsNull((await new HistoryPresentationQueryService(runtime).QueryAsync()).Timeline.Single().SizeStatistics!.AddedStoredBytes);
    }

    [TestMethod]
    public async Task OldArchiveFallbackKeepsReusedCheckpointsAndRunSourcesAtZero()
    {
        var config = new HistoryConfigId("reused-archive-fallback");
        await using var runtime = await Runtime("reused-fallback", config);
        var version = Version(config, SourceId.New(), RunId.New());
        var rep = Representation(version, null);
        var original = Checkpoint(version);
        var facts = new List<object> { version, rep, original };
        foreach (var outcome in new[] { BackupRunSourceOutcome.Reused, BackupRunSourceOutcome.CarriedForward })
        {
            var checkpoint = Checkpoint(version, RunId.New());
            facts.Add(checkpoint);
            facts.Add(new BackupRun(checkpoint.CreatedByRunId!.Value, config, version.CreatedAtUtc, version.CreatedAtUtc,
                BackupInvocationKind.Manual, BackupRunOutcome.NoChange,
                [new BackupRunSourceResult(version.SourceId, outcome, version.VersionId, [], checkpoint.CheckpointId)], []));
        }
        await Facts(runtime, [.. facts]);
        await Catalog(runtime, (rep, "reused", 61));
        var snapshot = await new HistoryPresentationQueryService(runtime).QueryAsync();
        Assert.AreEqual(61L, snapshot.Timeline.Single(item => item.CheckpointId == original.CheckpointId).SizeStatistics!.AddedStoredBytes);
        Assert.IsTrue(snapshot.Timeline.Where(item => item.CheckpointId != original.CheckpointId).All(item => item.SizeStatistics!.AddedStoredBytes == 0));
        Assert.IsTrue(snapshot.Runs.All(run => run.SourceSizeStatistics![version.SourceId].Statistics.AddedStoredBytes == 0));
        Assert.IsEmpty(await runtime.Index.GetBackupSizeSnapshotsAsync());
    }

    [TestMethod]
    public async Task LocalBackfillOverlaysFilesAndDeletionsWithoutInventingHistoricalAddedBytes()
    {
        var config = new HistoryConfigId("old-size");
        await using var runtime = await Runtime("old", config);
        var first = Version(config, SourceId.New());
        var second = Version(config, first.SourceId);
        var full = Representation(first, null);
        var delta = new VersionRepresentation(RepresentationId.New(), second.VersionId, RepresentationKind.CoreSmartDelta, "test", [full.RepresentationId],
            MaterializationFidelity.Exact, null, null, new Dictionary<string, string> { ["deletedFiles"] = "removed" });
        await Facts(runtime, first, second, full, delta);
        await Catalog(runtime, (full, "full", 51), (delta, "delta", 9));
        int reads = 0;
        var reader = new HistoryBackupSizeService(runtime, (node, _, _) =>
        {
            reads++;
            IReadOnlyDictionary<string, long> listing = node.RepresentationId == full.RepresentationId
                ? new Dictionary<string, long> { ["changed"] = 10, ["unchanged"] = 50, ["removed"] = 99 }
                : new Dictionary<string, long> { ["changed"] = 12 };
            return Task.FromResult<IReadOnlyDictionary<string, long>?>(listing);
        }, (_, _, _) => throw new AssertFailedException("No native decoder should run."));
        var result = await reader.ReadAsync(second.VersionId, delta.RepresentationId, BackupSizeStatistics.Unknown);
        Assert.AreEqual(new BackupSizeStatistics(null, 62, 60), result);
        Assert.AreEqual(2, reads);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(second.VersionId, delta.RepresentationId, result, canceled.Token));
    }

    [TestMethod]
    public async Task MissingLocalDependenciesAndUnavailableCredentialsStayUnknown()
    {
        var config = new HistoryConfigId("missing-size");
        await using var runtime = await Runtime("missing", config);
        var version = Version(config, SourceId.New());
        var rep = Representation(version, null);
        await Facts(runtime, version, rep);
        var reader = new HistoryBackupSizeService(runtime, (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, long>?>(null),
            (_, _, _) => throw new AssertFailedException("No native access."));
        Assert.AreEqual(BackupSizeStatistics.Unknown, await reader.ReadAsync(version.VersionId, rep.RepresentationId, BackupSizeStatistics.Unknown));
        await Catalog(runtime, (rep, "local", 51));
        Assert.AreEqual(BackupSizeStatistics.Unknown, await reader.ReadAsync(version.VersionId, rep.RepresentationId, BackupSizeStatistics.Unknown));
    }

    [TestMethod]
    public async Task ClosingAnInProgressObservationCancelsTheBackendWithoutPublishingStatistics()
    {
        var config = new HistoryConfigId("cancel-size");
        await using var runtime = await Runtime("cancel", config);
        var version = Version(config, SourceId.New());
        var rep = Representation(version, null);
        await Facts(runtime, version, rep);
        await Catalog(runtime, (rep, "cancel-payload", 51));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new HistoryBackupSizeService(runtime, async (_, _, cancellation) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return null;
        }, (_, _, _) => throw new AssertFailedException());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = reader.ReadAsync(version.VersionId, rep.RepresentationId, BackupSizeStatistics.Unknown, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsEmpty(await runtime.Query.GetVersionMetadataSnapshotsAsync(version.VersionId));
    }

    [TestMethod]
    public async Task RejectedCaptureCommitNeverPublishesItsSizeCandidate()
    {
        await using var runtime = await Runtime("rejected", new("rejected-size"));
        var first = Capture(RepresentationKind.CoreFull, States(("file", 5)));
        await Commit(runtime, first);
        var conflicting = Capture(RepresentationKind.CoreFull, States(("file", 9)));
        await Assert.ThrowsExactlyAsync<HistoryCommitConflictException>(() => Commit(runtime, conflicting));
        Assert.HasCount(1, await runtime.Query.GetAllVersionsAsync());
        Assert.HasCount(1, await runtime.Index.GetBackupSizeSnapshotsAsync());
    }

    private SourceCaptureResult Capture(RepresentationKind kind, ImmutableSortedDictionary<string, SourceCaptureFileState> states,
        SourceCaptureBaseline? baseline = null, CaptureScope scope = CaptureScope.FullSource, string[]? deleted = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".7z");
        File.WriteAllBytes(path, new byte[61]);
        return VerifiedArchiveCaptureFactory.Create(baseline?.SourceId ?? SourceId.New(), scope, path, kind, "test", states, baseline,
            baseline is null ? [] : [baseline.BaseRepresentationId], 0, MaterializationFidelity.Exact, deletedFiles: deleted);
    }

    private static ImmutableSortedDictionary<string, SourceCaptureFileState> States(params (string Path, long Size)[] files)
        => files.ToImmutableSortedDictionary(f => f.Path, f => new SourceCaptureFileState(f.Size, DateTime.UnixEpoch), StringComparer.Ordinal);

    private async Task<HistoryRuntime> Runtime(string name, HistoryConfigId config)
    {
        var runtime = new HistoryRuntime(new FileHistoryRepository(config, new HistoryRepositoryPaths(Path.Combine(_root, name))));
        await runtime.InitializeAsync();
        return runtime;
    }

    private static SourceVersion Version(HistoryConfigId config, SourceId source, RunId? runId = null,
        SourceVersionCreationKind creationKind = SourceVersionCreationKind.Capture, VersionId[]? parents = null)
        => new(VersionId.New(), config, source, parents ?? [], DateTimeOffset.UnixEpoch, runId, CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new("source", "unused"), null, HistoryProvenance.Native("test"), creationKind: creationKind);

    private static SourceCheckpoint Checkpoint(SourceVersion version, RunId? runId = null)
        => new(CheckpointId.New(), version.ConfigId, version.CreatedAtUtc, runId ?? version.CreatedByRunId, HistoryProvenance.Native("test"),
            [new CheckpointSource(version.SourceId, version.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.Captured)]);

    private static VersionRepresentation Representation(SourceVersion version, long? stored, RepresentationId[]? dependencies = null)
        => new(RepresentationId.New(), version.VersionId, dependencies?.Length > 0 ? RepresentationKind.CoreSmartDelta : RepresentationKind.CoreFull,
            "test", dependencies ?? [], MaterializationFidelity.Exact, null, null,
            stored is { } bytes ? new Dictionary<string, string> { [BackupSizeMetadata.StoredBytesKey] = BackupSizeMetadata.FormatBytes(bytes) } : []);

    private static async Task Facts(HistoryRuntime runtime, params object[] facts)
    {
        var codec = new HistoryPackCodec();
        await runtime.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, facts.Select(f => codec.CreateObject(f))));
        await runtime.EnsureIndexCurrentAsync();
    }

    private async Task Catalog(HistoryRuntime runtime, params (VersionRepresentation Representation, string Name, int Size)[] files)
    {
        var entries = files.Select(file =>
        {
            var path = Path.Combine(_root, file.Name); File.WriteAllBytes(path, new byte[file.Size]);
            return new LocalReplicaCatalogEntry(file.Representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow);
        });
        await runtime.LocalReplicaCatalogStore.SaveAsync(new LocalReplicaCatalog(runtime.ConfigId, 0, entries), -1);
    }

    private static async Task<HistoryCommitBatch> Commit(HistoryRuntime runtime, SourceCaptureResult capture)
    {
        var now = DateTimeOffset.UtcNow;
        return await runtime.Commit.CommitAsync(new HistoryCommitRequest(
            new(runtime.ConfigId, [new HistoryConfigSourceSnapshot(capture.SourceId, new("source", "unused"))]),
            new(RunId.New(), now.AddSeconds(-1), now, BackupInvocationKind.Manual, HistoryProvenance.Native("test")),
            (await runtime.WorkspaceStore.LoadAsync()).Value, [capture]));
    }
}
