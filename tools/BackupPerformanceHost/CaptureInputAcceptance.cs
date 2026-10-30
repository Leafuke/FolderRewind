using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

// Real service/7-Zip acceptance with synthetic data, no windows or UI operations.
internal static partial class CaptureInputAcceptance
{
    private static readonly List<object> Results = [];
    internal static async Task RunAsync()
    {
        var plugin = new PluginId("com.folderrewind.minerewind");
        var global = ConfigService.CurrentConfig.GlobalSettings;
        global.EnableKnotLink = false; global.AutoStartKnotLinkServer = false;
        global.EnableNotices = false; global.EnableUpdateReminder = false; global.FileSizeWarningThresholdKB = 0;
        global.HasShownFirstLaunchGuide = true; global.HasTriggeredInitialCoreValidation = true;
        global.Plugins.AutoCheckUpdates = false;
        global.Plugins.TypedSettings[plugin.Value] = new() { ["AutoDiscoverSaves"] = JsonSerializer.SerializeToElement(false), ["PreservePlayerData"] = JsonSerializer.SerializeToElement(false) };
        global.Plugins.EnabledIntent[plugin.Value] = true;
        var package = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_PLUGIN") ?? throw new IOException("Exact plugin required.");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        await new PluginPackageInstaller(PluginV3PackageService.PluginsRoot).InstallAsync(package, PluginInstallProvenance.Manual, hash);
        await PluginV3PackageService.InitializeAsync();
        if (!PluginV3RuntimeService.IsActive(plugin)) Require((await PluginV3PackageService.SetEnabledAsync(plugin, true)).Success, "Plugin activation failed.");
        foreach (var copy in new[] { false, true })
        foreach (var scenario in new[] { "full", "smart", "rolling", "native-full", "native-smart" })
            await VerifyAsync(scenario, copy);
        await VerifyFaultsAsync();
        VerifyConfigPersistence();
        await LogService.FlushAsync();
        await File.WriteAllTextAsync(Path.Combine(Root, "capture-input-acceptance.json"),
            JsonSerializer.Serialize(new { success = true, pluginSha256 = hash, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyAsync(string scenario, bool copy)
    {
        bool native = scenario.StartsWith("native-", StringComparison.Ordinal);
        var mode = scenario.EndsWith("smart", StringComparison.Ordinal) ? BackupMode.Smart
            : scenario == "rolling" ? BackupMode.Rolling : BackupMode.Full;
        var root = Path.Combine(Root, scenario + "-" + copy);
        var source = Path.Combine(root, "world"); Directory.CreateDirectory(source);
        WriteLevelDat(Path.Combine(source, "level.dat"));
        var changed = Path.Combine(source, native ? "entities/changed.bin" : "changed.bin"); var unchanged = Path.Combine(source, "unchanged.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
        File.WriteAllBytes(changed, new byte[8192]); File.WriteAllBytes(unchanged, new byte[65536]);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "excluded.cache"), "must never be copied");
        var config = new BackupConfig { Name = scenario, DestinationPath = Path.Combine(root, "backups"),
            Kind = new() { OwnerId = "com.folderrewind.minerewind", KindId = "minecraft-saves" }, RequiredPluginId = "com.folderrewind.minerewind" };
        var folder = new ManagedFolder { Path = source, DisplayName = "synthetic" }; config.SourceFolders.Add(folder);
        config.Filters.Blacklist.Add(FolderRewind.Plugin.Abstractions.PathFilterRule.Legacy("*.cache"));
        config.Archive.Mode = mode; config.Archive.CopySourceBeforeArchive = copy;
        config.Archive.MaxSmartBackupsPerFull = 0; config.Archive.CompressionLevel = 1;
        config.Archive.BackupBeforeRestore = false; config.Archive.QuickRestoreSafetySnapshotEnabled = false;
        if (!native)
        {
            config.Archive.FileTypeHandlingEnabled = true;
            config.Archive.FileTypeRules.Add(new() { Pattern = "*.bin", CompressionLevel = 0 });
        }
        if (scenario == "smart")
        {
            folder.SourceScope.Mode = BackupSourceScopeMode.Include;
            foreach (var path in new[] { "level.dat", "changed.bin", "unchanged.bin" }) folder.SourceScope.IncludePatterns.Add(path);
            File.WriteAllText(Path.Combine(source, "outside-scope.bin"), "never archived or copied");
        }
        if (native)
        {
            config.ArtifactTransformPolicy = new() { Transformer = new() { PluginId = "com.folderrewind.minerewind", TransformerId = "minecraft-world-full" },
                FailureBehavior = PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings, ExperimentalEnabled = true };
            config.ArtifactTransformPolicy.Parameters["representation"] = JsonSerializer.SerializeToElement("incremental-v1");
        }
        ConfigService.CurrentConfig.BackupConfigs.Add(config);
        Require((await ConfigService.SaveAsync()).Success, "Config save failed.");
        var total = new FileInfo(changed).Length + new FileInfo(unchanged).Length + new FileInfo(Path.Combine(source, "level.dat")).Length;
        await Capture("initial", copy ? total : 0, true);
        var history = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var sourceId = new SourceId(Guid.Parse(folder.Id));
        var original = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(sourceId).BaseVersionId!.Value;
        BranchUpdateId? originalTip = scenario == "native-smart"
            ? (await history.Branches.CreateFromCheckpointAsync((await history.Query.GetAllCheckpointsAsync()).Single().CheckpointId,
                "before-copy-revalidation", sourceId: sourceId)).BranchUpdate.UpdateId : null;
        if (scenario == "native-smart" && copy) await VerifyNativeProofAsync(config, history, original);
        await Capture("no-changes", 0, false);
        File.AppendAllText(changed, "delta");
        await Capture("changed", copy ? mode == BackupMode.Full ? total + 5 : new FileInfo(changed).Length : 0, true);
        File.Delete(unchanged);
        await Capture("deleted", copy && mode == BackupMode.Full ? new FileInfo(changed).Length + new FileInfo(Path.Combine(source, "level.dat")).Length : 0, true);
        if (native && mode == BackupMode.Smart)
        {
            Directory.CreateDirectory(Path.Combine(source, "new-empty"));
            await Capture("directory-only", 0, true);
            File.WriteAllText(Path.Combine(source, "shape"), "file");
            await Capture("structure-file", copy ? 4 : 0, true);
            File.Delete(Path.Combine(source, "shape"));
            Directory.CreateDirectory(Path.Combine(source, "shape"));
            File.WriteAllText(Path.Combine(source, "shape", "child"), "child");
            await Capture("file-to-directory", copy ? 5 : 0, true);
            Directory.Delete(Path.Combine(source, "shape"), recursive: true);
            File.WriteAllText(Path.Combine(source, "shape"), "file-again");
            await Capture("directory-to-file", copy ? 10 : 0, true);
            await Capture("checkpoint", 0, true, new BackupInvocationOptions { Source = BackupInvocationSource.Manual, RequestSemanticCheckpoint = true });
            if (!copy)
            {
                File.AppendAllText(changed, "locked");
                var before = (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision;
                using (var writer = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    var blocked = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                    Require(!blocked.IsSuccessful && !blocked.CreatedNewArchive, "Native write conflict unexpectedly succeeded or fell back.");
                    Require(before == (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision, "Blocked input changed history.");
                    var stats = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
                    Require(stats.Counters.GetValueOrDefault("native-independent-full-fallbacks") == 0, "Input failure triggered Full fallback.");
                }
                Results.Add(new { scenario, copy, phase = "native-write-conflict", correct = true });
            }
        }
        // A complete restore proves sparse captures did not interpret un-copied files as deletions.
        var restoreMethod = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService", true)!
            .GetMethod("RestoreVersionAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var restored = originalTip is { } tip
            ? await (Task<HistoryRestoreResult>)restoreMethod.DeclaringType!.GetMethod("CheckoutAsync", BindingFlags.Static | BindingFlags.Public)!
                .Invoke(null, [config, tip, CancellationToken.None])!
            : await (Task<HistoryRestoreResult>)restoreMethod.Invoke(null,
                [config, folder, original, BackupService.RestoreMode.Clean, CancellationToken.None, null, null])!;
        Require(restored.Succeeded && File.Exists(unchanged) && new FileInfo(changed).Length == 8192,
            "Original version restore failed: " + restored.Diagnostic);
        await (Task)typeof(HistoryRuntime).GetMethod("DrainRestoreCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history, [CancellationToken.None])!;
        Results.Add(new { scenario, copy, phase = "restore-original", correct = true });
        if (scenario == "native-smart") await VerifyPostCheckoutInputAsync(config, folder, history, changed, total, copy);

        async Task Capture(string phase, long expectedCopiedBytes, bool created, BackupInvocationOptions? options = null)
        {
            var result = await BackupService.BackupFolderAsync(config, folder, options ?? BackupInvocationOptions.ForInternal());
            var diagnostic = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
            Require(result.IsSuccessful && result.CreatedNewArchive == created,
                $"{scenario}/{copy}/{phase} failed: {result}; " + string.Join("; ", BackupService.ActiveTasks.Select(t => t.ErrorMessage)));
            Require(diagnostic.Counters.GetValueOrDefault("source-copy-bytes") == expectedCopiedBytes,
                $"{scenario}/{copy}/{phase} copied {diagnostic.Counters.GetValueOrDefault("source-copy-bytes")} instead of {expectedCopiedBytes}.");
            if (native && created)
            {
                var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
                var version = (await runtime.WorkspaceStore.LoadAsync()).Value!.GetSourceState(new SourceId(Guid.Parse(folder.Id))).BaseVersionId!.Value;
                Require((await runtime.Query.GetRepresentationsAsync(version)).Single().Kind == RepresentationKind.PluginArtifact, "Unexpected Native fallback.");
            }
            Results.Add(new { scenario, copy, phase, correct = true, diagnostic });
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }

    private static async Task VerifyPostCheckoutInputAsync(BackupConfig config, ManagedFolder folder, HistoryRuntime history,
        string changed, long originalBytes, bool copy)
    {
        var source = new SourceId(Guid.Parse(folder.Id));
        var restored = await history.CaptureBaselines.LoadAsync(source);
        Require(restored is not null && restored.Directories.Contains("entities") && restored.Directories.Contains("empty"),
            "Checkout produced an unreadable Native baseline or lost empty directories.");
        SourceDirectorySnapshot.Validate(restored!.Directories, restored.FileStates.Keys);
        using (var writer = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await Capture("post-checkout-no-changes", false, 0);
            restored = (await history.CaptureBaselines.LoadAsync(source))!;
            // Reproduce the previously persisted cache: nested files with a missing directory inventory.
            var representation = (await history.Query.GetRepresentationsAsync(restored.BaseVersionId))
                .Single(r => r.RepresentationId == restored.BaseRepresentationId);
            await history.CaptureBaselines.SaveAsync(source, new(restored.Revision, restored.PayloadPath,
                restored.ConsecutiveSmartCaptures, restored.FileStates, restored.BoundaryFingerprint)
                { ArtifactRootId = restored.ArtifactRootId, EvidenceIdentity = restored.EvidenceIdentity,
                    SourcePathFingerprint = restored.SourcePathFingerprint, ContentRealizationId = restored.ContentRealizationId },
                restored.BaseVersionId, representation);
            Require(await history.CaptureBaselines.LoadAsync(source) is null, "Legacy-invalid baseline fixture was accepted.");
            if (copy) await Capture("legacy-baseline-copy-revalidation", false, originalBytes, revalidated: true);
            else await Blocked("legacy-baseline-direct-write-conflict");
        }
        if (!copy) await Capture("legacy-baseline-direct-retry", false, 0, revalidated: true);
        Require(await history.CaptureBaselines.LoadAsync(source) is not null, "Successful revalidation did not repair the cache.");

        File.AppendAllText(changed, "post-checkout");
        using (var writer = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (copy) await Capture("post-checkout-copy-incremental", true, new FileInfo(changed).Length);
            else await Blocked("post-checkout-direct-write-conflict");
        }
        if (!copy) await Capture("post-checkout-direct-retry", true, 0);

        // A missing baseline needs full evidence, then a sparse capture must reuse that exact input once.
        var baseline = (await history.CaptureBaselines.LoadAsync(source))!;
        await history.CaptureBaselines.RemoveAsync(source, baseline.Revision);
        File.AppendAllText(changed, "rebuild");
        var bytes = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".cache", StringComparison.Ordinal)).Sum(p => new FileInfo(p).Length);
        using (var writer = new FileStream(changed, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (copy) await Capture("missing-baseline-copy-capture-reuses-view", true, bytes, revalidated: true);
            else await Blocked("missing-baseline-direct-write-conflict");
        }
        if (!copy) await Capture("missing-baseline-direct-capture-reuses-view", true, 0, revalidated: true);

        async Task Capture(string phase, bool created, long copied, bool revalidated = false)
        {
            var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
            var stats = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
            Require(result.IsSuccessful && result.CreatedNewArchive == created, phase + ": " + result);
            Require(stats.Counters.GetValueOrDefault("source-copy-bytes") == copied, phase + ": unexpected or duplicate source copy.");
            Require((stats.Counters.GetValueOrDefault("native-baseline-content-files") > 0) == revalidated, phase + ": wrong validation input.");
            Require(stats.Counters.GetValueOrDefault("native-independent-full-fallbacks") == 0, phase + ": unexpected Full fallback.");
            if (created)
            {
                var version = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).BaseVersionId!.Value;
                Require((await history.Query.GetRepresentationsAsync(version)).Single().Kind == RepresentationKind.PluginArtifact, phase + ": Native was lost.");
                if (revalidated) Require(stats.Counters.GetValueOrDefault("source-input-lease-reuses") == 1, phase + ": did not reuse validation input.");
            }
            Results.Add(new { scenario = "native-post-checkout", copy, phase, correct = true, diagnostic = stats });
        }
        async Task Blocked(string phase)
        {
            var revision = (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision;
            var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
            var stats = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
            Require(!result.IsSuccessful && !result.CreatedNewArchive
                && stats.Counters.GetValueOrDefault("source-copy-bytes") == 0
                && stats.Counters.GetValueOrDefault("native-independent-full-fallbacks") == 0
                && (await history.WorkspaceStore.LoadAsync()).Value!.StateRevision == revision, phase + ": protection failure committed or fell back.");
            Results.Add(new { scenario = "native-post-checkout", copy, phase, correct = true });
        }
    }

    private static void VerifyConfigPersistence()
    {
        var assembly = typeof(ConfigService).Assembly;
        var load = typeof(ConfigService).GetMethod("DeserializeConfig", BindingFlags.NonPublic | BindingFlags.Static)!;
        var saved = (AppConfig)load.Invoke(null, [File.ReadAllBytes(ConfigService.ConfigFilePath)])!;
        var mapper = assembly.GetType("FolderRewind.Services.ConfigSnapshotMapper", true)!.GetMethod("Copy", [typeof(BackupConfig)])!;
        var draftType = assembly.GetType("FolderRewind.Services.ConfigSettingsDraftService", true)!;
        var signature = assembly.GetType("FolderRewind.Services.NativeHistoryConfigLease", true)!.GetMethod("Signature", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
        {
            Require(saved.BackupConfigs.Single(c => c.Id == config.Id).Archive.CopySourceBeforeArchive == config.Archive.CopySourceBeforeArchive,
                "Copy setting was not persisted.");
            var clone = (BackupConfig)mapper.Invoke(null, [config])!;
            Require(clone.Archive.CopySourceBeforeArchive == config.Archive.CopySourceBeforeArchive, "Snapshot mapper lost copy setting.");
            var draftOwner = Activator.CreateInstance(draftType, config)!;
            var draft = (BackupConfig)draftType.GetProperty("Draft")!.GetValue(draftOwner)!;
            var before = signature.Invoke(null, [draft]);
            draft.Archive.CopySourceBeforeArchive = !config.Archive.CopySourceBeforeArchive;
            Require(!Equals(before, signature.Invoke(null, [draft])) && (bool)draftType.GetProperty("IsDirty")!.GetValue(draftOwner)!,
                "Draft/signature ignored copy setting.");
            Require(draft.Archive.CopySourceBeforeArchive != config.Archive.CopySourceBeforeArchive, "Draft mutated saved configuration.");
        }
        Results.Add(new { phase = "config-persistence-clone-draft-signature", correct = true });
    }

    private static void WriteLevelDat(string path)
    {
        using var output = new GZipStream(File.Create(path), CompressionLevel.Fastest);
        void Name(string value) { var bytes = Encoding.UTF8.GetBytes(value); Span<byte> size = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)bytes.Length); output.Write(size); output.Write(bytes); }
        output.WriteByte(10); Name(""); output.WriteByte(10); Name("Data"); output.WriteByte(8); Name("LevelName"); Name("synthetic"); output.WriteByte(0); output.WriteByte(0);
    }
}
