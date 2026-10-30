using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

// Uses the actual Host runtime and shipped plugin, never a substitute restore service.
internal static class RestoreCoordinationAcceptance
{
    private static readonly List<object> Results = [];
    private static readonly PluginId MineRewindId = new("com.folderrewind.minerewind");
    private static readonly Type Native = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService", true)!;

    internal static async Task RunAsync()
    {
        var global = ConfigService.CurrentConfig.GlobalSettings;
        global.EnableKnotLink = false; global.AutoStartKnotLinkServer = false;
        global.FileSizeWarningThresholdKB = 0; global.EnableNotices = false; global.EnableUpdateReminder = false;
        global.HasShownFirstLaunchGuide = true; global.HasTriggeredInitialCoreValidation = true;
        global.Plugins.TypedSettings[MineRewindId.Value] = new() { ["AutoDiscoverSaves"] = JsonSerializer.SerializeToElement(false), ["PreservePlayerData"] = JsonSerializer.SerializeToElement(false) };
        var enabled = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_PLUGIN_ENABLED") == "1";
        global.Plugins.EnabledIntent[MineRewindId.Value] = enabled;
        ConfigService.CurrentConfig.BackupConfigs.Clear();
        Require((await ConfigService.SaveAsync()).Success, "Initial isolated settings save failed.");
        var old = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_OLD_PLUGIN") ?? throw new InvalidOperationException("Old plugin package required.");
        var installer = new PluginPackageInstaller(PluginV3PackageService.PluginsRoot);
        var facts = PluginPackageInstallValidationFacts.Empty(MineRewindId) with { PersistedSettings = new(MineRewindId, global.Plugins.TypedSettings[MineRewindId.Value]) };
        await installer.InstallAsync(old, PluginInstallProvenance.OfficialCatalog, Hash(old), facts);
        await PluginV3PackageService.InitializeAsync();
        var upgraded = (await installer.ReadStateAsync(MineRewindId))!;
        Require(upgraded.CurrentVersion == "1.9.10" && upgraded.PreviousKnownGoodVersion == "1.9.9", "Bundled startup upgrade did not use the exact new package.");
        Require(global.Plugins.EnabledIntent[MineRewindId.Value] == enabled && PluginV3RuntimeService.IsActive(MineRewindId) == enabled, "Enabled intent was changed by upgrade.");
        Require(!global.Plugins.TypedSettings[MineRewindId.Value]["AutoDiscoverSaves"].GetBoolean()
            && !global.Plugins.TypedSettings[MineRewindId.Value]["PreservePlayerData"].GetBoolean(), "Plugin settings changed during upgrade.");
        Results.Add(new { scenario = "official-startup-upgrade", version = upgraded.CurrentVersion, previous = upgraded.PreviousKnownGoodVersion, enabled, settingsPreserved = true });
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_UPGRADE_ONLY") != "1")
        {
            Require((await PluginV3PackageService.SetEnabledAsync(MineRewindId, true)).Success, "New plugin activation failed.");
            await VerifyModeAsync(BackupMode.Full);
            await VerifyModeAsync(BackupMode.Smart);
            await VerifyCoordinatorMappingAsync();
        }
        await LogService.FlushAsync();
        await File.WriteAllTextAsync(Path.Combine(Root, "host-coordination.json"), JsonSerializer.Serialize(new { success = true, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyModeAsync(BackupMode mode)
    {
        var world = Path.Combine(Root, mode.ToString(), ".minecraft", "saves", "暑假档");
        Directory.CreateDirectory(world);
        WriteLevelDat(Path.Combine(world, "level.dat"));
        var payload = Path.Combine(world, "world.bin");
        var first = new byte[131072]; new Random(198).NextBytes(first); File.WriteAllBytes(payload, first);
        var config = new BackupConfig { Name = "协调回归 " + mode, DestinationPath = Path.Combine(Root, mode.ToString(), "backup"),
            Kind = new() { OwnerId = MineRewindId.Value, KindId = "minecraft-saves" }, RequiredPluginId = MineRewindId.Value };
        var folder = new ManagedFolder { Path = world, DisplayName = "暑假档 " + mode }; config.SourceFolders.Add(folder);
        config.Archive.Mode = mode; config.Archive.SkipIfUnchanged = false; config.Archive.KeepCount = 0; config.Archive.BackupBeforeRestore = false;
        ConfigService.CurrentConfig.BackupConfigs.Add(config); Require((await ConfigService.SaveAsync()).Success, "Configuration save failed.");
        var initial = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
        Require(initial.CreatedNewArchive && !initial.HasWarnings, "Initial backup unexpectedly warned: " + TaskDetails(BackupService.ActiveTasks[0]));
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var source = new SourceId(Guid.Parse(folder.Id));
        var v1 = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        var second = first.ToArray(); second[0] ^= 0xff; File.WriteAllBytes(payload, second); File.SetLastWriteTimeUtc(payload, DateTime.UtcNow.AddSeconds(2));
        Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).CreatedNewArchive, "Changed backup failed.");
        var v2 = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
        if (mode == BackupMode.Smart)
            Require((await history.Query.GetRepresentationsAsync(v2)).Any(item => item.Kind == RepresentationKind.CoreSmartDelta), "A real Smart representation was not created.");
        var lockPath = Path.Combine(world, "session.lock"); File.WriteAllBytes(lockPath, [0]); File.SetAttributes(lockPath, FileAttributes.ReadOnly);
        try
        {
            File.WriteAllText(payload, "changed current bytes");
            var cold = await RestoreAsync(config, folder, v1);
            Require(cold.Succeeded && File.ReadAllBytes(payload).AsSpan().SequenceEqual(first), "Readonly-lock Full restore failed: " + cold.Diagnostic);
            var delta = await RestoreAsync(config, folder, v2);
            Require(delta.Succeeded && File.ReadAllBytes(payload).AsSpan().SequenceEqual(second), "Readonly-lock selected restore failed: " + delta.Diagnostic);
            Require((File.GetAttributes(lockPath) & FileAttributes.ReadOnly) != 0, "Excluded readonly lock attributes changed.");
        }
        finally { File.SetAttributes(lockPath, FileAttributes.Normal); }

        using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var before = Hash(payload); var revision = (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var blocked = await RestoreAsync(config, folder, v1);
                Require(blocked.Status == HistoryRestoreStatus.BlockedBeforeMutation && before == Hash(payload)
                    && (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision == revision, "Blocked restore changed target/workspace.");
                Require(!blocked.Diagnostic.Contains("did not invoke", StringComparison.Ordinal), "Original coordination reason was lost.");
            }
            File.WriteAllText(Path.Combine(world, "next.txt"), "changed for a warning capture");
            var warning = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
            var task = BackupService.ActiveTasks[0];
            Require(warning.HasWarnings && task.IsSuccess && task.HasDetails && task.StatusSemantic == SemanticStatus.Warning, "Source warning was not presented.");
            Require(task.WarningDiagnostics.Any(item => item.Code == "minerewind.consistency_raw_source")
                && TaskDetails(task).Contains("minerewind.consistency_raw_source", StringComparison.Ordinal), "Warning detail identity was lost.");
        }
        File.Delete(lockPath); Directory.CreateDirectory(lockPath); // Isolated access-denied fault: the lock path is a directory.
        try
        {
            var before = Hash(payload);
            var blocked = await RestoreAsync(config, folder, v1);
            Require(blocked.Status == HistoryRestoreStatus.BlockedBeforeMutation && before == Hash(payload), "Unknown lock state did not block restore.");
            Require(LogService.GetEntriesSnapshot().Any(entry => entry.Message.Contains("minerewind.world_lock_probe_failed", StringComparison.Ordinal)
                && entry.Message.Contains(config.Id, StringComparison.Ordinal)), "Unknown-state details were not logged.");
        }
        finally { Directory.Delete(lockPath); }
        ConfigService.CurrentConfig.GlobalSettings.FileSizeWarningThresholdKB = 1024;
        File.WriteAllText(Path.Combine(world, "post-commit.txt"), "post-commit threshold warning");
        var postCommit = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
        Require(postCommit.HasWarnings && BackupService.ActiveTasks[0].WarningDiagnostics.Any(item => item.Code == "backup.file_too_small"), "Post-commit warning was not attached to its task.");
        ConfigService.CurrentConfig.GlobalSettings.FileSizeWarningThresholdKB = 0;
        var released = await RestoreAsync(config, folder, v2);
        Require(released.Succeeded, "Cold restore after releasing the lock failed: " + released.Diagnostic);
        await LogService.FlushAsync();
        using var logReader = new StreamReader(new FileStream(LogService.GetLogFilePath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var log = await logReader.ReadToEndAsync();
        Require(log.Contains("minerewind.consistency_raw_source", StringComparison.Ordinal)
            && log.Contains("minerewind.restore_knotlink_unavailable", StringComparison.Ordinal)
            && log.Contains("0x80070005", StringComparison.Ordinal), "Durable warning/error log evidence is missing.");
        Results.Add(new { scenario = mode.ToString(), readonlyColdRestore = true, actualSmartChain = mode == BackupMode.Smart,
            occupiedAttempts = 2, unknownStateBlocked = true, targetPreservedWhenBlocked = true, taskWarningAndPostCommitWarning = true, durableLogs = true });
        await NativeHistoryCoreGateway.DetachActiveConfigAsync(config.Id);
    }

    private static async Task VerifyCoordinatorMappingAsync()
    {
        var id = new PluginId("test.restore-coordinator"); var coordinator = new Coordinator(id);
        var config = new BackupConfig { Name = "mapping", Kind = new() { OwnerId = id.Value, KindId = "world" } };
        var folder = new ManagedFolder { Path = Path.Combine(Root, "mapping"), DisplayName = "mapping" }; config.SourceFolders.Add(folder);
        var activation = await PluginV3RuntimeService.Runtime.ActivateAsync(new(id, () => coordinator, new(id, new Dictionary<string, JsonElement>()), [], new UnusedHost(), new Store()));
        Require(activation.Success, "Mapping fixture activation failed.");
        var type = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryRestoreOrchestrator", true)!;
        var execute = type.GetMethod("ExecuteAsync")!;
        async Task<HistoryRestoreResult> Run(Func<CancellationToken, Task<HistoryRestoreResult>> mutation)
            => await (Task<HistoryRestoreResult>)execute.Invoke(Activator.CreateInstance(type, nonPublic: true), [config, new[] { folder }, "mapping-target", mutation, CancellationToken.None, WorkspaceOperationKind.Restore, null])!;
        var calls = 0;
        Task<HistoryRestoreResult> Mutation(CancellationToken _) { calls++; return Task.FromResult(new HistoryRestoreResult(HistoryRestoreStatus.Committed, "", true, [])); }
        foreach (var outcome in new[] { OperationOutcome.Blocked, OperationOutcome.Failed, OperationOutcome.Canceled })
        {
            coordinator.Callback = (_, _) => ValueTask.FromResult(new RestoreCoordinatorResult(outcome, [new("thirdparty.block_reason", DiagnosticSeverity.Error, "RestoreCoordinator", id.Value, new Dictionary<string, string> { ["message"] = "original reason" })]));
            var result = await Run(Mutation);
            Require(result.Status == HistoryRestoreStatus.BlockedBeforeMutation && result.Diagnostic.Contains("thirdparty.block_reason", StringComparison.Ordinal), "Pre-mutation outcome was misreported.");
        }
        coordinator.Callback = (_, _) => ValueTask.FromResult(new RestoreCoordinatorResult(OperationOutcome.Success, []));
        Require(!(await Run(Mutation)).Succeeded && calls == 0, "Missing continuation was accepted.");
        coordinator.Callback = async (request, context) => new(await request.ContinueMutationAsync(context.OperationCancellation), []);
        var failed = await Run(_ => throw new IOException("continuation exception evidence", unchecked((int)0x8007045d)));
        Require(failed.Diagnostic.Contains("continuation exception evidence", StringComparison.Ordinal), "Continuation exception was hidden.");
        foreach (var status in new[] { HistoryRestoreStatus.CommittedRecoveryRequired, HistoryRestoreStatus.MutationFailedRecoveryRequired })
        {
            coordinator.Callback = async (request, context) => { await request.ContinueMutationAsync(context.OperationCancellation); return new(OperationOutcome.Failed, []); };
            var result = await Run(_ => Task.FromResult(new HistoryRestoreResult(status, "recovery evidence", false, [])));
            Require(result.Status == status, "Coordinator replaced authoritative recovery state.");
        }
        await PluginV3RuntimeService.Runtime.DeactivateAsync(id);
        Results.Add(new { scenario = "actual-orchestrator-result-mapping", preMutationStops = 3, missingCallbackBlocked = true, callbackExceptionPreserved = true, recoveryStatusesPreserved = 2 });
    }

    private static async Task<HistoryRestoreResult> RestoreAsync(BackupConfig config, ManagedFolder folder, VersionId version)
        => await (Task<HistoryRestoreResult>)Native.GetMethod("RestoreVersionAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [config, folder, version, BackupService.RestoreMode.Clean, CancellationToken.None, null, null])!;
    private static string TaskDetails(BackupTask task) => (string)typeof(BackupService).Assembly.GetType("FolderRewind.Services.OperationDiagnosticFormatter", true)!
        .GetMethod("TaskDetails", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [task])!;
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WriteLevelDat(string path)
    {
        using var output = new GZipStream(File.Create(path), CompressionLevel.Fastest);
        void Name(string value) { var bytes = Encoding.UTF8.GetBytes(value); Span<byte> size = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)bytes.Length); output.Write(size); output.Write(bytes); }
        output.WriteByte(10); Name(""); output.WriteByte(10); Name("Data"); output.WriteByte(8); Name("LevelName"); Name("暑假档"); output.WriteByte(0); output.WriteByte(0);
    }
    private sealed class Coordinator(PluginId id) : IFolderRewindPlugin, IRestoreCoordinatorCapability
    {
        public ConfigKindRef Kind => new(new(id.Value), "world");
        public Func<RestoreCoordinatorRequest, PluginInvocationContext, ValueTask<RestoreCoordinatorResult>> Callback { get; set; } = null!;
        public ValueTask<PluginActivationResult> ActivateAsync(IPluginActivationContext context, CancellationToken token)
        { context.RegisterCapability<IRestoreCoordinatorCapability>(this); return ValueTask.FromResult(PluginActivationResult.Empty); }
        public ValueTask DeactivateAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<RestoreCoordinatorResult> CoordinateAsync(RestoreCoordinatorRequest request, PluginInvocationContext context) => Callback(request, context);
    }
    private sealed class Store : IPluginActivationStore { public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class UnusedHost : IPluginHostServices
    {
        public IReadOnlyConfigQueryService Configs => null!; public IBackupRequestService Backups => null!; public IRestoreRequestService Restores => null!;
        public IHistoryQueryService History => null!; public IPluginNotificationService Notifications => null!; public IKnotLinkHostService KnotLink => null!;
        public IPluginDataStore DataStore => null!; public IPluginTemporaryStorage TemporaryStorage => null!; public IPluginLogger Logger => null!;
    }
}
