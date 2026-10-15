using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
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
    [STAThread]
    private static void Main(string[] args)
    {
        Root = Path.GetFullPath(args[0]);
        if (Directory.Exists(Root)) throw new InvalidOperationException("Benchmark requires a new isolated state directory.");
        MiB = args.Length > 1 ? int.Parse(args[1]) : 8;
        Policy = args.Length > 2 ? args[2] : "Balanced";
        Quick = args.Length > 3 && args[3] == "quick";
        if (MiB is < 4 or > 10240 || Policy is not ("Balanced" or "Strict")) throw new ArgumentException("Invalid benchmark parameters.");
        Environment.SetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT", Root);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters => { SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())); _ = new BenchApplication(); });
    }
}
    public partial class BenchApplication : Application
    {
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
                var source = Path.Combine(Root, "source"); Directory.CreateDirectory(source);
                var block = new byte[1024 * 1024]; var random = new Random(719);
                for (int i = 0; i < MiB; i++) { if (i % 2 == 0) random.NextBytes(block); else Array.Clear(block); await File.WriteAllBytesAsync(Path.Combine(source, $"data-{i:D5}.bin"), block); }
                for (int i = 0; i < 1000; i++) await File.WriteAllTextAsync(Path.Combine(source, $"small-{i:D5}.txt"), $"deterministic synthetic file {i}");
                var results = new List<object>();
                var quickResults = new List<object>();
                foreach (var mode in new[] { BackupMode.Full, BackupMode.Smart })
                for (int repeat = 0; repeat < 3; repeat++)
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
                        var clock = Stopwatch.StartNew();
                        var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                        clock.Stop();
                        if (!result.IsSuccessful || !result.CreatedNewArchive) throw new InvalidOperationException("Host backup did not commit an archive: " + result.Outcome);
                        var diagnostic = HistoryOperationDiagnostics.GetRecent().LastOrDefault(d => d.Operation == "backup");
                        results.Add(new { mode = mode.ToString(), repeat, layer, policy = policyProperty is null ? "BaselineStrict" : Policy,
                            elapsedMs = clock.Elapsed.TotalMilliseconds, result.Outcome, diagnostic, cache = "uncontrolled", installation = "headless-msi-service-host" });
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
                            var began = DateTimeOffset.UtcNow;
                            var scope = HistoryOperationDiagnostics.Begin("host-quick"); var clock = Stopwatch.StartNew();
                            HistoryRestoreResult restored;
                            using (scope)
                            {
                                var callArguments = method.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
                                callArguments[0] = config; callArguments[1] = folder; callArguments[2] = CancellationToken.None;
                                var task = (Task)method.Invoke(null, callArguments)!; await task;
                                restored = (HistoryRestoreResult)task.GetType().GetProperty("Result")!.GetValue(task)!;
                            }
                            clock.Stop();
                            if (restored.Status != (changed ? HistoryRestoreStatus.Committed : HistoryRestoreStatus.NoChanges))
                                throw new InvalidOperationException("Unexpected Quick result: " + restored.Status + ": " + restored.Diagnostic);
                            var diagnostics = HistoryOperationDiagnostics.GetRecent().Where(d => d.StartedAtUtc >= began).ToArray();
                            quickResults.Add(new { repeat, ordinary, requestedQuick = quick, quickPolicySupported = quickPolicy is not null, changed,
                                elapsedMs = clock.Elapsed.TotalMilliseconds, restored.Status, diagnostics, cache = "uncontrolled", installation = "headless-msi-host" });
                        }
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(Root, "host-backup.json"), JsonSerializer.Serialize(new { MiB, results }, new JsonSerializerOptions { WriteIndented = true }));
                if (Quick) await File.WriteAllTextAsync(Path.Combine(Root, "host-quick.json"), JsonSerializer.Serialize(new { MiB, results = quickResults }, new JsonSerializerOptions { WriteIndented = true }));
                Environment.Exit(0);
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(Root);
                await File.WriteAllTextAsync(Path.Combine(Root, "failure.txt"), error.ToString());
                Environment.Exit(1);
            }
        }
    }
