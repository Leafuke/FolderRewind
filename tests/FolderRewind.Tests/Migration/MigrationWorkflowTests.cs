using FolderRewind.Services;
using FolderRewind.History.Application;
using System.Collections.Immutable;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MigrationWorkflowTests
{
    private string _root = null!;
    private static string Id() => Guid.NewGuid().ToString("N");
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "MigrationWorkflow", Id()); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private static MigrationWorkflowProject Project() => new(Id(), "world");
    private static MigrationProjectInspection Inspect(MigrationWorkflowProject project, bool identity = true, int candidates = 1)
        => new(project.ConfigId, "config-v1", "activation-1", [new("representation", "world", "world.7z",
            [.. Enumerable.Range(0, candidates).Select(i => new MigrationArchiveCandidate("archive-" + i, 5, "sha-" + i, identity, true))])],
            [new("source", "world", true, false, "main-tip", [new("main-tip", "main", "version")]), new("empty-source", "mods", false, false, null, [])]);

    [TestMethod] public void UniqueHashAndOriginalBranchNeedNoUserDecisionAndEmptySourcesHaveNoSelector()
    {
        var project = Project(); var decision = MigrationWorkflowPolicy.Decide(Inspect(project), project);
        Assert.IsNull(decision.Attention); Assert.HasCount(1, decision.Archives);
        Assert.AreEqual("main-tip", decision.Branches[0].UpdateId); Assert.IsNull(decision.Branches[1].UpdateId);
    }
    [TestMethod] public void LegacyArchiveAndAmbiguousCandidatesCannotBeAutomaticallyClaimed()
    {
        var project = Project();
        Assert.AreEqual(MigrationProjectPhase.AwaitingArchiveChoice, MigrationWorkflowPolicy.Decide(Inspect(project, false), project).Attention);
        Assert.AreEqual(MigrationProjectPhase.AwaitingArchiveChoice, MigrationWorkflowPolicy.Decide(Inspect(project, true, 2), project).Attention);
    }
    [TestMethod] public void ConfirmationIsBoundToConfigurationAndArchiveBytes()
    {
        var project = Project() with { ConfigurationSignature = "config-v1", ConfigImportId = "activation-1",
            ArchiveSelections = [new("representation", "archive-0", 5, "sha-0", true)] };
        Assert.IsNull(MigrationWorkflowPolicy.Decide(Inspect(project, false), project).Attention);
        Assert.AreEqual(MigrationProjectPhase.AwaitingArchiveChoice, MigrationWorkflowPolicy.Decide(Inspect(project, false) with { ConfigurationSignature = "changed" }, project).Attention);
        var changed = Inspect(project, false) with { Archives = [new("representation", "world", "world.7z", [new("archive-0", 5, "changed-bytes", false)])] };
        Assert.AreEqual(MigrationProjectPhase.AwaitingArchiveChoice, MigrationWorkflowPolicy.Decide(changed, project).Attention);
    }
    [TestMethod] public void MultipleBranchesNeedChoiceAndCompetingTipsRequireReconciliation()
    {
        var project = Project(); var inspection = Inspect(project) with
        { Branches = [new("source", "world", true, false, null, [new("tip-1", "main", "v1"), new("tip-2", "other", "v2")])] };
        Assert.AreEqual(MigrationProjectPhase.AwaitingBranchChoice, MigrationWorkflowPolicy.Decide(inspection, project).Attention);
        Assert.AreEqual(MigrationProjectPhase.ReconciliationRequired, MigrationWorkflowPolicy.Decide(inspection with
        { Branches = [new("source", "world", true, true, null, [])] }, project).Attention);
    }
    [TestMethod] public async Task StoreRoundTripResumesAfterRestartWithoutReimportOrRepeatedRecovery()
    {
        var project = Project(); var document = new MigrationWorkflowDocument(Id(), [project]) { ImportCommitted = true };
        var store = new MigrationWorkflowStore(_root); store.Save(document); var backend = new Backend();
        var first = await new MigrationWorkflowRunner(store, backend).RunAsync(document.WorkflowId);
        Assert.AreEqual(MigrationWorkflowStage.AwaitingPermissions, first.Stage); Assert.AreEqual(1, backend.Recoveries);
        var restarted = await new MigrationWorkflowRunner(new(_root), backend).RunAsync(document.WorkflowId);
        Assert.AreEqual(MigrationWorkflowStage.AwaitingPermissions, restarted.Stage); Assert.AreEqual(1, backend.Recoveries);
        var done = await new MigrationWorkflowRunner(new(_root), backend).ConfirmAsync(document.WorkflowId);
        Assert.AreEqual(MigrationWorkflowStage.Completed, done.Stage); Assert.IsFalse(backend.CloudGranted);
    }
    [TestMethod] public async Task CancellationKeepsEarlierProjectsAndResumesOnlyRemainingWork()
    {
        var first = Project(); var second = Project(); var document = new MigrationWorkflowDocument(Id(), [first, second]);
        var store = new MigrationWorkflowStore(_root); store.Save(document); using var cancellation = new CancellationTokenSource();
        var backend = new Backend { AfterRecovery = count => { if (count == 1) cancellation.Cancel(); } };
        var paused = await new MigrationWorkflowRunner(store, backend).RunAsync(document.WorkflowId, cancellation.Token);
        Assert.AreEqual(MigrationWorkflowStage.Paused, paused.Stage); Assert.IsTrue(paused.Projects[0].RecoveryVerified);
        backend.AfterRecovery = null;
        await new MigrationWorkflowRunner(new(_root), backend).RunAsync(document.WorkflowId);
        Assert.AreEqual(2, backend.Recoveries);
    }
    [TestMethod] public async Task ConfigurationChangeInvalidatesOldRecoveryBeforePermissionGrant()
    {
        var document = new MigrationWorkflowDocument(Id(), [Project()]); var store = new MigrationWorkflowStore(_root); store.Save(document); var backend = new Backend();
        var runner = new MigrationWorkflowRunner(store, backend); await runner.RunAsync(document.WorkflowId);
        backend.Current = false;
        var result = await runner.ConfirmAsync(document.WorkflowId);
        Assert.AreEqual(MigrationWorkflowStage.NeedsAttention, result.Stage); Assert.AreEqual(0, backend.Grants);
        Assert.IsFalse(result.Projects[0].RecoveryVerified);
    }
    [TestMethod] public async Task MissingVersionsNeedAcknowledgementAndDoNotBlockOtherReadyProjects()
    {
        var first = Project(); var second = Project(); var document = new MigrationWorkflowDocument(Id(), [first, second]);
        var store = new MigrationWorkflowStore(_root); store.Save(document); var backend = new Backend { MissingProject = first.ConfigId };
        var runner = new MigrationWorkflowRunner(store, backend); var result = await runner.RunAsync(document.WorkflowId);
        Assert.AreEqual(MigrationProjectPhase.AwaitingArchiveResolution, result.Projects[0].Phase);
        result = await runner.ConfirmAsync(document.WorkflowId);
        Assert.AreEqual(MigrationProjectPhase.Completed, result.Projects[1].Phase);
        store.Save(result with { Projects = result.Projects.SetItem(0, result.Projects[0] with { RetainMissingArchives = true }) });
        await runner.RunAsync(document.WorkflowId); result = await runner.ConfirmAsync(document.WorkflowId);
        Assert.AreEqual(MigrationWorkflowStage.PartiallyCompleted, result.Stage);
    }
    [TestMethod] public async Task PasswordAndHistoryWaitStatesNeverReachRecoveryOrPermissionGrant()
    {
        foreach (var phase in new[] { MigrationProjectPhase.AwaitingPassword, MigrationProjectPhase.AwaitingHistory, MigrationProjectPhase.AwaitingPaths })
        {
            var document = new MigrationWorkflowDocument(Id(), [Project()]); var store = new MigrationWorkflowStore(_root); store.Save(document);
            var backend = new Backend { Block = phase }; var result = await new MigrationWorkflowRunner(store, backend).RunAsync(document.WorkflowId);
            Assert.AreEqual(phase, result.Projects[0].Phase); Assert.AreEqual(0, backend.Recoveries); Assert.AreEqual(0, backend.Grants);
        }
    }
    [TestMethod] public async Task WaitingForActivationDoesNotTouchArchivesAndMissingTaskDoesNotLeakExecutionLease()
    {
        var store = new MigrationWorkflowStore(_root); var backend = new Backend(); var runner = new MigrationWorkflowRunner(store, backend);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync(Id()));
        var document = new MigrationWorkflowDocument(Id(), [Project()]) { Stage = MigrationWorkflowStage.AwaitingRestart }; store.Save(document);
        Assert.AreEqual(MigrationWorkflowStage.AwaitingRestart, (await runner.RunAsync(document.WorkflowId)).Stage); Assert.AreEqual(0, backend.Recoveries);
    }
    [TestMethod] public void WorkflowStoreRejectsEscapingIdentitiesAndDuplicateProjects()
    {
        var store = new MigrationWorkflowStore(_root); Assert.ThrowsExactly<InvalidDataException>(() => store.Load("../outside"));
        var project = Project(); Assert.ThrowsExactly<InvalidDataException>(() => store.Save(new(Id(), [project, project])));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(new(Id(), [project]) { ImportProjectIds = [Id()] }));
    }

    [TestMethod] public async Task DecliningBackupPermissionKeepsWorkflowPendingAndDoesNotClaimCompletion()
    {
        var document = new MigrationWorkflowDocument(Id(), [Project() with { BackupSelected = false }]);
        var store = new MigrationWorkflowStore(_root); store.Save(document); var runner = new MigrationWorkflowRunner(store, new Backend());
        await runner.RunAsync(document.WorkflowId);
        Assert.AreEqual(MigrationWorkflowStage.AwaitingPermissions, (await runner.ConfirmAsync(document.WorkflowId)).Stage);
    }

    [TestMethod] public async Task FailedIncomingHistoryCannotBeHiddenByPreviouslyRecoverableVersions()
    {
        var failed = Project() with { ImportPending = true, Phase = MigrationProjectPhase.Blocked };
        var document = new MigrationWorkflowDocument(Id(), [failed, Project()]);
        var store = new MigrationWorkflowStore(_root); store.Save(document); var backend = new Backend();
        var runner = new MigrationWorkflowRunner(store, backend); await runner.RunAsync(document.WorkflowId);
        var result = await runner.ConfirmAsync(document.WorkflowId);
        Assert.AreEqual(1, backend.Recoveries); Assert.AreEqual(MigrationProjectPhase.Blocked, result.Projects[0].Phase);
        Assert.AreEqual(MigrationProjectPhase.Completed, result.Projects[1].Phase); Assert.IsFalse(result.IsFinished);
    }

    [TestMethod] public async Task SameInstallationRejectsConcurrentExecutionButIndependentInstallationsDoNotShareALease()
    {
        var document = new MigrationWorkflowDocument(Id(), [Project()]); var store = new MigrationWorkflowStore(_root); store.Save(document);
        var backend = new Backend { InspectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var running = new MigrationWorkflowRunner(store, backend).RunAsync(document.WorkflowId);
        await backend.InspectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MigrationWorkflowRunner(new(_root), new Backend()).RunAsync(document.WorkflowId));
            var separate = new MigrationWorkflowStore(Path.Combine(_root, "other")); separate.Save(document);
            Assert.AreEqual(MigrationWorkflowStage.AwaitingPermissions, (await new MigrationWorkflowRunner(separate, new Backend()).RunAsync(document.WorkflowId)).Stage);
        }
        finally { backend.InspectGate.SetResult(); await running; }
    }

    [TestMethod] public void CorruptProgressIsReportedWithoutHidingOtherTasks()
    {
        var store = new MigrationWorkflowStore(_root); var document = new MigrationWorkflowDocument(Id(), [Project()]); store.Save(document);
        File.WriteAllText(Path.Combine(store.DirectoryKey, Id() + ".json"), "{broken");
        var errors = new List<Exception>(); var valid = store.LoadAll((_, error) => errors.Add(error));
        Assert.HasCount(1, errors); Assert.AreEqual(document.WorkflowId, valid.Single().WorkflowId);
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => store.LoadAll());
    }

    [TestMethod] public void ConfigurationActivationCorrelationSurvivesReloadAndRollbackAndDoesNotMatchOtherFlows()
    {
        var path = Path.Combine(_root, "config.json"); File.WriteAllText(path, "{}"); var id = Id(); var project = Id();
        var transaction = new ConfigImportTransactionStore(path);
        transaction.Stage("{\"new\":true}"u8.ToArray(), _ => null, [project], migrationWorkflowId: id);
        var reloaded = new ConfigImportTransactionStore(path);
        Assert.AreEqual(ConfigImportStatus.StagedRequiresRestart, reloaded.ImportForWorkflow(id, out var activation)!.Status);
        Assert.IsNull(reloaded.ImportForWorkflow(Id(), out _)); Assert.AreEqual(project, reloaded.ProjectsForWorkflow(id).Single());
        Assert.IsTrue(reloaded.BeginActivation(_ => null)); reloaded.Rollback("injected activation failure");
        Assert.AreEqual(ConfigImportStatus.RolledBack, new ConfigImportTransactionStore(path).ImportForWorkflow(id, out var rolledBack)!.Status);
        Assert.AreEqual(activation, rolledBack); Assert.AreEqual("{}", File.ReadAllText(path));
        reloaded.Stage("{\"new\":true}"u8.ToArray(), _ => null, [project], migrationWorkflowId: id);
        Assert.IsTrue(reloaded.BeginActivation(_ => null)); reloaded.CommitActivation();
        Assert.AreEqual(ConfigImportStatus.Applied, new ConfigImportTransactionStore(path).ImportForWorkflow(id, out _)!.Status);
    }

    private sealed class Backend : IMigrationWorkflowBackend
    {
        private readonly HashSet<string> _released = [];
        internal int Recoveries, Grants; internal bool Current = true, CloudGranted; internal string? MissingProject;
        internal MigrationProjectPhase? Block; internal Action<int>? AfterRecovery;
        internal TaskCompletionSource? InspectGate;
        internal readonly TaskCompletionSource InspectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> IsRecoveryCurrentAsync(MigrationWorkflowProject project, CancellationToken token) => Task.FromResult(Current);
        public async Task<MigrationProjectInspection> InspectAsync(MigrationWorkflowProject project, CancellationToken token)
        { InspectEntered.TrySetResult(); if (InspectGate is not null) await InspectGate.Task.WaitAsync(token); return Inspect(project) with { BlockingPhase = Block }; }
        public Task<MigrationWorkflowRecovery> RecoverAsync(MigrationProjectInspection inspection, MigrationRecoveryDecision decision, CancellationToken token)
        { Recoveries++; AfterRecovery?.Invoke(Recoveries); return Task.FromResult(new MigrationWorkflowRecovery(inspection.ConfigId == MissingProject ? 1 : 2, 2, 0, true, [])); }
        public Task<MigrationWorkflowPermission> InspectPermissionsAsync(MigrationWorkflowProject project, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new MigrationWorkflowPermission(!_released.Contains(project.ConfigId), true, false, _released.Contains(project.ConfigId), CloudGranted, [])); }
        public Task GrantAsync(MigrationWorkflowProject project, CancellationToken token)
        { Grants++; if (project.BackupSelected) _released.Add(project.ConfigId); CloudGranted |= project.CloudSelected; return Task.CompletedTask; }
    }
}
