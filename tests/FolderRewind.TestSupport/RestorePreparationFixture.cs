using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace FolderRewind.Tests;

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
