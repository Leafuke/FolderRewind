using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using static FolderRewind.PerformanceHost.Program;

namespace FolderRewind.PerformanceHost;

internal static class BackupFailureAcceptance
{
    // A controllable archive child avoids timing assumptions about real compression speed.
    internal static bool RunArchiveChild(string[] args)
    {
        var fault = Environment.GetEnvironmentVariable("FOLDERREWIND_ARCHIVE_CHILD_FAULT");
        if (fault is null || args.Length == 0 || args[0] is not ("a" or "u" or "d" or "t" or "l")) return false;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT")!);
        var archive = args.First(arg => arg.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));
        if (!Path.GetFullPath(archive).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Archive probe escaped its isolated root.");
        // Let Rolling's baseline test complete before injecting the mutating command failure.
        if (args[0] == "t") return true;
        File.WriteAllText(archive, "partial candidate");
        File.WriteAllText(Path.Combine(root, "archive-child-ready"), Environment.ProcessId.ToString());
        if (fault == "cancel") Thread.Sleep(Timeout.Infinite);
        Environment.ExitCode = 2;
        return true;
    }

    internal static async Task RunAsync()
    {
        ConfigService.CurrentConfig.GlobalSettings.EnableNotices = false;
        ConfigService.CurrentConfig.GlobalSettings.FileSizeWarningThresholdKB = 0;
        var real = ConfigService.CurrentConfig.GlobalSettings.SevenZipPath;
        var results = new List<object>();
        foreach (var mode in new[] { BackupMode.Full, BackupMode.Smart, BackupMode.Rolling })
        foreach (var fault in new[] { "fail", "cancel" })
        {
            var root = Path.Combine(Root, mode + "-" + fault);
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var file = Path.Combine(source, "file.txt"); File.WriteAllText(file, "baseline");
            var config = new BackupConfig { Name = "backup-failure", DestinationPath = Path.Combine(root, "backups") };
            var folder = new ManagedFolder { Path = source, DisplayName = "synthetic" }; config.SourceFolders.Add(folder);
            config.Archive.Mode = mode; config.Archive.MaxSmartBackupsPerFull = 0;
            ConfigService.CurrentConfig.BackupConfigs.Add(config);
            Require((await ConfigService.SaveAsync()).Success, "Config save failed");
            Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).IsSuccessful, "Initial capture failed");
            var archives = Directory.GetFiles(config.DestinationPath, "*.7z", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            File.AppendAllText(file, " changed");
            var marker = Path.Combine(Root, "archive-child-ready"); File.Delete(marker);
            using var cancellation = new CancellationTokenSource();
            ConfigService.CurrentConfig.GlobalSettings.SevenZipPath = Environment.ProcessPath!;
            Environment.SetEnvironmentVariable("FOLDERREWIND_ARCHIVE_CHILD_FAULT", fault);
            BackupOperationResult result;
            try
            {
                var backup = BackupWithCancellationAsync(config, folder, cancellation.Token);
                if (fault == "cancel")
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!File.Exists(marker) && !backup.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(20);
                    Require(File.Exists(marker), "Archive child did not reach its copy barrier");
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
                    cancellation.Cancel();
                    result = await backup.WaitAsync(TimeSpan.FromSeconds(20));
                    Require(child.HasExited, "Canceled operation left its child process alive");
                    Require(result.Outcome == OperationOutcome.Canceled, "Cancellation became ordinary failure");
                }
                else result = await backup.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                cancellation.Cancel();
                Environment.SetEnvironmentVariable("FOLDERREWIND_ARCHIVE_CHILD_FAULT", null);
                ConfigService.CurrentConfig.GlobalSettings.SevenZipPath = real;
            }
            Require(!result.IsSuccessful, "Injected archive failure succeeded");
            Require(Directory.GetFiles(config.DestinationPath, "*.7z", SearchOption.AllDirectories).Length == archives.Count, "Candidate archive leaked");
            Require(archives.All(pair => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value), "Committed archive changed");
            if (mode == BackupMode.Smart && fault == "fail") await VerifyMissingPasswordAsync(config, folder);
            Require((await BackupService.BackupFolderAsync(config, folder, BackupInvocationOptions.ForInternal())).IsSuccessful, "Configuration gate could not resume");
            results.Add(new { mode = mode.ToString(), fault, result.Outcome });
        }
        await File.WriteAllTextAsync(Path.Combine(Root, "backup-failure-results.json"), JsonSerializer.Serialize(results));
    }
    private static async Task VerifyMissingPasswordAsync(BackupConfig config, ManagedFolder folder)
    {
        var temporary = Path.Combine(Root, "password-temporary"); Directory.CreateDirectory(temporary);
        var previous = Environment.GetEnvironmentVariable("TMP");
        try
        {
            Environment.SetEnvironmentVariable("TMP", temporary);
            Require(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') == Path.GetFullPath(temporary).TrimEnd('\\'), "Temp isolation failed");
            config.IsEncrypted = true;
            var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
            var source = new SourceId(Guid.Parse(folder.Id));
            var baseline = await runtime.CaptureBaselines.LoadAsync(source);
            Require(baseline is not null, "Missing Smart baseline");
            var method = typeof(BackupService).GetMethod("DoSmartBackupAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = await (Task<SourceCaptureResult>)method.Invoke(null, [source, CaptureScope.FullSource, folder.Path,
                Path.GetDirectoryName(baseline!.PayloadPath)!, baseline, "password", config, folder.SourceScope, "", null, null, CancellationToken.None])!;
            Require(result.Outcome == SourceCaptureOutcome.Failed, "Missing password was accepted");
            Require(!Directory.EnumerateFileSystemEntries(temporary).Any(), "Password preflight left a temporary list");
        }
        finally { config.IsEncrypted = false; Environment.SetEnvironmentVariable("TMP", previous); }
    }
    private static async Task<BackupOperationResult> BackupWithCancellationAsync(BackupConfig config, ManagedFolder folder, CancellationToken token)
    {
        var method = typeof(BackupService).GetMethod("BackupFolderForPluginAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(null, [config, folder, BackupInvocationOptions.ForInternal(), token])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return new((OperationOutcome)result.GetType().GetProperty("Outcome")!.GetValue(result)!,
            (bool)result.GetType().GetProperty("CreatedNewArchive")!.GetValue(result)!);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
