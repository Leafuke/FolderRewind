// Harness only. The five released source files are loaded verbatim from v1.8.2 by the PowerShell script.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Models;
using FolderRewind.Services;

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
foreach (var format in new[] { "7z", "zip" })
{
    var directory = Path.Combine(output, format); Directory.CreateDirectory(directory);
    var original = new Dictionary<string, string> { ["a.txt"] = "before", ["deleted.txt"] = "delete-me", ["switch"] = "file" };
    var changed = new Dictionary<string, string> { ["a.txt"] = "after", ["switch/child.txt"] = "directory", ["中文.txt"] = "中文内容" };
    await BackupService.Produce(directory, args[1], "full." + format, "Full", original, 1);
    await BackupService.Produce(directory, args[1], "smart." + format, "Smart", changed, 2);
    var again = new Dictionary<string, string> { ["a.txt"] = "after", ["switch"] = "file again", ["deleted.txt"] = "recreated" };
    await BackupService.Produce(directory, args[1], "smart2." + format, "Smart", again, 3);
    await File.WriteAllTextAsync(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new
    {
        files = again.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Value))).ToLowerInvariant()),
        absent = new[] { "switch/child.txt", "中文.txt" }
    }, new JsonSerializerOptions { WriteIndented = true }));
}

namespace Microsoft.UI.Xaml.Controls { internal sealed class UnusedNamespaceAnchor { } }
namespace FolderRewind.Models
{
    // Metadata writer receives explicit file states. UI/config/filter types are not exercised by this harness.
    public sealed class BackupConfig { public string DestinationPath { get; set; } = ""; }
    public sealed class FilterSettings { }
    [JsonSerializable(typeof(BackupMetadata))]
    [JsonSerializable(typeof(BackupMetadataState))]
    [JsonSerializable(typeof(BackupChangeRecord))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal partial class AppJsonContext : JsonSerializerContext { }
}
namespace FolderRewind.Services
{
    internal static class I18n
    {
        public static string Format(string key, params object[] args) => key + ": " + string.Join(", ", args);
        public static string GetString(string key) => key;
    }
    internal static class LogService
    {
        public static void LogWarning(string text, string category) => Console.Error.WriteLine(text);
        public static void LogError(string text, string category, Exception error) => Console.Error.WriteLine(error);
    }
    public static partial class BackupService
    {
        private enum LogLevel { Warning, Error }
        private static void Log(string text, LogLevel level) => Console.Error.WriteLine(text);
        private sealed class BackupChangeSet
        {
            public List<string> AddedFiles { get; } = new();
            public List<string> ModifiedFiles { get; } = new();
            public List<string> DeletedFiles { get; } = new();
        }
        private static Dictionary<string, FileState> ScanDirectory(string source, FilterSettings? filters) => throw new NotSupportedException("Harness supplies real tree file states.");
        private static bool TryResolveBackupStoragePaths(string root, string name, string? fallbackPath, out string storage, out string backup, out string metadata)
            => BackupStoragePathService.TryResolveBackupStoragePaths(root, name, fallbackPath, out storage, out backup, out metadata);

        public static async Task Produce(string directory, string sevenZip, string name, string type, Dictionary<string, string> tree, int generation)
        {
            var metadata = Path.Combine(directory, "_metadata");
            var previous = ConvertToAggregateMetadata(await LoadBackupMetadataAsync(metadata));
            var states = tree.ToDictionary(p => p.Key, p => new FileState
            {
                Size = System.Text.Encoding.UTF8.GetByteCount(p.Value), LastWriteTimeUtc = new DateTime(2026, 1, generation, 0, 0, 0, DateTimeKind.Utc),
                Hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Value))).ToLowerInvariant()
            });
            var change = CompareFileStates(states, previous?.FileStates);
            var payload = Path.Combine(directory, "payload-" + generation); Directory.CreateDirectory(payload);
            foreach (var path in type == "Full" ? tree.Keys : change.AddedFiles.Concat(change.ModifiedFiles))
            {
                var target = Path.Combine(payload, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, tree[path], new System.Text.UTF8Encoding(false));
            }
            var start = new ProcessStartInfo(sevenZip) { WorkingDirectory = payload, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "a", Path.Combine(directory, name), "*", "-y" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(await stdout + await stderr);
            if (!await UpdateMetadataAsync(payload, metadata, name, type == "Full" ? name : previous!.BasedOnFullBackup,
                type, previous, states, change)) throw new IOException("Released writer rejected fixture.");
            // Only the harness-created payload directory is disposable; it is never a user backup tree.
            Directory.Delete(payload, true);
        }
    }
}
