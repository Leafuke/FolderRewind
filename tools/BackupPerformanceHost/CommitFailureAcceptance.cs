using System.Reflection;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

// Exercises the real BackupService caller; all data is under Program's validated isolated root.
internal static class CommitFailureAcceptance
{
    internal static async Task RunAsync()
    {
        ConfigService.CurrentConfig.GlobalSettings.EnableNotices = false;
        ConfigService.CurrentConfig.GlobalSettings.FileSizeWarningThresholdKB = 0;
        var results = new List<object>();
        foreach (var mode in new[] { BackupMode.Full, BackupMode.Smart, BackupMode.Rolling })
        foreach (var unreadable in new[] { false, true })
        foreach (var entry in new[] { "public", "plugin-folder", "plugin-config" })
        {
            var root = Path.Combine(Root, mode + "-" + unreadable + "-" + entry);
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "file.txt"), "original");
            var config = new BackupConfig { Name = "commit-failure", DestinationPath = Path.Combine(root, "backups") };
            var folder = new ManagedFolder { Path = source, DisplayName = "synthetic" };
            config.SourceFolders.Add(folder); config.Archive.Mode = mode;
            config.Archive.MaxSmartBackupsPerFull = 0; config.Archive.CompressionLevel = 1;
            ConfigService.CurrentConfig.BackupConfigs.Add(config);
            Require((await ConfigService.SaveAsync()).Success, "Config save failed");
            Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).IsSuccessful, "Initial capture failed");
            var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
            var repository = runtime.Repository;
            var callbackProperty = typeof(FileHistoryRepository).GetProperty("PacksInstalled", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = (Func<IReadOnlyList<HistoryPackReadResult>, IReadOnlyList<HistoryPackReadResult>, CancellationToken, Task>?)callbackProperty.GetValue(repository);
            FileStream? held = null;
            Func<IReadOnlyList<HistoryPackReadResult>, IReadOnlyList<HistoryPackReadResult>, CancellationToken, Task> fault = async (old, added, token) =>
            {
                if (original is not null) await original(old, added, token);
                var pack = added.Single().Pack;
                held = new FileStream(unreadable ? repository.Paths.GetPackPath(pack.PackId) : repository.Paths.GetJournalPath(pack.TransactionId),
                    FileMode.Open, FileAccess.Read, unreadable ? FileShare.None : FileShare.Read);
                if (unreadable) throw new IOException("Injected post-publication failure");
            };
            callbackProperty.SetValue(repository, fault);
            File.AppendAllText(Path.Combine(source, "file.txt"), " changed");
            await Task.Delay(1100); // Existing archive naming may use a one-second window.
            BackupOperationResult result;
            try { result = await InvokeAsync(entry, config, folder); }
            finally { held?.Dispose(); callbackProperty.SetValue(repository, original); }
            Require(result.HistoryRecoveryRequired && !result.IsSuccessful, "Commit uncertainty was reported as ordinary success/failure");
            if (unreadable) Require(result.Outcome == OperationOutcome.RecoveryRequired, "Unknown publication was reported committed");
            else if (entry != "public") Require(result.Outcome == OperationOutcome.CommittedRecoveryRequired, "Plugin lost the confirmed commit recovery outcome");
            for (int repeat = 0; repeat < 2; repeat++) await runtime.InitializeAsync();
            var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync()).Value!;
            Require((await repository.ReadAllPacksAsync()).Count == 2, "Recovery duplicated facts");
            Require(catalog.Entries.Length == 2 && catalog.Entries.All(entry => File.Exists(entry.Locator.AbsolutePath)), "Published archive was deleted");
            results.Add(new { mode = mode.ToString(), unreadable, entry, result.Outcome, result.HistoryRecoveryRequired, retained = catalog.Entries.Length });
        }
        await LogService.FlushAsync();
        await File.WriteAllTextAsync(Path.Combine(Root, "commit-failure-results.json"), JsonSerializer.Serialize(results));
    }
    private static async Task<BackupOperationResult> InvokeAsync(string entry, BackupConfig config, ManagedFolder folder)
    {
        if (entry == "public") return await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal());
        var method = typeof(BackupService).GetMethod(entry == "plugin-folder" ? "BackupFolderForPluginAsync" : "BackupConfigurationForPluginAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object sources = entry == "plugin-folder" ? folder : new[] { folder };
        var task = (Task)method.Invoke(null, [config, sources, BackupInvocationOptions.ForInternal(), CancellationToken.None])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var outcome = (OperationOutcome)result.GetType().GetProperty("Outcome")!.GetValue(result)!;
        return new(outcome, (bool)result.GetType().GetProperty("CreatedNewArchive")!.GetValue(result)!,
            outcome is OperationOutcome.CommittedRecoveryRequired or OperationOutcome.RecoveryRequired);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
