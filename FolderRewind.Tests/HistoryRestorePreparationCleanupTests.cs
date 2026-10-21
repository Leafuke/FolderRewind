using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryRestorePreparationCleanupTests
{
    [TestMethod]
    [DataRow("null")]
    [DataRow("envelope")]
    public async Task DamagedCompletedJournalCannotAuthorizePreparationCleanup(string damage)
    {
        await using var f = await RestorePreparationFixture.CreateAsync();
        var journal = f.CreateLegacyPreparation();
        f.Journals.Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
        var directory = f.Journals.TransactionDirectory(journal.TransactionId);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var cleanupPath = Path.Combine(directory, "restore-cleanup.json");
        File.WriteAllBytes(cleanupPath, JsonSerializer.SerializeToUtf8Bytes(new HistoryRestoreCleanupRecord(2,
            RestoreCleanupKind.AbandonedPreparation, f.Config, journal.TransactionId, journal.Workspaces), options));
        var path = Path.Combine(directory, "restore-journal.json");
        if (damage == "null") File.WriteAllText(path, "null");
        else
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            json["expectedWorkspace"] = null; File.WriteAllText(path, json.ToJsonString());
        }
        f.Journals.RetryDeferredCleanup();
        Assert.IsTrue(Directory.Exists(journal.Workspaces.Single().Location.Container));
        Assert.IsTrue(File.Exists(cleanupPath));
        Assert.IsNotNull(f.History.MaintenanceDiagnostic);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PartialMaterializationFailureOrCancellationAllowsNextMutation(bool canceled)
    {
        await using var f = await RestorePreparationFixture.CreateAsync();
        var handler = new PartialRestoreMaterializer(canceled);
        var result = await f.Restore(handler).RestoreVersionAsync(f.Version.VersionId, new(f.Source, f.Target),
            f.Expected, HistoryRestoreApplyMode.Clean);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status);
        StringAssert.Contains(result.Diagnostic, "partial materialization");
        Assert.IsTrue(handler.Called);
        await f.AssertRecoveredAsync();
        Assert.IsEmpty(Directory.GetDirectories(f.Root, ".folderrewind-restore-*"));
    }

    [TestMethod]
    public async Task OriginalMaterializationErrorIncludesFailedPreparationAbort()
    {
        await using var f = await RestorePreparationFixture.CreateAsync();
        var handler = new PartialRestoreMaterializer(false, rollbackEvidence: true);
        var result = await f.Restore(handler).RestoreVersionAsync(f.Version.VersionId, new(f.Source, f.Target),
            f.Expected, HistoryRestoreApplyMode.Clean);
        StringAssert.Contains(result.Diagnostic, "partial materialization");
        StringAssert.Contains(result.Diagnostic, "rollback");
        Assert.Throws<InvalidDataException>(() => HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot));
        Assert.AreEqual("original world", File.ReadAllText(Path.Combine(f.Target, "original.txt")));
    }

    [TestMethod]
    public async Task BuildDeltaBoundaryConflictLeavesOnlyDisposablePreparation()
    {
        var boundary = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [],
            EffectiveBoundaryFilterMode.Blacklist, ["shape/keep.dat"], false);
        await using var f = await RestorePreparationFixture.CreateAsync(boundary: boundary);
        Directory.CreateDirectory(Path.Combine(f.Target, "shape"));
        File.WriteAllText(Path.Combine(f.Target, "shape", "keep.dat"), "excluded data");
        var result = await f.Restore(new ShapeMaterializer()).RestoreVersionAsync(f.Version.VersionId,
            new(f.Source, f.Target, boundary), f.Expected, HistoryRestoreApplyMode.Clean);
        Assert.AreEqual(HistoryRestoreStatus.BlockedBeforeMutation, result.Status);
        StringAssert.Contains(result.Diagnostic, "boundary-excluded");
        await f.AssertRecoveredAsync();
        Assert.AreEqual("excluded data", File.ReadAllText(Path.Combine(f.Target, "shape", "keep.dat")));
        Assert.IsEmpty(Directory.GetDirectories(f.Root, ".folderrewind-restore-*"));
    }

    [TestMethod]
    public async Task Legacy197PreparationWithIncompleteKnownObjectsDoesNotRequireOldWorkspaceRevision()
    {
        await using var f = await RestorePreparationFixture.CreateAsync();
        var journal = f.CreateLegacyPreparation();
        var next = f.Expected.WithSourceStates([f.Expected.GetSourceState(f.Source) with { RequiresFullCapture = true }]);
        await f.History.WorkspaceStore.SaveAsync(next, f.Expected.StateRevision);
        Assert.HasCount(3, journal.Workspaces.Single().KnownObjects);
        await f.AssertRecoveredAsync();
        Assert.IsTrue(HistoryWorkspace.StateEquals(next, (await f.History.WorkspaceStore.LoadAsync()).Value!));
        Assert.IsFalse(Directory.Exists(journal.Workspaces.Single().Location.Container));
    }

    [TestMethod]
    [DataRow(false, "marker")]
    [DataRow(true, "marker")]
    [DataRow(false, "marker-missing")]
    [DataRow(true, "marker-missing")]
    [DataRow(false, "acl")]
    [DataRow(true, "acl")]
    [DataRow(false, "identity")]
    [DataRow(true, "identity")]
    [DataRow(false, "link")]
    [DataRow(true, "link")]
    public async Task UnprovenPreparationIsPreservedAsMaintenance(bool legacy, string change)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Restore ownership requires Windows."); return; }
        await using var f = await RestorePreparationFixture.CreateAsync();
        var journal = f.CreateLegacyPreparation(publishLegacy: legacy);
        var container = journal.Workspaces.Single().Location.Container;
        var security = new DirectoryInfo(container).GetAccessControl(AccessControlSections.Access);
        var originalAcl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string? moved = null;
        try
        {
            switch (change)
            {
                case "marker": File.WriteAllText(Path.Combine(container, "owner.json"), "foreign marker"); break;
                case "marker-missing": File.Delete(Path.Combine(container, "owner.json")); break;
                case "acl":
                    security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                        FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                    new DirectoryInfo(container).SetAccessControl(security);
                    break;
                case "identity":
                    moved = Path.Combine(Path.GetDirectoryName(container)!, "original-container");
                    Directory.Move(container, moved);
                    Directory.CreateDirectory(container);
                    File.WriteAllText(Path.Combine(container, "foreign"), "must remain");
                    break;
                case "link":
                    var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
                    File.WriteAllText(Path.Combine(outside, "foreign"), "must remain");
                    Directory.CreateSymbolicLink(Path.Combine(container, "staging", "link"), outside);
                    break;
            }
            await f.AssertRecoveredAsync();
            Assert.IsTrue(Directory.Exists(container));
            Assert.IsNotNull(f.History.MaintenanceDiagnostic);
            Assert.IsNotEmpty(Directory.GetFiles(f.History.Repository.Paths.TransactionsRoot, "restore-cleanup.json", SearchOption.AllDirectories));
            if (change == "link") Assert.AreEqual("must remain", File.ReadAllText(Path.Combine(f.Root, "outside", "foreign")));
            if (change == "identity") Assert.AreEqual("must remain", File.ReadAllText(Path.Combine(container, "foreign")));
        }
        finally
        {
            if (change == "acl" && Directory.Exists(container))
            {
                var restore = new DirectorySecurity(); restore.SetSecurityDescriptorSddlForm(originalAcl, AccessControlSections.Access);
                new DirectoryInfo(container).SetAccessControl(restore);
            }
            if (change == "link") Directory.Delete(Path.Combine(container, "staging", "link"));
        }
    }

    [TestMethod]
    [DataRow(false, "rollback")]
    [DataRow(true, "rollback")]
    [DataRow(false, "progress")]
    [DataRow(true, "progress")]
    [DataRow(true, "started")]
    [DataRow(true, "target")]
    public async Task ContradictoryPreparationStillBlocksMutation(bool legacy, string evidence)
    {
        await using var f = await RestorePreparationFixture.CreateAsync();
        var journal = f.CreateLegacyPreparation(publishLegacy: legacy);
        var container = journal.Workspaces.Single().Location.Container;
        switch (evidence)
        {
            case "rollback": Directory.CreateDirectory(Path.Combine(container, "rollback")); break;
            case "progress": File.WriteAllBytes(Path.Combine(f.Journals.TransactionDirectory(journal.TransactionId), "restore-progress.bin"), [1]); break;
            case "started": f.Journals.Save(journal with { StartedSources = [f.Source] }); break;
            case "target":
                Directory.Move(f.Target, Path.Combine(f.Root, "original-world"));
                Directory.CreateDirectory(f.Target); File.WriteAllText(Path.Combine(f.Target, "original.txt"), "original world");
                break;
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Journals.RecoverIncompleteAsync(default));
        Assert.IsTrue(Directory.Exists(container));
        Assert.Throws<Exception>(() => HistoryRestoreTransactionJournalStore.RequireRecovered(f.History.Repository.Paths.TransactionsRoot));
    }

    private sealed class ShapeMaterializer : ReadyRestoreMaterializer
    {
        public override ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
        { File.WriteAllText(Path.Combine(context.StagingDirectory, "shape"), "replacement"); return ValueTask.CompletedTask; }
    }
}

