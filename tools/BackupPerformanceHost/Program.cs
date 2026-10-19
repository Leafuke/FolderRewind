using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

// 技术服务基准：初始化 XAML 调度队列但不创建窗口，不作为 UI 验收。
internal static class Program
{
    internal static string Root = "";
    internal static int MiB;
    internal static string Policy = "Balanced";
    internal static bool Quick;
    internal static bool Acceptance;
    internal static int Repeats = 5;
    [STAThread]
    private static void Main(string[] args)
    {
        Root = Path.GetFullPath(args[0]);
        if (Directory.Exists(Root)) throw new InvalidOperationException("Benchmark requires a new isolated state directory.");
        MiB = args.Length > 1 ? int.Parse(args[1]) : 8;
        Policy = args.Length > 2 ? args[2] : "Balanced";
        Quick = args.Length > 3 && args[3] == "quick";
        Acceptance = args.Length > 3 && args[3] == "stage2";
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_BENCHMARK_REPEATS") is { } configured)
            Repeats = int.Parse(configured);
        if (Repeats is < 1 or > 20) throw new ArgumentException("Repeat count must be between 1 and 20.");
        if (MiB is < 4 or > 10240 || Policy is not ("Balanced" or "Strict")) throw new ArgumentException("Invalid benchmark parameters.");
        Environment.SetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT", Root);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters => { SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())); _ = new BenchApplication(); });
    }
}
    public partial class BenchApplication : Application
    {
        private static readonly Dictionary<string, string> ExpectedPayloadHashes = new(StringComparer.Ordinal);
        private static long _lastCompletedUtcSecond;
        // 基线按秒命名。双方都在计时外避开前次完成的秒，避免无效旧载荷污染性能对照。
        private static async Task WaitForArchiveNameWindowAsync()
        {
            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= _lastCompletedUtcSecond)
                await Task.Delay(25);
        }
        public BenchApplication() { InitializeComponent(); }
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                // 在初始化任何应用状态前确认 MSI 测试根覆盖生效，禁止回落用户 LocalAppData。
                if (!Path.GetFullPath(ConfigService.ConfigFilePath).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Host does not support isolated test state; no state was initialized.");
                UiDispatcherService.Initialize(DispatcherQueue.GetForCurrentThread());
                ConfigService.Initialize();
                var exe = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_7Z") ?? throw new InvalidOperationException("7-Zip path required.");
                ConfigService.CurrentConfig.GlobalSettings.SevenZipPath = exe;
                if (Acceptance)
                {
#if FOLDERREWIND_BENCHMARK_ONLY
                    throw new InvalidOperationException("The benchmark-only build cannot run stage-two service acceptance.");
#else
                    await StageTwoAcceptance.RunAsync();
                    Environment.Exit(0);
                    return;
#endif
                }
                var source = Path.Combine(Root, "source"); Directory.CreateDirectory(source);
                var block = new byte[1024 * 1024]; var random = new Random(719);
                for (int i = 0; i < MiB; i++) { if (i % 2 == 0) random.NextBytes(block); else Array.Clear(block); await File.WriteAllBytesAsync(Path.Combine(source, $"data-{i:D5}.bin"), block); }
                for (int i = 0; i < 1000; i++) await File.WriteAllTextAsync(Path.Combine(source, $"small-{i:D5}.txt"), $"deterministic synthetic file {i}");
                var results = new List<object>();
                var quickResults = new List<object>();
                bool allCorrect = true;
                foreach (var mode in new[] { BackupMode.Full, BackupMode.Smart })
                for (int repeat = 0; repeat < Repeats; repeat++)
                {
                    var config = new BackupConfig { Name = "performance", DestinationPath = Path.Combine(Root, mode + "-" + repeat) };
                    var folder = new ManagedFolder { Path = source, DisplayName = "synthetic" };
                    config.SourceFolders.Add(folder); config.Archive.Mode = mode; config.Archive.SkipIfUnchanged = false;
                    config.Archive.MaxSmartBackupsPerFull = 8;
                    var policyProperty = config.Archive.GetType().GetProperty("BackupVerificationLevel");
                    if (policyProperty is not null) policyProperty.SetValue(config.Archive, Enum.ToObject(policyProperty.PropertyType, Policy == "Strict" ? 1 : 0));
                    ConfigService.CurrentConfig.BackupConfigs.Add(config);
                    if (!(await ConfigService.SaveAsync()).Success) throw new InvalidOperationException("Isolated config save failed.");
                    for (int layer = 0; layer < (mode == BackupMode.Smart ? 6 : 1); layer++)
                    {
                        await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "layer " + layer);
                        await LogService.FlushAsync();
                        await WaitForArchiveNameWindowAsync();
                        long logBefore = LogLength();
                        var clock = Stopwatch.StartNew();
                        var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                        double fileReadyMs = clock.Elapsed.TotalMilliseconds;
                        await LogService.FlushAsync();
                        clock.Stop();
                        _lastCompletedUtcSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (!result.IsSuccessful || !result.CreatedNewArchive) throw new InvalidOperationException("Host backup did not commit an archive: " + result.Outcome);
                        var diagnostic = HistoryOperationDiagnostics.GetRecent().LastOrDefault(d => d.Operation == "backup");
                        bool payloadsIntact = await PayloadsIntactAsync(config);
                        allCorrect &= payloadsIntact;
                        results.Add(new { mode = mode.ToString(), repeat, layer, policy = policyProperty is null ? "BaselineStrict" : Policy,
                            elapsedMs = clock.Elapsed.TotalMilliseconds, fileReadyMs, totalMs = clock.Elapsed.TotalMilliseconds,
                            logDrainMs = clock.Elapsed.TotalMilliseconds - fileReadyMs, logBytes = LogLength() - logBefore,
                            correct = payloadsIntact, result.Outcome, diagnostic,
                            timing = BackupTimingService.GetRecent(config.Id).LastOrDefault(),
                            cache = "uncontrolled", installation = "headless-msi-service-host" });
                    }
                    if (Quick && mode == BackupMode.Full)
                    {
                        var restorePolicy = config.Archive.GetType().GetProperty("RestorePreflightMode");
                        if (restorePolicy is not null) restorePolicy.SetValue(config.Archive, Enum.ToObject(restorePolicy.PropertyType, Policy == "Strict" ? 1 : 0));
                        var quickPolicy = config.Archive.GetType().GetProperty("QuickRestoreSafetySnapshotEnabled");
                        var native = typeof(BackupService).Assembly.GetType("FolderRewind.Services.NativeHistoryApplicationService")!;
                        var method = native.GetMethod("QuickRestoreAsync", BindingFlags.Public | BindingFlags.Static)!;
                        foreach (var ordinary in new[] { false, true }) foreach (var quick in new[] { false, true }) foreach (var changed in new[] { false, true })
                        {
                            config.Archive.BackupBeforeRestore = ordinary;
                            quickPolicy?.SetValue(config.Archive, quick);
                            if (!(await ConfigService.SaveAsync()).Success) throw new InvalidOperationException("Quick preference save failed.");
                            await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), changed ? "local changed" : "layer 0");
                            await LogService.FlushAsync();
                            await WaitForArchiveNameWindowAsync();
                            long logBefore = LogLength();
                            var began = DateTimeOffset.UtcNow;
                            var scope = HistoryOperationDiagnostics.Begin("host-quick"); var clock = Stopwatch.StartNew();
                            HistoryRestoreResult restored;
                            double fileReadyMs;
                            double cleanupDrainMs, logDrainMs;
                            using (scope)
                            {
                                var callArguments = method.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
                                callArguments[0] = config; callArguments[1] = folder; callArguments[2] = CancellationToken.None;
                                try
                                {
                                    var task = (Task)method.Invoke(null, callArguments)!; await task;
                                    restored = (HistoryRestoreResult)task.GetType().GetProperty("Result")!.GetValue(task)!;
                                }
                                catch (Exception error) { restored = new(HistoryRestoreStatus.BlockedBeforeMutation, error.GetBaseException().Message, false, []); }
                                fileReadyMs = clock.Elapsed.TotalMilliseconds;
                                var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
                                var drain = runtime.GetType().GetMethod("DrainRestoreCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                                if (drain is not null) await (Task)drain.Invoke(runtime, [CancellationToken.None])!;
                                cleanupDrainMs = clock.Elapsed.TotalMilliseconds - fileReadyMs;
                                double logStartedMs = clock.Elapsed.TotalMilliseconds;
                                await LogService.FlushAsync();
                                logDrainMs = clock.Elapsed.TotalMilliseconds - logStartedMs;
                            }
                            clock.Stop();
                            _lastCompletedUtcSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            bool correct = restored.Status == (changed ? HistoryRestoreStatus.Committed : HistoryRestoreStatus.NoChanges)
                                && await File.ReadAllTextAsync(Path.Combine(source, "changed.txt")) == "layer 0" && await PayloadsIntactAsync(config);
                            allCorrect &= correct;
                            var diagnostics = HistoryOperationDiagnostics.GetRecent().Where(d => d.StartedAtUtc >= began).ToArray();
                            quickResults.Add(new { repeat, ordinary, requestedQuick = quick, quickPolicySupported = quickPolicy is not null, changed,
                                elapsedMs = clock.Elapsed.TotalMilliseconds, fileReadyMs, totalMs = clock.Elapsed.TotalMilliseconds,
                                cleanupDrainMs, logDrainMs, logBytes = LogLength() - logBefore, correct, restored.Status, restored.Diagnostic, diagnostics, cache = "uncontrolled", installation = "headless-msi-host" });
                        }
                    }
                }
                const string spacing = "next UTC second after previous completion; outside timing, identical for both cohorts";
                await File.WriteAllTextAsync(Path.Combine(Root, "host-backup.json"), JsonSerializer.Serialize(new { MiB, spacing, results }, new JsonSerializerOptions { WriteIndented = true }));
                if (Quick) await File.WriteAllTextAsync(Path.Combine(Root, "host-quick.json"), JsonSerializer.Serialize(new { MiB, spacing, results = quickResults }, new JsonSerializerOptions { WriteIndented = true }));
                Environment.Exit(allCorrect ? 0 : 2);
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(Root);
                await File.WriteAllTextAsync(Path.Combine(Root, "failure.txt"), error.ToString());
                await File.WriteAllTextAsync(Path.Combine(Root, "failure-diagnostics.json"), JsonSerializer.Serialize(HistoryOperationDiagnostics.GetRecent(), new JsonSerializerOptions { WriteIndented = true }));
                Environment.Exit(1);
            }
        }
        private static long LogLength() => Directory.Exists(LogService.GetLogDirectory())
            ? Directory.EnumerateFiles(LogService.GetLogDirectory()).Sum(path => new FileInfo(path).Length) : 0;
        private static async Task<bool> PayloadsIntactAsync(BackupConfig config)
        {
            var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
            var graph = (await runtime.Query.GetAllRepresentationsAsync()).ToDictionary(r => r.RepresentationId);
            var entries = (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value?.Entries;
            if (entries is null) return false;
            var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Value)
            {
                if (!graph.TryGetValue(entry.RepresentationId, out var representation)) return false;
                var path = entry.Locator.AbsolutePath!;
                if (!File.Exists(path)) return false;
                if (!observed.TryGetValue(path, out var digest))
                {
                    await using var file = File.OpenRead(path); digest = Convert.ToHexString(await SHA256.HashDataAsync(file)); observed.Add(path, digest);
                }
                var key = config.Id + "/" + entry.RepresentationId;
                if (ExpectedPayloadHashes.TryGetValue(key, out var prior) && prior != digest) return false;
                ExpectedPayloadHashes.TryAdd(key, digest);
                if (representation.RepresentationSpecificMetadata.TryGetValue("storageSha256", out var expected)
                    && !StringComparer.OrdinalIgnoreCase.Equals(digest, expected)) return false;
            }
            return true;
        }
    }
