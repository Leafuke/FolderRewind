using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using FolderRewind.History.Merge;
using System.Collections.Immutable;
using System.IO.Compression;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryMergeApplyTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "FolderRewindMerge", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    [DataRow("success")]
    [DataRow("real-7z")]
    [DataRow("cancel-before-apply")]
    [DataRow("archive-verify-failure")]
    [DataRow("pack-throw")]
    [DataRow("session-db-locked")]
    [DataRow("post-commit-cancel")]
    [DataRow("cleanup-failure")]
    [DataRow("physical-ff-missing")]
    [DataRow("physical-reuse-hash")]
    [DataRow("physical-reuse-nohash")]
    [DataRow("physical-reuse-alternate")]
    [DataRow("physical-alternate-corrupt")]
    [DataRow("candidate-corrupt")]
    [DataRow("candidate-locked")]
    [DataRow("zero-write")]
    [DataRow("one-write")]
    [DataRow("scope-drift")]
    [DataRow("second-source")]
    [DataRow("dirty")]
    [DataRow("dirty-protected")]
    [DataRow("config")]
    [DataRow("stale-session")]
    [DataRow("session-completion")]
    [DataRow("ff-unavailable")]
    [DataRow("reuse-unavailable")]
    [DataRow("provider")]
    [DataRow("provider-changed")]
    public async Task ThreeWayApplyIsAtomicAndLeavesSourceTipUnchanged(string failure)
    {
        var config = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        await using var history = new HistoryRuntime(new FileHistoryRepository(config, new HistoryRepositoryPaths(Path.Combine(_root, failure == "real-7z" ? new string('x', 100) : "short", "repo"))));
        await history.InitializeAsync();
        var handler = new TreeHandler(); var facts = new List<object>();
        var physical = failure.StartsWith("physical", StringComparison.Ordinal);
        var originalReplicas = new List<LocalReplicaCatalogEntry>();
        var payloads = new Dictionary<VersionId, string>();
        var sourceIds = new[] { SourceId.New(), SourceId.New() };
        SourceVersion Version(SourceId source, string name, SourceVersion? parent, params (string Path, string Text)[] files)
        {
            var version = new SourceVersion(VersionId.New(), config, source, parent is null ? [] : [parent.VersionId], DateTimeOffset.UtcNow,
                null, CaptureScope.FullSource, CaptureOutcome.Captured, [], new SourceDescriptorSnapshot(name, name), null, HistoryProvenance.Native("test"));
            var rep = new VersionRepresentation(RepresentationId.New(), version.VersionId, RepresentationKind.CoreFull, "tree", [], MaterializationFidelity.Exact, null, null, null);
            handler.Trees[rep.RepresentationId] = files.ToDictionary(f => f.Path, f => f.Text);
            if (physical)
            {
                var content = Path.Combine(_root, "input-" + version.VersionId); Directory.CreateDirectory(content);
                foreach (var file in files) File.WriteAllText(Path.Combine(content, file.Path), file.Text);
                var path = Path.Combine(_root, version.VersionId + ".zip"); ZipFile.CreateFromDirectory(content, path);
                payloads.Add(version.VersionId, path);
                rep = new(rep.RepresentationId, version.VersionId, RepresentationKind.CoreFull, "zip", [], MaterializationFidelity.Exact, null, null,
                    failure == "physical-reuse-nohash" ? null : ImmutableDictionary<string, string>.Empty.Add("storageSha256",
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))));
                originalReplicas.Add(new(rep.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow));
                if ((failure is "physical-reuse-alternate" or "physical-alternate-corrupt") && name == "theirs")
                {
                    var alternate = path + ".alternate"; File.Copy(path, alternate);
                    originalReplicas.Add(new(rep.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(alternate), DateTimeOffset.UtcNow));
                }
            }
            facts.Add(version); facts.Add(rep); return version;
        }
        var b = sourceIds.Select(id => Version(id, "base", null, ("a.txt", "base-a"), ("b.txt", "base-b"))).ToArray();
        var oursText = (failure == "reuse-unavailable" || physical) ? "base-a" : "ours-a";
        var o = b.Select(v => Version(v.SourceId, "ours", v, ("a.txt", oursText), ("b.txt", "base-b"))).ToArray();
        var t = b.Select(v => Version(v.SourceId, "theirs", v, ("a.txt", "base-a"), ("b.txt", "theirs-b"))).ToArray();
        ConfigurationCheckpoint Checkpoint(SourceVersion[] versions, ConfigurationCheckpoint? parent)
        {
            var cp = new ConfigurationCheckpoint(CheckpointId.New(), config, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("test"),
                versions.Select(v => new CheckpointSource(v.SourceId, v.SourceDescriptorSnapshot, v.VersionId, CheckpointSourceDisposition.Captured, v.EffectiveSourceBoundary)),
                parent is null ? [] : [parent.CheckpointId]);
            facts.Add(cp); return cp;
        }
        var bc = Checkpoint(b, null); var oc = Checkpoint(o, bc); var tc = Checkpoint(t, (failure is "ff-unavailable" or "physical-ff-missing") ? oc : bc);
        var ours = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "ours", oc.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        var theirs = new BranchUpdate(BranchUpdateId.New(), BranchId.New(), [], "theirs", tc.CheckpointId, false, DateTimeOffset.UtcNow, BranchUpdateReason.Created);
        facts.AddRange([ours, theirs]); var codec = new HistoryPackCodec();
        await history.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, facts.Select(f => codec.CreateObject(f))));
        await history.EnsureIndexCurrentAsync();
        var workspace = new HistoryWorkspace(config, 0, ours.BranchId, ours.UpdateId,
            o.Select(v => new WorkspaceSourceBaseline(v.SourceId, v.VersionId, WorkspaceBaselineRelation.Exact)), oc.CheckpointId);
        await history.WorkspaceStore.SaveAsync(workspace, -1);
        await history.LocalReplicaCatalogStore.SaveAsync(new(config, 0, originalReplicas), -1);
        var bindings = sourceIds.Select(id => new HistoryRestoreSourceBinding(id, Path.Combine(_root, id.ToString()), EffectiveSourceBoundarySnapshot.All)).ToArray();
        foreach (var binding in bindings)
        {
            Directory.CreateDirectory(binding.TargetDirectory);
            File.WriteAllText(Path.Combine(binding.TargetDirectory, "a.txt"), oursText);
            File.WriteAllText(Path.Combine(binding.TargetDirectory, "b.txt"), "base-b");
        }
        using var applyCancellation = new CancellationTokenSource();
        FileStream? sessionDbLock = null;
        var backend = new FailingBackend(failure == "second-source", async ct =>
        {
            Assert.IsFalse(ct.IsCancellationRequested);
            if (failure == "session-db-locked" && sessionDbLock is null)
                sessionDbLock = new FileStream(Path.Combine(history.MergeSessions.Root, "sessions.db"), FileMode.Open, FileAccess.Read, FileShare.None);
            if (failure == "post-commit-cancel") applyCancellation.Cancel();
            if (failure == "cleanup-failure") throw new IOException("Injected cleanup failure.");
            await Task.CompletedTask;
        });
        var restore = new HistoryRestoreService(history, new RepresentationRuntime(physical ? [new CoreArchiveRepresentationHandler(new ZipBackend())] : [handler]),
            async ct => new RepresentationEnvironment((await history.LocalReplicaCatalogStore.LoadAsync(ct)).Value!.Entries, [], []), backend)
        {
            PackPublisher = async (pack, ct) =>
            {
                await history.Repository.CommitAsync(pack, cancellationToken: ct);
                if (failure == "pack-throw") throw new IOException("Injected exception after durable pack install.");
            }
        };
        IHistoryMergeProvider provider = failure.StartsWith("provider", StringComparison.Ordinal) ? new ReplacementProvider() : new GenericFileMergeProvider();
        var session = (await new HistoryMergeService(history, restore, provider).StartAsync(theirs.BranchId, "config-1", bindings))!;
        Assert.AreEqual(provider.Descriptor.Identity, session.Plan.ProviderVersion);
        Assert.AreEqual(MergeSessionState.Ready, session.State);
        Assert.HasCount((failure is "ff-unavailable" or "physical-ff-missing") ? 4 : 6, history.MergeSessions.ActiveRoots());
        if (failure.EndsWith("unavailable", StringComparison.Ordinal)) handler.Unavailable.UnionWith(t.Select(v => v.VersionId));
        if (failure is "dirty" or "dirty-protected") File.WriteAllText(Path.Combine(bindings[0].TargetDirectory, "a.txt"), "dirty!");
        if (failure == "stale-session") history.MergeSessions.Update(session, MergeSessionState.Abandoned);
        if (failure == "session-completion")
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + Path.Combine(history.MergeSessions.Root, "sessions.db"));
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_completion BEFORE UPDATE ON sessions WHEN NEW.state=5 BEGIN SELECT RAISE(ABORT, 'injected completion failure'); END;";
            command.ExecuteNonQuery();
        }
        var real7z = failure == "real-7z";
        var executable = FolderRewind.Services.SevenZipExecutableLocator.Resolve(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z"));
        if (real7z && executable is null) Assert.Inconclusive("Set FOLDERREWIND_TEST_7Z to run real 7-Zip integration.");
        IHistoryCompactionBackend archive = real7z
            ? new FolderRewind.Services.SevenZipArchiveProcessBackend(() => executable, () => null, false, ".folderrewind")
            : new ZipBackend(failure == "archive-verify-failure");
        var materializer = (IArchiveRepresentationBackend)archive;
        if (failure is "success" or "provider")
        {
            var builder = new HistoryMergeCommitBuilder(history, restore, archive, materializer);
            var first = await builder.BuildAsync(session);
            var reopened = new MergeSessionStore(history.Repository.Paths.LocalStateRoot).LoadPrepared(session)!.Restore(session);
            Assert.AreEqual(first.PackId, reopened.PackId);
            Assert.AreEqual(first.TransactionId, (await builder.BuildAsync(session)).TransactionId);
            Assert.IsTrue(first.NewReplicas.All(r => r.Locator.AbsolutePath.StartsWith(Path.Combine(history.Repository.Paths.RepositoryRoot, "payloads"), StringComparison.OrdinalIgnoreCase)));
            Assert.HasCount(0, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged).ToArray());
        }
        if (physical)
        {
            foreach (var version in t)
            {
                var path = payloads[version.VersionId]; File.Delete(path);
                if (failure is "physical-reuse-hash" or "physical-reuse-nohash" or "physical-alternate-corrupt")
                {
                    var changed = Path.Combine(_root, "changed-" + version.VersionId); Directory.CreateDirectory(changed);
                    File.WriteAllText(Path.Combine(changed, "a.txt"), "changed"); File.WriteAllText(Path.Combine(changed, "b.txt"), "theirs-b");
                    ZipFile.CreateFromDirectory(changed, path);
                }
            }
        }
        var applyBuilder = new HistoryMergeCommitBuilder(history, restore, archive, materializer);
        HistoryMergeApplyService.CoordinationPlan? coordinated = null;
        PreparedMerge? prepared = null;
        if (failure is "zero-write" or "one-write" or "scope-drift")
        {
            prepared = await applyBuilder.BuildAsync(session);
            if (failure != "scope-drift")
                foreach (var binding in bindings.Take(failure == "zero-write" ? 2 : 1))
                    File.WriteAllText(Path.Combine(binding.TargetDirectory, "b.txt"), "theirs-b");
            coordinated = await new HistoryMergeApplyService(history, restore, applyBuilder)
                .PlanCoordinationAsync(prepared, workspace, bindings);
            Assert.HasCount(failure == "zero-write" ? 0 : failure == "one-write" ? 1 : 2, coordinated.Writes);
            Assert.IsFalse(coordinated.NeedsProtection);
            if (failure == "scope-drift") File.WriteAllText(Path.Combine(bindings[0].TargetDirectory, "a.txt"), "became-dirty");
        }
        FileStream? heldCandidate = null;
        if (failure is "candidate-corrupt" or "candidate-locked")
        {
            prepared = await applyBuilder.BuildAsync(session);
            var path = prepared.NewReplicas[0].Locator.AbsolutePath;
            if (failure == "candidate-corrupt") File.WriteAllText(path, "corrupted");
            else heldCandidate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        using var candidateGuard = heldCandidate;
        var protection = new InsideProtector(async () =>
        {
            var start = facts.Count;
            var snapshotVersion = Version(sourceIds[0], "snapshot", o[0], ("a.txt", "dirty!"), ("b.txt", "base-b"));
            await using var mutation = await history.MutationGate.EnterAsync();
            await history.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
                facts.Skip(start).Select(f => codec.CreateObject(f))));
            await history.EnsureIndexCurrentAsync();
            var next = new HistoryWorkspace(config, workspace.StateRevision + 1, workspace.ActiveBranchId,
                workspace.ActiveBranchUpdateId, workspace.SourceBaselines.Select(b => b.SourceId == sourceIds[0]
                    ? new WorkspaceSourceBaseline(b.SourceId, snapshotVersion.VersionId, WorkspaceBaselineRelation.Exact) : b), workspace.CheckpointAncestryAnchorId);
            await history.WorkspaceStore.SaveAsync(next, workspace.StateRevision);
            return next;
        });
        var failures = new List<(string Stage, Exception Error)>();
        var applyService = new HistoryMergeApplyService(history, restore, new(history, restore, archive, materializer),
            protector: failure == "dirty-protected" ? protection : null,
            reload: _ => Task.FromResult((failure == "config" ? "config-2" : "config-1", (IReadOnlyList<HistoryRestoreSourceBinding>)bindings)),
            provider: failure == "provider-changed" ? provider.Descriptor with { SchemaVersion = 2 } : provider.Descriptor,
            reportFailure: (stage, error) => failures.Add((stage, error)));
        if (failure == "cancel-before-apply")
        {
            applyCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => applyService.ApplyAsync(session, applyCancellation.Token));
            Assert.IsEmpty(failures);
            Assert.HasCount(0, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged));
            foreach (var binding in bindings) Assert.AreEqual("base-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
            return;
        }
        var result = await applyService.ApplyAsync(session, applyCancellation.Token, ready: prepared, coordinated: coordinated);
        if (failure == "archive-verify-failure")
        {
            Assert.HasCount(1, failures);
            Assert.AreEqual("archive-verify", failures[0].Stage);
            StringAssert.Contains(result.MergeDiagnostic!.Detail!, "injected verifier evidence");
            Assert.IsNotNull(failures[0].Error.StackTrace);
        }
        sessionDbLock?.Dispose();
        var current = (await history.WorkspaceStore.LoadAsync()).Value!;
        Assert.AreEqual(theirs.UpdateId, HistoryBranchProjection.Build(await history.Query.GetAllBranchUpdatesAsync()).Single(p => p.BranchId == theirs.BranchId).Tips.Single().UpdateId);
        if (failure is "session-completion" or "session-db-locked")
        {
            Assert.AreEqual(HistoryRestoreStatus.CommittedRecoveryRequired, result.Status, result.Diagnostic);
            Assert.IsTrue(result.TargetCommitted);
            Assert.IsTrue(result.WorkspaceUpdated);
            Assert.HasCount(2, result.AppliedSources);
            Assert.HasCount(1, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged).ToArray());
            if (failure == "session-completion") using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + Path.Combine(history.MergeSessions.Root, "sessions.db")))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "DROP TRIGGER fail_completion"; command.ExecuteNonQuery();
            }
            await restore.RecoverIncompleteAsync();
            Assert.AreEqual(MergeSessionState.Committed, history.MergeSessions.Load(session.Id).State);
            Assert.HasCount(1, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged).ToArray());
        }
        else if (failure == "cleanup-failure")
        {
            Assert.AreEqual(HistoryRestoreStatus.CommittedWithPostActionWarning, result.Status, result.Diagnostic);
            Assert.IsTrue(result.TargetCommitted); Assert.IsTrue(result.WorkspaceUpdated); Assert.HasCount(2, result.AppliedSources);
            var recovery = new HistoryRestoreService(history, new RepresentationRuntime([handler]),
                _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])), new FileSystemHistoryRestoreMutationBackend());
            await recovery.RecoverIncompleteAsync();
            Assert.HasCount(1, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged));
        }
        else if (failure is "physical-reuse-alternate" or "physical-alternate-corrupt")
        {
            Assert.AreEqual(HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.HasCount(1, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged));
            foreach (var binding in bindings) Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
        }
        else if (failure is "real-7z" or "success" or "provider" or "zero-write" or "one-write" or "dirty-protected" or "pack-throw" or "post-commit-cancel")
        {
            Assert.AreEqual(HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.HasCount(failure == "zero-write" ? 0 : failure == "one-write" ? 1 : 2, result.AppliedSources);
            Assert.AreNotEqual(workspace.ActiveBranchUpdateId, current.ActiveBranchUpdateId);
            var merged = (await history.Query.GetAllBranchUpdatesAsync()).Single(u => u.Reason == BranchUpdateReason.Merged);
            Assert.AreEqual(bc.CheckpointId, merged.MergeProvenance!.BaseCheckpointId);
            Assert.HasCount(2, (await history.Query.GetCheckpointAsync(merged.TargetCheckpointId!.Value))!.ParentCheckpointIds);
            Assert.HasCount(2, (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries);
            Assert.HasCount(0, history.MergeSessions.ActiveRoots());
            var timeline = await new HistoryPresentationQueryService(history).QueryAsync();
            Assert.HasCount(2, timeline.Timeline.Where(v => v.CreationKind == SourceVersionCreationKind.Merge));
            Assert.IsFalse(Directory.Exists(history.MergeSessions.SessionDirectory(session.Id)));
            foreach (var replica in (await history.LocalReplicaCatalogStore.LoadAsync()).Value!.Entries)
            {
                Assert.IsTrue(File.Exists(replica.Locator.AbsolutePath));
                var verification = Path.Combine(_root, "verify-" + replica.RepresentationId);
                if (real7z)
                    await materializer.MaterializeAsync([new(new(replica.RepresentationId, VersionId.New(), RepresentationKind.CoreFull,
                        "7z", [], MaterializationFidelity.Exact, null, null, null), replica.Locator.AbsolutePath)], verification, default);
                else ZipFile.ExtractToDirectory(replica.Locator.AbsolutePath, verification);
                Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(verification, "b.txt")));
            }
            foreach (var binding in bindings)
            {
                Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(binding.TargetDirectory, "a.txt")));
                Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
            }
            if (failure is "success" or "real-7z")
            {
                await using var reopened = new HistoryRuntime(new FileHistoryRepository(config, history.Repository.Paths));
                await reopened.InitializeAsync();
                var exactRestore = new HistoryRestoreService(reopened, new RepresentationRuntime([new CoreArchiveRepresentationHandler(materializer)]),
                    async ct => new RepresentationEnvironment((await reopened.LocalReplicaCatalogStore.LoadAsync(ct)).Value!.Entries, [], []),
                    new FileSystemHistoryRestoreMutationBackend());
                foreach (var binding in bindings) File.WriteAllText(Path.Combine(binding.TargetDirectory, "a.txt"), "later-local-edit");
                var restoredResult = await exactRestore.RestoreCheckpointAsync(merged.TargetCheckpointId!.Value, bindings,
                    (await reopened.WorkspaceStore.LoadAsync()).Value!, HistoryCheckpointRestoreScope.CompleteCheckpoint, HistoryRestoreApplyMode.Clean);
                Assert.AreEqual(HistoryRestoreStatus.Committed, restoredResult.Status, restoredResult.Diagnostic);
                foreach (var binding in bindings)
                {
                    Assert.AreEqual("ours-a", File.ReadAllText(Path.Combine(binding.TargetDirectory, "a.txt")));
                    Assert.AreEqual("theirs-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
                }
            }
            var retry = await new HistoryMergeApplyService(history, restore, new(history, restore, archive, materializer)).ApplyAsync(session);
            Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, retry.Status);
        }
        else
        {
            Assert.AreEqual(failure == "second-source" ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.BlockedBeforeMutation, result.Status, result.Diagnostic);
            if (failure == "scope-drift") Assert.AreEqual(MergeDiagnosticCode.CoordinationScopeChanged, result.MergeDiagnostic!.Code);
            if (failure == "config") Assert.AreEqual(MergeSessionState.Stale, history.MergeSessions.Load(session.Id).State);
            Assert.IsTrue(HistoryRestoreTransactionJournalStore.WorkspaceEquals(workspace, current));
            Assert.HasCount(0, (await history.Query.GetAllBranchUpdatesAsync()).Where(u => u.Reason == BranchUpdateReason.Merged).ToArray());
            foreach (var binding in bindings) Assert.AreEqual("base-b", File.ReadAllText(Path.Combine(binding.TargetDirectory, "b.txt")));
        }
    }

    private sealed class InsideProtector(Func<Task<HistoryWorkspace>> protect)
        : IHistoryWorkingStateProtector, IHistoryWorkingStateProtectorInsideOperation
    {
        public Task<HistoryWorkspace> ProtectAsync(HistoryWorkspace expected, CancellationToken token)
            => throw new AssertFailedException("Merge must use the operation lease.");
        public Task<HistoryWorkspace> ProtectInsideOperationAsync(HistoryWorkspace expected,
            FolderRewind.Services.NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken token)
        {
            operation.Require(expected.ConfigId);
            return protect();
        }
    }
    private sealed class TreeHandler : IRepresentationHandler
    {
        public HashSet<VersionId> Unavailable { get; } = [];
        public Dictionary<RepresentationId, Dictionary<string, string>> Trees { get; } = [];
        public bool CanHandle(VersionRepresentation representation) => representation.Format == "tree";
        public ValueTask<RepresentationAssessment> AssessAsync(RepresentationAssessmentContext context, CancellationToken token)
            => ValueTask.FromResult(new RepresentationAssessment(context.Representation.RepresentationId,
                Unavailable.Contains(context.Representation.VersionId) ? HistoryReadiness.Unavailable : HistoryReadiness.Ready, MaterializationFidelity.Exact, [], []));
        public ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
        {
            Directory.CreateDirectory(context.StagingDirectory);
            foreach (var pair in Trees[context.Representation.RepresentationId]) File.WriteAllText(Path.Combine(context.StagingDirectory, pair.Key), pair.Value);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ReplacementProvider : IHistoryMergeProvider
    {
        public MergeProviderDescriptor Descriptor => new("replacement", "2", 1, "conservative", "1");
        public string Version => Descriptor.Identity;
        public MergeFileProposal Analyze(SourceId source, MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t)
            => new GenericFileMergeProvider().Analyze(source, b, o, t);
    }
    private sealed class ZipBackend(bool failVerification = false) : IHistoryCompactionBackend, IArchiveRepresentationBackend
    {
        public Task<HistoryCompactionPayload> CreateFullAsync(SourceVersion version, string source, RepresentationId id, string output, CancellationToken token)
        {
            Directory.CreateDirectory(output); var path = Path.Combine(output, "full.zip"); ZipFile.CreateFromDirectory(source, path);
            return Task.FromResult(new HistoryCompactionPayload("zip", path, new FileInfo(path).Length, null, null, ImmutableDictionary<string, string>.Empty));
        }
        public ValueTask<PayloadVerificationResult> DeepVerifyAsync(VersionRepresentation representation, string path, CancellationToken token) => failVerification ? ValueTask.FromResult(new PayloadVerificationResult(false, "", "injected verifier evidence")) : VerifyAsync(representation, path, token);
        public ValueTask<PayloadVerificationResult> VerifyAsync(VersionRepresentation representation, string path, CancellationToken token)
        { using var zip = ZipFile.OpenRead(path); foreach (var entry in zip.Entries) { using var s = entry.Open(); s.CopyTo(Stream.Null); } return ValueTask.FromResult(new PayloadVerificationResult(true, "zip", "")); }
        public ValueTask MaterializeAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
        { foreach (var input in inputs) ZipFile.ExtractToDirectory(input.LocalPath, staging); return ValueTask.CompletedTask; }
    }
    private sealed class FailingBackend(bool fail, Func<CancellationToken, Task>? postCommit = null) : IHistoryRestoreMutationBackend
    {
        private readonly FileSystemHistoryRestoreMutationBackend _inner = new(); private int _count;
        public HistoryRestoreRollbackSnapshot PlanRollback(HistoryRestoreSourceBinding source, HistoryTransactionId transaction) => _inner.PlanRollback(source, transaction);
        public Task PrepareRollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => _inner.PrepareRollbackAsync(snapshot, token);
        public Task ApplyAsync(HistoryRestoreSourceBinding source, string staging, HistoryRestoreApplyMode mode, HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
        { if (fail && ++_count == 2) throw new IOException("Injected second Source failure."); return _inner.ApplyAsync(source, staging, mode, snapshot, token); }
        public Task RollbackAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token) => _inner.RollbackAsync(snapshot, token);
        public async Task CommitAsync(HistoryRestoreRollbackSnapshot snapshot, CancellationToken token)
        { await _inner.CommitAsync(snapshot, token); if (postCommit is not null) await postCommit(token); }
    }
}