internal class ReadyRestoreMaterializer : IRepresentationHandler
{
    public bool CanHandle(VersionRepresentation representation) => true;
    public ValueTask<RepresentationAssessment> AssessAsync(RepresentationAssessmentContext context, CancellationToken token)
        => ValueTask.FromResult(new RepresentationAssessment(context.Representation.RepresentationId,
            HistoryReadiness.Ready, MaterializationFidelity.Exact, [], []));
    public virtual ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token) => ValueTask.CompletedTask;
}

internal sealed class PartialRestoreMaterializer(bool canceled = false, bool rollbackEvidence = false) : ReadyRestoreMaterializer
{
    internal bool Called;
    public override ValueTask MaterializeAsync(RepresentationMaterializationContext context, CancellationToken token)
    {
        Called = true;
        Directory.CreateDirectory(Path.Combine(context.StagingDirectory, "region"));
        File.WriteAllText(Path.Combine(context.StagingDirectory, "region", "r.0.0.mca"), "partial output");
        HistoryRestoreFileSystemPlanStore.Reach("after-partial-materialization");
        if (rollbackEvidence) Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(context.StagingDirectory)!, "rollback"));
        if (canceled) throw new OperationCanceledException("Canceled partial materialization.");
        throw new IOException("Failed partial materialization.");
    }
}

