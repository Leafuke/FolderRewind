using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Host facade: captures presentation and operation state before handing off to the Core file writer.</summary>
public static class LogService
{
    private static readonly object Sync = new();
    private static readonly Queue<LogEntry> Buffer = new();
    private static readonly LogPersistenceWriter Writer = new(GetLogDirectory);
    private static long _sequence;
    private static LogOptions _options = new();
    public static event Action<LogEntry>? EntryPublished;
    public static LogHealthSnapshot Health => Writer.Health;

    public static IReadOnlyList<LogEntry> GetEntriesSnapshot()
    { lock (Sync) return Buffer.ToArray(); }

    public static void ApplyOptions(LogOptions? options)
    {
        if (options is null) return;
        lock (Sync)
        {
            _options = LogPersistenceWriter.Normalize(options);
            TrimBuffer();
            Writer.ApplyOptions(_options);
        }
    }

    public static void Log(string message, LogLevel level = LogLevel.Info, string? source = null,
        Exception? exception = null, string? operationId = null)
    {
        if (string.IsNullOrWhiteSpace(message) && exception is null) return;
        var context = NotificationOperationScope.Current?.Capture();
        var sensitive = source?.Contains("Cloud", StringComparison.OrdinalIgnoreCase) == true
            || source?.Contains("Plugin", StringComparison.OrdinalIgnoreCase) == true || context?.PluginId is not null;
        var details = exception is null ? null : exception.Data["SafeDiagnostic"] as string
            ?? DiagnosticPrivacy.ExceptionDetails(exception, sensitive);
        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow, Level = level, Message = DiagnosticPrivacy.Text(message?.Trim()),
            Source = DiagnosticPrivacy.Text(source), Exception = details,
            OperationId = operationId ?? context?.OperationId, Context = context
        };
        lock (Sync)
        {
            entry.Sequence = ++_sequence;
            entry.PersistenceAccepted = Writer.TryWrite(entry);
            Buffer.Enqueue(entry);
            TrimBuffer();
        }
        LogPersistenceWriter.Publish(EntryPublished, entry);
    }

    public static void LogInfo(string message, string? source = null) => Log(message, LogLevel.Info, source);
    public static void LogWarning(string message, string? source = null) => Log(message, LogLevel.Warning, source);
    public static void LogError(string message, string? source = null, Exception? exception = null) => Log(message, LogLevel.Error, source, exception);
    public static void MarkSessionStart() => Log(new string('-', 64), LogLevel.Info, "Session");
    public static Task<LogPersistenceResult> FlushAsync() { lock (Sync) return Writer.FlushAsync(); }
    public static Task<LogPersistenceResult> ClearAsync()
    {
        lock (Sync)
        {
            if (Health.LifecycleState == LogLifecycleState.Running) Buffer.Clear();
            return Writer.ClearAsync();
        }
    }
    public static Task<LogPersistenceResult> StopAsync() { lock (Sync) return Writer.StopAsync(); }

    public static Task<LogPersistenceResult> StopForExitAsync() => Writer.StopWithinAsync(TimeSpan.FromSeconds(5));

    public static void TryFlushFatal()
    {
        try { _ = Writer.FlushAsync().Wait(TimeSpan.FromSeconds(1)); }
        catch { /* Fatal handlers cannot depend on the UI thread or recursively log failures. */ }
    }

    public static string GetLogDirectory() => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "logs");
    public static string GetLogFilePath() => GetLogFilePath(DateTime.Now);
    public static string GetLogFilePath(DateTime date) => Path.Combine(GetLogDirectory(), "app-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
    public static string FormatEntry(LogEntry entry) => LogPersistenceWriter.FormatEntry(entry);

    public static void OpenLogFolder()
    {
        try { var folder = GetLogDirectory(); Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception error) { Debug.WriteLine(DiagnosticPrivacy.ExceptionMetadata(error)); }
    }

    private static void TrimBuffer() { while (Buffer.Count > _options.MaxEntries) Buffer.Dequeue(); }
}
