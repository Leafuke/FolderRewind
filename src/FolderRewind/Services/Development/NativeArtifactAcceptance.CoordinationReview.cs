#if MINECRAFT_COORDINATION_UI_ACCEPTANCE
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private static async Task<int> RunCoordinationReviewAsync(BackupConfig config, string root)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = budget.Token;
        var transport = _coordinationServices!;
        transport.Enabled = true; transport.SimulateHostTransport = true;
        config.ArtifactTransformPolicy = null;
        config.Archive.Mode = BackupMode.Full; config.Archive.SkipIfUnchanged = false; config.Archive.BackupBeforeRestore = true;
        var folder = config.SourceFolders.Single(); var source = new SourceId(Guid.Parse(folder.Id));
        var liveFile = Path.Combine(folder.Path, "coordination-review.txt");
        await File.WriteAllTextAsync(liveFile, "baseline", token);
        var initial = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual(), cancellationToken: token);
        Check(initial.CreatedNewArchive, "Initial real Host capture failed.");
        var version = initial.CommittedBatch!.Run.SourceResults.Single(r => r.SourceId == source).VersionId!.Value;
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token);
        var checks = new List<string>();
        FileStream? occupied = null;
        try
        {
            foreach (var phase in new[] { "restore-safety-capture-start", "restore-safety-source-read", "restore-safety-capture-committed" })
            {
                await File.WriteAllTextAsync(liveFile, phase, token);
                var before = (await history.Query.GetSafetySnapshotsAsync(token)).Count;
                var triggered = false;
                transport.Events.Clear();
                Observation = observed =>
                {
                    if (observed != phase || triggered) return;
                    triggered = true; occupied = Hold();
                };
                var result = await Restore();
                Check(triggered && result.Status == HistoryRestoreStatus.BlockedBeforeMutation, phase + ": restore was not blocked.");
                Check(File.ReadAllText(liveFile) == phase, phase + ": target was changed.");
                Check(result.Diagnostic.Contains(I18n.GetString("OperationDiagnostic_RestoreCoordinationChanged"), StringComparison.Ordinal), phase + ": retry guidance missing.");
                Check(!transport.Events.Any(e => e is "handshake:backup" or "pre_hot_backup" or "backup_success" or "backup_failed"), phase + ": internal capture emitted backup protocol.");
                var after = (await history.Query.GetSafetySnapshotsAsync(token)).Count;
                Check(after == before + (phase.EndsWith("committed", StringComparison.Ordinal) ? 1 : 0), phase + ": unexpected safety snapshot facts.");
                occupied!.Dispose(); occupied = null; Observation = null;
                CheckIdle(); checks.Add(phase);
            }

            // Both paths are production Host operations: the outer restore holds its configuration
            // lease at safety-capture entry while a separate backup admits the channel first.
            await File.WriteAllTextAsync(liveFile, "lock-inversion", token);
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<BackupService.BackupTransactionExecutionResult>? competitor = null;
            transport.Events.Clear();
            Observation = phase =>
            {
                if (phase == "backup-channel-admitted" && competitor is not null) admitted.TrySetResult();
                if (phase != "restore-safety-capture-start" || competitor is not null) return;
                occupied = Hold();
                using (ExecutionContext.SuppressFlow())
                    competitor = Task.Run(() => BackupService.ExecuteBackupTransactionAsync(config, [folder],
                        BackupInvocationOptions.ForManual(), cancellationToken: token));
                admitted.Task.WaitAsync(TimeSpan.FromSeconds(10), token).GetAwaiter().GetResult();
            };
            var raced = await Restore().WaitAsync(TimeSpan.FromSeconds(20), token);
            Check(raced.Status == HistoryRestoreStatus.BlockedBeforeMutation && File.ReadAllText(liveFile) == "lock-inversion", "Racing restore wrote or waited for the channel.");
            Check(competitor is not null && (await competitor.WaitAsync(TimeSpan.FromSeconds(20), token)).CreatedNewArchive, "Competing backup did not resume after configuration release.");
            Check(transport.Events.Count(e => e == "handshake:backup") == 1 && transport.Events.Count(e => e == "pre_hot_backup") == 1,
                "Internal snapshot started a second backup session.");
            occupied!.Dispose(); occupied = null; Observation = null;
            CheckIdle(); checks.Add("real-host-lock-inversion");

            await File.WriteAllTextAsync(liveFile, "online-restore-current", token);
            occupied = Hold(); transport.Events.Clear();
            transport.OnEvent = name => { if (name == "pre_hot_restore") { occupied!.Dispose(); occupied = null; } };
            var borrowed = false;
            Observation = phase =>
            {
                if (phase != "restore-safety-source-read") return;
                borrowed = true;
                Check(KnotLinkCoordinationChannel.Owner is { } owner && KnotLinkCoordinationChannel.Gate.IsOwnedBy(owner), "Safety snapshot lost the outer channel.");
                Check(!PluginV3BackupRunOwner.HasActiveRuns && NativeRestoreSafetyCaptureContext.Current is not null, "Safety snapshot created its own run.");
            };
            var online = await Restore();
            Check(online.Succeeded && borrowed && File.ReadAllText(liveFile) == "baseline", "Online restore did not finish through borrowed coordination.");
            Check(!transport.Events.Any(e => e is "handshake:backup" or "pre_hot_backup" or "backup_success" or "backup_failed"), "Internal backup terminal escaped into the restore.");
            Check(transport.Events.Count(e => e == "handshake:restore") == 1 && transport.Events.Count(e => e == "hot_restore_complete") == 1, "Restore protocol did not complete once.");
            Observation = null; transport.OnEvent = null;
            CheckIdle(); checks.Add("online-restore-borrowed-safety-snapshot");

            // A recoverable provider failure must traverse the real Native -> Core Host fallback without re-freezing.
            config.ArtifactTransformPolicy = new()
            {
                Transformer = new() { PluginId = "com.folderrewind.minerewind", TransformerId = "minecraft-world-full" },
                FailureBehavior = PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings, ExperimentalEnabled = true
            };
            config.ArtifactTransformPolicy.Parameters["representation"] = JsonSerializer.SerializeToElement("incremental-v1");
            var transformed = false;
            Observation = phase =>
            {
                if (phase != "native-direct-capture") return;
                transformed = true;
                throw new Plugin.Runtime.Artifacts.ArtifactCaptureTransformException("Injected recoverable transformer failure.");
            };
            config.ConsistencyIntent = PersistedConsistencyIntent.Require;
            transport.Events.Clear(); occupied = Hold();
            var fallback = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual(), cancellationToken: token);
            Check(transformed && fallback.CreatedNewArchive && fallback.Outcome == OperationOutcome.SuccessWithWarnings
                && fallback.SourceOutcomes.Single().CaptureResult?.RepresentationCandidate?.Kind == RepresentationKind.CoreFull,
                "Recoverable Native failure did not produce an independent Core fallback.");
            Check(transport.Events.Count(e => e == "handshake:backup") == 1 && transport.Events.Count(e => e == "pre_hot_backup") == 1,
                "Native fallback repeated its freeze request.");
            occupied.Dispose(); occupied = null; Observation = null; config.ArtifactTransformPolicy = null;
            CheckIdle(); checks.Add("native-fallback-reuses-coordination");

            // The same guard must bind the registered Source identity, not just the ambient restore.
            var other = new ManagedFolder { Id = Guid.NewGuid().ToString(), Path = Directory.CreateDirectory(Path.Combine(root, "other")).FullName, DisplayName = "other" };
            config.SourceFolders.Add(other);
            var wrongSource = await new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, [folder], "wrong-safety-source", async ct =>
            {
                await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, ct);
                var capture = await BackupService.ExecuteBackupTransactionAsync(config, [other], BackupInvocationOptions.ForSafetySnapshot(),
                    HistoryCommitIntent.IndependentRecoveryPoint, new(SafetySnapshotReason.BeforeRestore), ct, operation);
                return new(capture.Outcome == OperationOutcome.Blocked ? HistoryRestoreStatus.BlockedBeforeMutation : HistoryRestoreStatus.NoChanges,
                    capture.Diagnostic ?? "", false, []);
            }, token);
            Check(wrongSource.Status == HistoryRestoreStatus.BlockedBeforeMutation, "Unregistered safety Source bypassed validation.");
            config.SourceFolders.Remove(other); checks.Add("safety-source-identity");
            await RunObservationBudgetHostAsync(root, checks, token);
            await File.WriteAllTextAsync(Path.Combine(root, "coordination-review-result.json"), JsonSerializer.Serialize(new { Success = true, Checks = checks }), token);
            return 0;
        }
        finally { Observation = null; transport.OnEvent = null; occupied?.Dispose(); }

        FileStream Hold() => File.Open(Path.Combine(folder.Path, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<HistoryRestoreResult> Restore() => NativeHistoryApplicationService.RestoreVersionAsync(config, folder, version, BackupService.RestoreMode.Clean, token);
        void CheckIdle()
        {
            Check(!PluginV3BackupRunOwner.HasActiveRuns && !KnotLinkCoordinationChannel.Gate.IsBusy
                && !KnotLinkCoordinationChannel.Gate.NeedsReset && !KnotLinkCoordinationChannel.HasActiveInteractions,
                "Coordination ownership or uncertainty leaked.");
            Check(BackupService.ActiveTasks.All(t => t.IsCompleted), "A blocked safety capture left an unfinished task.");
        }
    }

    private static async Task RunObservationBudgetHostAsync(string root, List<string> checks, CancellationToken token)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "budget-source")).FullName;
        var included = Directory.CreateDirectory(Path.Combine(directory, "Included")).FullName;
        await File.WriteAllTextAsync(Path.Combine(included, "level.dat"), "marker", token);
        await File.WriteAllTextAsync(Path.Combine(included, "payload.txt"), "data", token);
        var excluded = Directory.CreateDirectory(Path.Combine(directory, "ZExcluded")).FullName;
        for (var i = 0; i < 18; i++) excluded = Directory.CreateDirectory(Path.Combine(excluded, "d")).FullName;
        var config = new BackupConfig { Name = "Scoped budget acceptance", DestinationPath = Path.Combine(root, "budget-backup"),
            Kind = new() { OwnerId = "com.folderrewind.minerewind", KindId = "minecraft-saves" }, RequiredPluginId = "com.folderrewind.minerewind" };
        config.Archive.Mode = BackupMode.Full; config.Archive.SkipIfUnchanged = false; config.Archive.KeepCount = 0;
        var folder = new ManagedFolder { Id = Guid.NewGuid().ToString(), Path = directory, DisplayName = "scoped" };
        folder.SourceScope = new() { Mode = BackupSourceScopeMode.Include, IncludePatterns = new(["Included/payload.txt"]) };
        config.SourceFolders.Add(folder); ConfigService.CurrentConfig.BackupConfigs.Add(config);
        using var held = File.Open(Path.Combine(included, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        config.ConsistencyIntent = PersistedConsistencyIntent.Require;
        var required = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual(), cancellationToken: token);
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token);
        Check(!required.CreatedNewArchive && (await history.Query.GetAllVersionsAsync(token)).Count == 0, "Incomplete Require capture published data.");
        config.ConsistencyIntent = PersistedConsistencyIntent.Prefer;
        var preferred = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual(), cancellationToken: token);
        Check(preferred.CreatedNewArchive && preferred.Outcome == OperationOutcome.SuccessWithWarnings
            && preferred.CommittedBatch!.Run.Diagnostics.Any(d => d.Code == "minerewind.world_identity_unresolved"), "Incomplete Prefer capture lost its durable warning.");
        checks.Add("scoped-budget-require-and-prefer");
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new IOException(reason); }
}
#endif