internal sealed class RestorePreparationFixture : IAsyncDisposable
{
    internal string Root { get; }
    internal string Repository => Path.Combine(Root, "history");
    internal string Target => Path.Combine(Root, "world");
    internal HistoryConfigId Config { get; } = new(Guid.NewGuid().ToString("N"));
    internal SourceId Source { get; } = SourceId.New();
    internal HistoryRuntime History { get; private set; } = null!;
    internal HistoryWorkspace Expected { get; private set; } = null!;
    internal SourceVersion Version { get; private set; } = null!;
    internal HistoryRestoreTransactionJournalStore Journals { get; private set; } = null!;
    private RestorePreparationFixture(string? root) => Root = root ?? Path.Combine(Path.GetTempPath(), "fr-preparation-cleanup-" + Guid.NewGuid().ToString("N"));
    internal static async Task<RestorePreparationFixture> CreateAsync(string? root = null, EffectiveSourceBoundarySnapshot? boundary = null)
    {
        var f = new RestorePreparationFixture(root);
        Directory.CreateDirectory(f.Target); File.WriteAllText(Path.Combine(f.Target, "original.txt"), "original world");
        f.History = new(new FileHistoryRepository(f.Config, new(f.Repository))); await f.History.InitializeAsync();
        f.Expected = new(f.Config, 0, [new(f.Source, null, WorkspaceBaselineRelation.Unknown)]);
        await f.History.WorkspaceStore.SaveAsync(f.Expected, -1);
        f.Version = new(VersionId.New(), f.Config, f.Source, [], DateTimeOffset.UtcNow, null,
            CaptureScope.FullSource, CaptureOutcome.Captured, [], new("world", f.Target), null, HistoryProvenance.Native("cleanup-test"),
            boundary ?? EffectiveSourceBoundarySnapshot.All);
        var representation = new VersionRepresentation(RepresentationId.New(), f.Version.VersionId, RepresentationKind.CoreFull,
            "cleanup-test", [], MaterializationFidelity.Exact, null, null, []);
        var codec = new HistoryPackCodec();
        await f.History.Repository.CommitAsync(new(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [codec.CreateObject(f.Version), codec.CreateObject(representation)]));
        await f.History.EnsureIndexCurrentAsync();
        f.Journals = new(f.History, new FileSystemHistoryRestoreMutationBackend());
        return f;
    }
    internal HistoryRestoreService Restore(IRepresentationHandler handler) => new(History, new RepresentationRuntime([handler]),
        _ => Task.FromResult<IRepresentationEnvironment>(new RepresentationEnvironment([], [], [])),
        new FileSystemHistoryRestoreMutationBackend()) { UseTargetVolumeWorkspaces = true, AllowDirectorySwap = false };
    internal HistoryRestoreTransactionJournal CreateLegacyPreparation(bool publishLegacy = true)
    {
        using var session = new HistoryRestorePreparationSession(History, Journals, Expected, [new(Source, Target)], []);
        var staging = session.CreateStaging(Source);
        var registration = session.Journal.Workspaces.Single();
        var known = HistoryRestoreFileSystemPlanStore.CaptureTree(registration.Location.Container);
        File.WriteAllText(Path.Combine(staging, "partial"), "unregistered output");
        var journal = session.Journal with { Workspaces = [registration with { KnownObjects = [.. known] }] };
        if (publishLegacy)
        {
            Journals.Save(journal);
            File.Delete(Journals.PreparationPath(journal.TransactionId));
        }
        session.Finish();
        return journal;
    }
    internal async Task AssertRecoveredAsync()
    {
        for (int repeat = 0; repeat < 2; repeat++)
        { await Journals.RecoverIncompleteAsync(default); await History.DrainRestoreCleanupAsync(); }
        HistoryRestoreTransactionJournalStore.RequireRecovered(History.Repository.Paths.TransactionsRoot);
        await using var admitted = await History.MutationGate.EnterAsync();
        Assert.AreEqual("original world", File.ReadAllText(Path.Combine(Target, "original.txt")));
    }
    public async ValueTask DisposeAsync()
    {
        await History.DisposeAsync();
        if (Root.StartsWith(Path.Combine(Path.GetTempPath(), "fr-preparation-cleanup-"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(Root, true);
    }
}
