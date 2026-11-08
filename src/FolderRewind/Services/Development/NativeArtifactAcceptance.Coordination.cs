#if MINECRAFT_COORDINATION_UI_ACCEPTANCE
using FolderRewind.History.Domain;
using FolderRewind.History.Application;
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
    private static CoordinationAcceptanceServices? _coordinationServices;
    internal static bool HasCoordinationTransport => _coordinationServices?.SimulateHostTransport == true;
    internal static bool? SubmitCoordinationEvent(string data, CancellationToken token)
    {
        if (!HasCoordinationTransport) return null;
        token.ThrowIfCancellationRequested();
        _coordinationServices!.Events.Enqueue(KnotLinkKeyValueCodec.Parse(data).Values.GetValueOrDefault("event") ?? "unknown");
        return true;
    }
    private static async Task<int> RunCoordinationDeadlineAsync(BackupConfig config, string root, string intent)
    {
        if (intent is not ("Prefer" or "Require")) throw new InvalidDataException("Unknown deadline fixture intent.");
        config.ArtifactTransformPolicy = null; config.Archive.Mode = BackupMode.Full; config.Archive.SkipIfUnchanged = false;
        config.ConsistencyIntent = intent == "Require" ? PersistedConsistencyIntent.Require : PersistedConsistencyIntent.Prefer;
        var folder = config.SourceFolders.Single(); Directory.CreateDirectory(Path.Combine(folder.Path, "region"));
        var clock = new DeadlineAcceptanceClock();
        _coordinationServices!.Enabled = true; _coordinationServices.ConfigureClock(clock);
        _coordinationServices.BeforeSaveReceipt = () => clock.Advance(TimeSpan.FromSeconds(151));
        using var occupied = File.Open(Path.Combine(folder.Path, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual());
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var count = (await history.Query.GetAllVersionsAsync()).Count;
        if (intent == "Require")
        {
            if (result.CreatedNewArchive || count != 0 || result.Outcome is not (OperationOutcome.Failed or OperationOutcome.Blocked))
                throw new IOException("Expired Require capture published an invalid result.");
            if (!result.SourceOutcomes.Any(o => o.ErrorDiagnostics.Any(d => d.Code == "minerewind.coordination_window_expired")))
                throw new IOException("Expired Require capture lost its structured error.");
        }
        else if (!result.CreatedNewArchive || count != 1 || result.Outcome != OperationOutcome.SuccessWithWarnings
            || !result.CommittedBatch!.Run.Diagnostics.Any(d => d.Code == "minerewind.coordination_window_expired"))
            throw new IOException("Expired Prefer capture did not persist its degraded result.");
        if (PluginV3BackupRunOwner.HasActiveRuns || KnotLinkCoordinationChannel.Gate.IsBusy)
            throw new IOException("Deadline fixture leaked a coordination owner.");
        await File.WriteAllTextAsync(Path.Combine(root, "coordination-deadline-result.json"), JsonSerializer.Serialize(new { Success = true, Intent = intent, Versions = count, result.CreatedNewArchive, result.Outcome }));
        return 0;
    }

    private static async Task<int> RunCoordinationLifecycleAsync(BackupConfig config, string root)
    {
        config.ArtifactTransformPolicy = null;
        config.Archive.Mode = BackupMode.Full; config.Archive.SkipIfUnchanged = true;
        var folder = config.SourceFolders.Single();
        Directory.CreateDirectory(Path.Combine(folder.Path, "region"));
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        var checks = new List<string>();
        using (var held = File.Open(Path.Combine(folder.Path, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var entry in new[] { BackupInvocationSource.Manual, BackupInvocationSource.Automatic, BackupInvocationSource.PluginHotkey, BackupInvocationSource.Remote })
            {
                await File.WriteAllTextAsync(Path.Combine(folder.Path, "phase1-probe.bin"), entry.ToString());
                var options = new BackupInvocationOptions { Source = entry };
                var result = await BackupService.ExecuteBackupTransactionAsync(config, [folder], options);
                Require(result.CreatedNewArchive && result.Outcome == OperationOutcome.SuccessWithWarnings, entry + " degradation");
                Require(result.CommittedBatch!.Run.Diagnostics.Any(d => d.Code == "minerewind.consistency_raw_source"), entry + " durable run warning");
                var unchanged = await BackupService.ExecuteBackupTransactionAsync(config, [folder], options);
                Require(!unchanged.CreatedNewArchive && unchanged.Outcome == OperationOutcome.SuccessWithWarnings, entry + " no-change warning");
                Require(unchanged.CommittedBatch!.Run.Diagnostics.Any(d => d.Code == "minerewind.consistency_raw_source"), entry + " no-change run warning");
                Require((await PluginV3CoordinationRecoveryService.GetStatusAsync(default))?.State == BackupCoordinationState.Ready, entry + " closed session");
                checks.Add(entry.ToString());
            }
            var before = (await history.Query.GetAllVersionsAsync()).Count;
            config.ConsistencyIntent = PersistedConsistencyIntent.Require;
            var required = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual());
            Require(!required.CreatedNewArchive && required.Outcome is OperationOutcome.Failed or OperationOutcome.Blocked, "Require rejects uncoordinated world");
            Require((await history.Query.GetAllVersionsAsync()).Count == before, "Require cannot publish a Version");
            checks.Add("Require");
            config.ConsistencyIntent = PersistedConsistencyIntent.Prefer;
            await File.WriteAllTextAsync(Path.Combine(folder.Path, "phase1-probe.bin"), "exclusively locked data");
            using (var data = File.Open(Path.Combine(folder.Path, "phase1-probe.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual());
                Require(!failed.CreatedNewArchive && failed.Outcome is OperationOutcome.Failed or OperationOutcome.Blocked, "Unreadable data cannot be skipped");
            }
            Require((await history.Query.GetAllVersionsAsync()).Count == before, "Read failure cannot publish a Version");
            checks.Add("exclusive-data-read");
            File.Move(Path.Combine(folder.Path, "level.dat"), Path.Combine(folder.Path, "level.dat_old"));
            foreach (var kind in new[] { WorkspaceOperationKind.Restore, WorkspaceOperationKind.Checkout, WorkspaceOperationKind.Merge })
            {
                var writes = 0;
                var blocked = await new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, [folder], "fixture", _ =>
                {
                    writes++; return Task.FromResult(new HistoryRestoreResult(HistoryRestoreStatus.NoChanges, "fixture", false, []));
                }, default, kind);
                Require(!blocked.Succeeded && writes == 0, kind + " cannot bypass missing metadata and occupied lock");
            }
            checks.Add("restore-checkout-merge");
        }
        var ordinaryPath = Directory.CreateDirectory(Path.Combine(root, "ordinary")).FullName;
        await File.WriteAllTextAsync(Path.Combine(ordinaryPath, "session.lock"), "ordinary business data");
        var ordinary = new ManagedFolder { Id = Guid.NewGuid().ToString(), Path = ordinaryPath, DisplayName = "ordinary" };
        config.SourceFolders.Add(ordinary); config.ConsistencyIntent = PersistedConsistencyIntent.Require;
        var mixed = await BackupService.ExecuteBackupTransactionAsync(config, [folder, ordinary], BackupInvocationOptions.ForManual());
        Require(mixed.CreatedNewArchive && mixed.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings, "Mixed offline and ordinary Require");
        checks.Add("mixed-offline-require");
        var versions = (await history.Query.GetAllVersionsAsync()).Count;
        try
        {
            await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual(), cancellationToken: new(true));
            throw new IOException("Cancelled capture was accepted.");
        }
        catch (OperationCanceledException) { }
        Require((await history.Query.GetAllVersionsAsync()).Count == versions, "Cancellation cannot publish");
        Require(!KnotLinkCoordinationChannel.Gate.NeedsReset, "Offline/no-link work does not manufacture uncertain protocol state");
        checks.Add("cancellation");
        var safetyChecks = 0; var unsafeWrites = 0;
        var relocked = await new NativeHistoryRestoreOrchestrator().ExecuteAsync(config, [folder], "relocked-after-admission", async token =>
        {
            await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(new(config.Id), token);
            using var occupied = File.Open(Path.Combine(folder.Path, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try { await NativeHistoryRestoreOrchestrator.RevalidateMutationSafetyAsync(token); }
            catch (IOException error) { safetyChecks++; return new(HistoryRestoreStatus.BlockedBeforeMutation, error.Message, false, []); }
            unsafeWrites++; return new(HistoryRestoreStatus.NoChanges, "fixture", false, []);
        }, default);
        Require(!relocked.Succeeded && safetyChecks == 1 && unsafeWrites == 0, "Relocking after configuration admission blocks destination writes");
        checks.Add("post-admission-world-relock");
        var empty = new ManagedFolder { Id = Guid.NewGuid().ToString(), Path = Path.Combine(root, "missing"), DisplayName = "missing" };
        config.SourceFolders.Add(empty);
        var unavailable = await BackupService.ExecuteBackupTransactionAsync(config, [empty], BackupInvocationOptions.ForManual());
        Require(!unavailable.CreatedNewArchive && unavailable.Outcome is OperationOutcome.Failed or OperationOutcome.Blocked, "Unavailable is not ordinary success");
        checks.Add("unavailable");
        config.ConsistencyIntent = PersistedConsistencyIntent.Prefer;
        _coordinationServices!.Enabled = true;
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "phase1-probe.bin"), "terminal failure after commit");
        using (var held = File.Open(Path.Combine(folder.Path, "session.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var completed = await BackupService.ExecuteBackupTransactionAsync(config, [folder], BackupInvocationOptions.ForManual());
            Require(completed.CreatedNewArchive && !completed.HistoryRecoveryRequired, "Terminal failure cannot change committed data");
            Require(completed.Outcome == OperationOutcome.SuccessWithWarnings
                && completed.CompletionDiagnostics.Any(d => d.Code == "backup.coordination_cleanup_failed"), "Terminal failure has durable Run-linked completion diagnostics");
            Require((await PluginV3CoordinationRecoveryService.GetStatusAsync(default))?.State == BackupCoordinationState.NeedsReset, "Failed terminal isolates the next run");
        }
        Require(!PluginV3BackupRunOwner.HasActiveRuns && !KnotLinkCoordinationChannel.Gate.IsBusy, "Run ownership closes after failed dispatch");
        checks.Add("post-commit-terminal-failure");
        await File.WriteAllTextAsync(Path.Combine(root, "coordination-lifecycle-result.json"), JsonSerializer.Serialize(new { Success = true, Checks = checks }));
        return 0;
        static void Require(bool condition, string reason) { if (!condition) throw new IOException(reason); }
    }

    private sealed class CoordinationAcceptanceServices(IPluginHostServices inner, IFolderRewindPlugin plugin, PluginId pluginId) : IPluginHostServices, IKnotLinkHostService
    {
        internal bool Enabled;
        internal bool SimulateHostTransport;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();
        internal Action<string>? OnEvent;
        internal Action? BeforeSaveReceipt;
        internal void ConfigureClock(TimeProvider clock) => plugin.GetType().GetProperty("CoordinationClock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(plugin, clock);
        public IReadOnlyConfigQueryService Configs => inner.Configs;
        public IBackupRequestService Backups => inner.Backups;
        public IRestoreRequestService Restores => inner.Restores;
        public IHistoryQueryService History => inner.History;
        public IPluginNotificationService Notifications => inner.Notifications;
        public IKnotLinkHostService KnotLink => this;
        public IPluginDataStore DataStore => inner.DataStore;
        public IPluginTemporaryStorage TemporaryStorage => inner.TemporaryStorage;
        public IPluginLogger Logger => inner.Logger;
        public bool IsAvailable => Enabled;
        public async ValueTask SendAsync(string name, IReadOnlyDictionary<string, string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Enabled) throw new IOException("Acceptance transport unavailable.");
            Events.Enqueue(name == "handshake" ? name + ":" + arguments.GetValueOrDefault("action") : name);
            OnEvent?.Invoke(name);
            var callback = (IKnotLinkIntegrationCapability)plugin;
            if (name == "handshake") await callback.ExecuteAsync("HANDSHAKE_RESPONSE", new Dictionary<string, string> { ["mod_version"] = "3.1.0" }, new(pluginId, this, token, default));
            if (name == "pre_hot_backup")
            {
                BeforeSaveReceipt?.Invoke();
                await callback.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), new(pluginId, this, token, default));
            }
            if (SimulateHostTransport && name == "pre_hot_restore")
                await callback.ExecuteAsync("WORLD_SAVE_AND_EXIT_COMPLETE", new Dictionary<string, string>(), new(pluginId, this, token, default));
            if (SimulateHostTransport && name == "rejoin_world")
                await callback.ExecuteAsync("REJOIN_RESULT", new Dictionary<string, string> { ["result"] = "success" }, new(pluginId, this, token, default));
        }
    }

    private sealed class DeadlineAcceptanceClock : TimeProvider
    {
        private TimeSpan _elapsed;
        private Timer? _timer;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new(callback, state, _elapsed + dueTime);
        internal void Advance(TimeSpan duration)
        {
            _elapsed += duration;
            if (_timer is { Disposed: false } timer && timer.At <= _elapsed) { timer.Disposed = true; timer.Callback(timer.State); }
        }
        private sealed class Timer(TimerCallback callback, object? state, TimeSpan at) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal readonly TimeSpan At = at;
            internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
#endif
