using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Retention;

public sealed class HistoryCleanupReport
{
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Trigger { get; set; } = "Manual";
    public int KeepCount { get; set; }
    public HistoryRetentionBenefitPolicy Policy { get; set; }
    public string Status { get; set; } = "Running";
    public List<HistoryCleanupSourceReport> Sources { get; set; } = [];
    public List<HistoryCleanupIssue> Issues { get; set; } = [];
}

public sealed class HistoryCleanupSourceReport
{
    public string SourceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int? Before { get; set; }
    public int? After { get; set; }
    public int? Recent { get; set; }
    public int? ExtraProtected { get; set; }
    // Shared-group byte/file totals are attached only to the first member, never double counted.
    public int DeletedArchives { get; set; }
    public long CreatedBytes { get; set; }
    public long ReclaimedBytes { get; set; }
    public List<HistoryCleanupIssue> Issues { get; set; } = [];
}

public sealed record HistoryCleanupIssue(string Code, string Detail = "", string? Version = null);

public sealed class HistoryCleanupReportStore(string localStateRoot)
{
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);
    internal IDisposable BeginRun()
    {
        var key = Path.GetFullPath(localStateRoot);
        Active[key] = 0;
        return new ActiveRun(key);
    }
    private sealed class ActiveRun(string key) : IDisposable
    {
        public void Dispose() => Active.TryRemove(key, out _);
    }
    private string PathFor(string name) => Path.Combine(localStateRoot, name);
    public HistoryCleanupReport? Read()
    {
        var path = PathFor("retention-report.json");
        if (!File.Exists(path)) return null;
        var report = JsonSerializer.Deserialize(File.ReadAllText(path), CleanupJsonContext.Default.HistoryCleanupReport);
        if (report?.Status == "Running" && !Active.ContainsKey(Path.GetFullPath(localStateRoot))) report.Status = "Interrupted";
        return report;
    }
    public void Save(HistoryCleanupReport report) => Write("retention-report.json",
        JsonSerializer.Serialize(report, CleanupJsonContext.Default.HistoryCleanupReport));
    public Dictionary<string, string> ReadCache()
    {
        try
        {
            var path = PathFor("retention-cache.json");
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), CleanupJsonContext.Default.DictionaryStringString) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public void SaveCache(Dictionary<string, string> cache) => Write("retention-cache.json",
        JsonSerializer.Serialize(cache, CleanupJsonContext.Default.DictionaryStringString));
    private void Write(string name, string text)
    {
        Directory.CreateDirectory(localStateRoot);
        var path = PathFor(name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }
}

[JsonSerializable(typeof(HistoryCleanupReport))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class CleanupJsonContext : JsonSerializerContext;
