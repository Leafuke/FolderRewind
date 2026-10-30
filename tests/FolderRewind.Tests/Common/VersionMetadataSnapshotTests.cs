using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
public sealed class VersionMetadataSnapshotTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FolderRewindMetadataTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void SemanticIdentityHasIndependentDeterministicFixedVector()
    {
        var versionId = VersionId.Parse("00112233445566778899aabbccddeeff");

        var id = VersionMetadataSnapshot.CreateId(
            versionId,
            "com.folderrewind.minerewind",
            "minecraft.world",
            1);

        Assert.AreEqual("216eb7a8a78e58cd8279924f2b07bc4b", id.ToString());
    }

    [TestMethod]
    public void SameSemanticIdentityIsIdempotentButDifferentPayloadIsIntegrityConflict()
    {
        var codec = new HistoryPackCodec();
        var configId = new HistoryConfigId("metadata-conflict");
        var sourceId = SourceId.New();
        var version = new SourceVersion(
            VersionId.New(), configId, sourceId, [], DateTimeOffset.UnixEpoch, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [],
            new SourceDescriptorSnapshot("source", "source"), null, HistoryProvenance.Native("test"));
        var first = VersionMetadataSnapshot.Create(
            version.VersionId,
            "com.folderrewind.minerewind",
            "minecraft.world",
            1,
            JsonSerializer.SerializeToElement(new { levelName = "World", dataVersion = 4189 }),
            version.CreatedAtUtc);
        var reordered = VersionMetadataSnapshot.Create(
            version.VersionId,
            "com.folderrewind.minerewind",
            "minecraft.world",
            1,
            JsonSerializer.SerializeToElement(new { dataVersion = 4189, levelName = "World" }),
            version.CreatedAtUtc);
        var conflict = VersionMetadataSnapshot.Create(
            version.VersionId,
            "com.folderrewind.minerewind",
            "minecraft.world",
            1,
            JsonSerializer.SerializeToElement(new { levelName = "Other", dataVersion = 4189 }),
            version.CreatedAtUtc);
        Assert.AreEqual(first.MetadataSnapshotId, reordered.MetadataSnapshotId);

        var duplicatePacks = new[]
        {
            Pack(codec, version, first),
            Pack(codec, reordered)
        };
        new HistoryRepositoryValidator(codec).Validate(configId, duplicatePacks);

        var conflictingPacks = duplicatePacks.Append(Pack(codec, conflict));
        Assert.ThrowsExactly<HistoryIntegrityConflictException>(() =>
            new HistoryRepositoryValidator(codec).Validate(configId, conflictingPacks));
    }

    [TestMethod]
    public async Task CommitPersistsMetadataAndKeepsProviderWarningNonFatal()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "commit-repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var capture = Capture(
            sourceId,
            [new VersionMetadataCandidate(
                "com.folderrewind.minerewind",
                "minecraft.world",
                1,
                JsonSerializer.SerializeToElement(new { dataVersion = 4189 }))],
            [new HistoryDiagnostic(
                "minerewind.version_metadata_partial",
                HistoryDiagnosticSeverity.Warning,
                "Optional metadata field was unavailable.")]);
        var now = DateTimeOffset.UtcNow;

        var batch = await runtime.Commit.CommitAsync(new HistoryCommitRequest(
            new HistoryConfigSnapshot(
                configId,
                [new HistoryConfigSourceSnapshot(
                    sourceId,
                    new SourceDescriptorSnapshot("world", "world"))]),
            new HistoryBackupInvocation(
                RunId.New(), now.AddSeconds(-1), now, BackupInvocationKind.Manual,
                HistoryProvenance.Native("test")),
            expectedWorkspace: null,
            [capture]));

        Assert.HasCount(1, batch.NewVersions);
        Assert.HasCount(1, batch.NewMetadataSnapshots);
        Assert.AreEqual(batch.NewVersions[0].VersionId, batch.NewMetadataSnapshots[0].VersionId);
        Assert.IsTrue(batch.Run.Diagnostics.Any(item =>
            item.Code == "minerewind.version_metadata_partial"
            && item.Severity == HistoryDiagnosticSeverity.Warning));
        Assert.IsTrue(batch.IndexRefreshSucceeded,
            "The history pack was committed, but refreshing the derived index failed.");
        var indexed = await runtime.Query.GetVersionMetadataSnapshotsAsync(batch.NewVersions[0].VersionId);
        Assert.AreEqual(batch.NewMetadataSnapshots[0].MetadataSnapshotId, indexed.Single().MetadataSnapshotId);
        CollectionAssert.AreEqual(
            HistoryPackCodec.Canonicalize(batch.NewMetadataSnapshots[0].Payload),
            HistoryPackCodec.Canonicalize(indexed.Single().Payload));
    }

    [TestMethod]
    public async Task MetadataExtractionFailureDoesNotBlockCapturedVersionOrCreateEmptySnapshot()
    {
        var configId = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var runtime = new HistoryRuntime(new FileHistoryRepository(
            configId,
            new HistoryRepositoryPaths(Path.Combine(_root, "warning-repository"))));
        await runtime.InitializeAsync();
        var sourceId = SourceId.New();
        var capture = Capture(
            sourceId,
            [],
            [new HistoryDiagnostic(
                "plugin.version_metadata_capture_failed",
                HistoryDiagnosticSeverity.Warning,
                "Invalid level.dat")]);
        var now = DateTimeOffset.UtcNow;

        var batch = await runtime.Commit.CommitAsync(new HistoryCommitRequest(
            new HistoryConfigSnapshot(
                configId,
                [new HistoryConfigSourceSnapshot(
                    sourceId,
                    new SourceDescriptorSnapshot("world", "world"))]),
            new HistoryBackupInvocation(
                RunId.New(), now.AddSeconds(-1), now, BackupInvocationKind.Manual,
                HistoryProvenance.Native("test")),
            expectedWorkspace: null,
            [capture]));

        Assert.HasCount(1, batch.NewVersions);
        Assert.HasCount(0, batch.NewMetadataSnapshots);
        Assert.IsTrue(batch.Run.Diagnostics.Any(item => item.Code == "plugin.version_metadata_capture_failed"));
    }

    private SourceCaptureResult Capture(
        SourceId sourceId,
        IReadOnlyList<VersionMetadataCandidate> metadata,
        IReadOnlyList<HistoryDiagnostic> diagnostics)
    {
        var payloadPath = Path.Combine(_root, $"{Guid.NewGuid():N}.7z");
        File.WriteAllText(payloadPath, "payload");
        var bytes = File.ReadAllBytes(payloadPath);
        var representationId = RepresentationId.New();
        return new SourceCaptureResult(
            sourceId,
            SourceCaptureOutcome.Captured,
            CaptureScope.FullSource,
            "state",
            null,
            new RepresentationCandidate(
                representationId,
                RepresentationKind.CoreFull,
                "7z",
                [],
                MaterializationFidelity.Exact,
                null,
                "state",
                null),
            new LocalReplicaCandidate(
                LocalReplicaId.New(),
                representationId,
                LocalReplicaLocator.ControlledAbsolute(payloadPath),
                CapturePayloadState.VerifiedFinal,
                DateTimeOffset.UtcNow),
            new CapturePayloadCandidate(
                payloadPath,
                CapturePayloadState.VerifiedFinal,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            HistoryWorkspaceStore.MissingRevision,
            null,
            null,
            diagnostics,
            versionMetadataCandidates: metadata);
    }

    private static HistoryPackReadResult Pack(HistoryPackCodec codec, params object[] facts)
    {
        var pack = new HistoryCommitPack(
            PackId.New(),
            HistoryTransactionId.New(),
            DateTimeOffset.UtcNow,
            facts.Select(fact => codec.CreateObject(fact)));
        return codec.Decode(codec.Encode(pack));
    }
}
