using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Services;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

public partial class BenchApplication
{
    // Research-only driver: invokes the unmodified production BackupService with isolated synthetic data.
    private static async Task RunAnalysisProbeAsync(string scenario)
    {
        var source = Path.Combine(Root, "source");
        Directory.CreateDirectory(source);
        int fileCount = scenario.StartsWith("many-files-") ? int.Parse(scenario[11..]) : 1;
        for (int i = 0; i < fileCount; i++)
        {
            var directory = Path.Combine(source, $"d{i / 100:D4}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"small-{i:D6}.txt"), $"deterministic synthetic small file {i:D6}");
        }
        var changed = Path.Combine(source, "changed.txt");
        await File.WriteAllTextAsync(changed, "value-00000");
        var big = Path.Combine(source, "large.bin");
        if (scenario == "large-file")
        {
            var block = new byte[1024 * 1024]; var random = new Random(719);
            await using var output = File.Create(big);
            for (int i = 0; i < 64; i++)
            {
                if (i % 2 == 0) random.NextBytes(block); else Array.Clear(block);
                await output.WriteAsync(block);
            }
        }
        var rows = new List<object>();
        var stages = new ConcurrentDictionary<string, double>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "FolderRewind.Artifacts",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => stages.AddOrUpdate(a.OperationName, a.Duration.TotalMilliseconds, (_, old) => old + a.Duration.TotalMilliseconds)
        };
        ActivitySource.AddActivityListener(listener);
        foreach (string method in scenario == "large-file" ? new[] { "LZMA2", "zstd" } : new[] { "LZMA2" })
        {
            var config = new BackupConfig { Name = "analysis-" + method, DestinationPath = Path.Combine(Root, method) };
            var folder = new ManagedFolder { Path = source, DisplayName = "synthetic" };
            config.SourceFolders.Add(folder);
            config.Archive.Mode = BackupMode.Smart;
            config.Archive.SkipIfUnchanged = true;
            config.Archive.MaxSmartBackupsPerFull = 5;
            config.Archive.Method = method;
            config.Archive.CompressionLevel = method == "zstd" ? 1 : 5;
            ConfigService.CurrentConfig.BackupConfigs.Add(config);
            if (!(await ConfigService.SaveAsync()).Success) throw new IOException("Isolated configuration save failed.");

            async Task Sample(string kind, int iteration, bool expectedCreated)
            {
                await LogService.FlushAsync();
                stages.Clear();
                long allocatedBefore = GC.GetTotalAllocatedBytes(false);
                var clock = Stopwatch.StartNew();
                var result = await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
                var fileReadyMs = clock.Elapsed.TotalMilliseconds;
                await LogService.FlushAsync();
                clock.Stop();
                long allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
                var activityStages = stages.ToDictionary();
                var diagnostic = HistoryOperationDiagnostics.GetRecent().Last(d => d.Operation == "backup");
                var timing = BackupTimingService.GetRecent(config.Id).Last();
                bool intact = await PayloadsIntactAsync(config);
                bool correct = result.IsSuccessful && result.CreatedNewArchive == expectedCreated && intact;
                var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
                var packs = Directory.GetFiles(runtime.Repository.Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories);
                var payloadPaths = ((await runtime.LocalReplicaCatalogStore.LoadAsync()).Value?.Entries ?? [])
                    .Select(e => e.Locator.AbsolutePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                long totalPayloadBytes = payloadPaths.Sum(p => new FileInfo(p).Length);
                rows.Add(new { scenario, method, level = config.Archive.CompressionLevel, kind, iteration,
                    fileCount = fileCount + 1 + (scenario == "large-file" ? 1 : 0), fileReadyMs,
                    totalMs = clock.Elapsed.TotalMilliseconds, allocatedBytes, correct,
                    outcome = result.Outcome.ToString(), result.CreatedNewArchive, packCount = packs.Length,
                    totalPayloadBytes, payloadCount = payloadPaths.Length, diagnostic, timing, activityStages,
                    cache = "uncontrolled", validation = "production admission plus immutable payload hashes; no restore roundtrip" });
                if (!correct) throw new InvalidOperationException($"Unexpected {kind} result: {result.Outcome}, created={result.CreatedNewArchive}.");
                if (iteration % 50 == 0) await Save();
            }
            async Task Save() => await File.WriteAllTextAsync(Path.Combine(Root, "analysis-probe.json"),
                JsonSerializer.Serialize(new { scenario, rows }, new JsonSerializerOptions { WriteIndented = true }));

            await Sample("initial-full", 0, true);
            if (scenario == "large-file")
            {
                for (int i = 1; i <= 5; i++)
                {
                    using (var file = new FileStream(big, FileMode.Open, FileAccess.ReadWrite))
                    {
                        int value = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(value ^ 1));
                    }
                    await Sample("one-byte-change-in-64MiB-file", i, true);
                }
            }
            else
            {
                int noOps = scenario == "history-growth" ? 300 : 5;
                for (int i = 1; i <= noOps; i++) await Sample("unchanged", i, false);
                if (scenario != "history-growth")
                    for (int i = 1; i <= 5; i++)
                    {
                        await File.WriteAllTextAsync(changed, $"value-{i:D5}");
                        await Sample("tiny-change", i, true);
                    }
            }
            await Save();
        }
    }
}
