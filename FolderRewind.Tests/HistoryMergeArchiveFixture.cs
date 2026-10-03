using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System.Security.Cryptography;

namespace FolderRewind.Tests;

// Real archive, capture, branch, checkout and merge services shared by Stage A integration tests.
internal sealed class HistoryMergeArchiveFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FolderRewindMergeArchive", Guid.NewGuid().ToString("N"));
    public HistoryConfigId Config { get; } = new(Guid.NewGuid().ToString("N"));
    public SourceId Source { get; } = SourceId.New();
    public SourceId Other { get; } = SourceId.New();
    public string Target => Path.Combine(Root, "source");
    public string OtherTarget => Path.Combine(Root, "other");
    public HistoryRuntime History { get; private set; } = null!;
    public CountingArchiveBackend Archives { get; }
    public SourceCheckpoint Base { get; private set; } = null!;
    public HistoryCommitBatch Ours { get; private set; } = null!;
    public HistoryCommitBatch Theirs { get; private set; } = null!;
    public MergeSession Session { get; private set; } = null!;
    public HistoryWorkspace Before { get; private set; } = null!;
    public LocalReplicaCatalog CatalogBefore { get; private set; } = null!;
    public string OursDigest { get; private set; } = null!;
    public HistoryConfigSnapshot Snapshot => new(Config,
        [new(Source, new("source", Target)), new(Other, new("other", OtherTarget))]);

    public HistoryMergeArchiveFixture()
    {
        var executable = SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        Assert.IsNotNull(executable, "Stage A requires real 7-Zip. Set FOLDERREWIND_TEST_7Z.");
        Archives = new(new SevenZipArchiveProcessBackend(() => executable, () => null, false, ".restore-marker"));
    }

    public async Task SeedAsync()
    {
        Directory.CreateDirectory(Target); Directory.CreateDirectory(OtherTarget);
        File.WriteAllText(Path.Combine(Target, "a.txt"), "base-a");
        File.WriteAllText(Path.Combine(Target, "b.txt"), "base-b");
        File.WriteAllText(Path.Combine(OtherTarget, "keep.txt"), "untouched");
        await OpenAsync();
        var initial = await CaptureAsync(Source, Other);
        Base = initial.NewCheckpoints.Single(c => c.SourceId == Source);
        var feature = await History.Branches.CreateFromCheckpointAsync(Base.CheckpointId, "feature", sourceId: Source);
        File.WriteAllText(Path.Combine(Target, "a.txt"), "ours-a");
        Ours = await CaptureAsync(Source);
        await CheckoutAsync(feature.BranchUpdate);
        File.WriteAllText(Path.Combine(Target, "b.txt"), "theirs-b");
        Theirs = await CaptureAsync(Source);
        await CheckoutAsync(Ours.NewBranchUpdates.Single());
        Before = (await History.WorkspaceStore.LoadAsync()).Value!;
        CatalogBefore = (await History.LocalReplicaCatalogStore.LoadAsync()).Value!;
        OursDigest = (await MergeTreeManifest.ReadAsync(Target, _ => true, default)).Digest;
        Session = (await new HistoryMergeService(History, Restore()).StartAsync(
            Theirs.NewBranchUpdates.Single().BranchId, "fixture-v1", [new(Source, Target)]))!;
        Assert.AreEqual(HistoryMergeMode.ThreeWay, Session.Plan.Mode);
        Assert.AreEqual(MergeSessionState.Ready, Session.State);
        Assert.IsEmpty(new HistoryMergeService(History, Restore()).AllConflicts(Session));
        Archives.Reset();
    }

    private async Task OpenAsync()
    {
        History = new(new FileHistoryRepository(Config, new(Path.Combine(Root, "repository"))));
        await History.InitializeAsync();
    }

    public async Task ReopenAsync()
    {
        await History.DisposeAsync();
        await OpenAsync();
        Session = History.MergeSessions.Load(Session.Id);
    }

    private async Task CheckoutAsync(BranchUpdate branch)
    {
        var result = await new HistoryCheckoutService(History, Restore()).CheckoutAsync(branch.UpdateId,
            [new(Source, Target), new(Other, OtherTarget)], (await History.WorkspaceStore.LoadAsync()).Value!,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
    }

    private async Task<HistoryCommitBatch> CaptureAsync(params SourceId[] sources)
    {
        var workspace = (await History.WorkspaceStore.LoadAsync()).Value;
        var captures = new List<SourceCaptureResult>();
        foreach (var source in sources)
        {
            var directory = source == Source ? Target : OtherTarget;
            var tree = await MergeTreeManifest.ReadAsync(directory, _ => true, default);
            var representationId = RepresentationId.New();
            var version = new SourceVersion(VersionId.New(), Config, source, [], DateTimeOffset.UtcNow,
                null, CaptureScope.FullSource, CaptureOutcome.Captured, [], new("fixture", directory),
                tree.Digest, HistoryProvenance.Native("integration"));
            var payload = await Archives.CreateFullAsync(version, directory, representationId,
                Path.Combine(Root, "captures", representationId.ToString()), default);
            var representation = new VersionRepresentation(representationId, version.VersionId,
                RepresentationKind.CoreFull, payload.Format, [], MaterializationFidelity.Exact,
                tree.Digest, tree.Digest, payload.Metadata);
            Assert.IsTrue((await Archives.DeepVerifyAsync(representation, payload.PayloadPath, default)).Success);
            var bytes = File.ReadAllBytes(payload.PayloadPath);
            captures.Add(new(source, SourceCaptureOutcome.Captured, CaptureScope.FullSource, tree.Digest, null,
                new(representationId, RepresentationKind.CoreFull, payload.Format, [], MaterializationFidelity.Exact,
                    tree.Digest, tree.Digest, payload.Metadata.SetItem("fileName", Path.GetFileName(payload.PayloadPath))),
                new(LocalReplicaId.New(), representationId, LocalReplicaLocator.ControlledAbsolute(payload.PayloadPath),
                    CapturePayloadState.VerifiedFinal, DateTimeOffset.UtcNow),
                new(payload.PayloadPath, CapturePayloadState.VerifiedFinal, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))),
                workspace?.StateRevision ?? -1, workspace?.GetSourceState(source).BaseVersionId, null, []));
        }
        var now = DateTimeOffset.UtcNow;
        return await History.Commit.CommitAsync(new(Snapshot,
            new(RunId.New(), now, now, BackupInvocationKind.Manual, HistoryProvenance.Native("integration")),
            workspace, captures));
    }

    public HistoryRestoreService Restore(IHistoryRestoreMutationBackend? mutation = null,
        Func<HistoryCommitPack, CancellationToken, Task>? publisher = null)
        => new(History, new RepresentationRuntime([new CoreArchiveRepresentationHandler(Archives.Inner)]),
            async token => new RepresentationEnvironment((await History.LocalReplicaCatalogStore.LoadAsync(token)).Value!.Entries, [], []),
            mutation ?? new FileSystemHistoryRestoreMutationBackend()) { PackPublisher = publisher };

    public HistoryMergeCommitBuilder Builder(HistoryRestoreService? restore = null)
        => new(History, restore ?? Restore(), Archives, Archives);

    public async Task AssertMergedAndRestorableAsync(PreparedMerge prepared)
    {
        var version = prepared.Sources.Single().Version;
        var state = (await History.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(version.VersionId, state.GetSourceState(Source).BaseVersionId);
        Assert.AreEqual(prepared.Checkpoint.CheckpointId, state.GetSourceState(Source).CheckpointAncestryAnchorId);
        Assert.AreEqual(prepared.Update.UpdateId, state.GetSourceState(Source).ActiveBranchUpdateId);
        Assert.AreEqual(Before.GetSourceState(Other), state.GetSourceState(Other));
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(OtherTarget, "keep.txt")));
        Assert.AreEqual(Theirs.NewBranchUpdates.Single().UpdateId,
            (await History.Query.GetBranchTipsAsync(Theirs.NewBranchUpdates.Single().BranchId)).Single().UpdateId);
        var destination = Path.Combine(Root, "restored-" + Guid.NewGuid().ToString("N"));
        var result = await Restore().RestoreVersionAsync(version.VersionId, new(Source, destination), state, HistoryRestoreApplyMode.Clean);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual(prepared.Sources.Single().TreeDigest, (await MergeTreeManifest.ReadAsync(destination, _ => true, default)).Digest);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, Directory.GetFiles(destination).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(Target, "a.txt")), File.ReadAllBytes(Path.Combine(destination, "a.txt")));
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(Target, "b.txt")), File.ReadAllBytes(Path.Combine(destination, "b.txt")));
        Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    public async ValueTask DisposeAsync()
    {
        if (History is not null) await History.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    internal sealed class CountingArchiveBackend(SevenZipArchiveProcessBackend inner) : IHistoryCompactionBackend, IArchiveRepresentationBackend
    {
        public SevenZipArchiveProcessBackend Inner { get; } = inner;
        public int Created { get; private set; }
        public int DeepVerified { get; private set; }
        public int Materialized { get; private set; }
        public bool FailVerification { get; set; }
        public bool ChangeRoundtrip { get; set; }
        public void Reset() { Created = DeepVerified = Materialized = 0; }
        public async Task<HistoryCompactionPayload> CreateFullAsync(SourceVersion version, string directory, RepresentationId id, string output, CancellationToken token)
        { Created++; return await Inner.CreateFullAsync(version, directory, id, output, token); }
        public async ValueTask<PayloadVerificationResult> DeepVerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
        { DeepVerified++; return FailVerification ? new(false, "", "Injected deep verification failure") : await Inner.DeepVerifyAsync(representation, path, token); }
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
            => Inner.VerifyAsync(representation, path, token);
        public async ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
        {
            Materialized++;
            await Inner.MaterializeAsync(inputs, staging, token);
            if (ChangeRoundtrip) File.WriteAllText(Path.Combine(staging, "a.txt"), "Injected roundtrip mismatch");
        }
    }
}
