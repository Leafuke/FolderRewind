using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    /// <summary>
    /// Centralized logging with in-memory buffer, file persistence (per-day files), and live stream events.
    /// </summary>
    public static class LogService
    {
        private static readonly object _lock = new();
        private static readonly Queue<LogEntry> _buffer = new();
        private static long _publicationSequence;
        private static LogOptions _options = new();
        private static string _currentLogDate = string.Empty;
        private sealed record LogWork(LogEntry? Entry = null, bool Clear = false, TaskCompletionSource? Completion = null);
        private static readonly Channel<LogWork> _logChannel = Channel.CreateUnbounded<LogWork>(new UnboundedChannelOptions { SingleReader = true });
        private static readonly Task LogWorker = Task.Run(ProcessLogQueueAsync);
        private static StreamWriter? _fileWriter;
        private static string? _writerPath;
        private static long _writerBytes;

        private static async Task ProcessLogQueueAsync()
        {
            while (await _logChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var started = Stopwatch.GetTimestamp();
                var count = 0;
                while (count < 100)
                {
                    if (!_logChannel.Reader.TryRead(out var work))
                    {
                        var remaining = TimeSpan.FromMilliseconds(100) - Stopwatch.GetElapsedTime(started);
                        if (remaining <= TimeSpan.Zero) break;
                        using var timeout = new CancellationTokenSource(remaining);
                        try { if (!await _logChannel.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false)) break; }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }
                    try
                    {
                        if (work.Clear)
                        {
                            CloseWriter();
                            Directory.CreateDirectory(GetLogDirectory());
                            File.WriteAllText(GetLogFilePath(), string.Empty);
                        }
                        if (work.Entry is { } entry) AppendToWriter(entry);
                        if (work.Completion is not null) { FlushWriter(); work.Completion.TrySetResult(); }
                    }
                    catch (Exception error)
                    {
                        work.Completion?.TrySetException(error);
                        Debug.WriteLine($"Log write failed: {error.Message}");
                        try { CloseWriter(); } catch { _fileWriter = null; }
                    }
                    count++;
                    if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(100)) break;
                }
                try { FlushWriter(); } catch (Exception error) { Debug.WriteLine(error); }
            }
            CloseWriter();
        }

        private static void FlushWriter() => _fileWriter?.Flush();
        private static void CloseWriter()
        {
            var writer = _fileWriter; _fileWriter = null; _writerPath = null;
            writer?.Dispose();
        }

        private static void AppendToWriter(LogEntry entry)
        {
            if (!_options.EnableFileLogging) { CloseWriter(); return; }
            var path = GetLogFilePath();
            if (_writerPath != path) CloseWriter();
            if (_fileWriter is not null && _writerBytes >= Math.Max(1024, _options.MaxFileSizeKb) * 1024L)
                CloseWriter();
            if (_fileWriter is null)
            {
                Directory.CreateDirectory(GetLogDirectory());
                RotateIfNeeded(path);
                var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (_currentLogDate != today) { _currentLogDate = today; TrimOldLogFiles(); }
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024);
                _writerBytes = stream.Length; _writerPath = path;
                _fileWriter = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024);
            }
            var line = FormatEntry(entry);
            _fileWriter.WriteLine(line);
            _writerBytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        }

        public static Task FlushAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (!_logChannel.Writer.TryWrite(new LogWork(Completion: completion))) return LogWorker;
            }
            return completion.Task;
        }

        public static async Task StopAsync()
        {
            lock (_lock) _logChannel.Writer.TryComplete();
            await LogWorker.ConfigureAwait(false);
        }

        public static event Action<LogEntry>? EntryPublished;

        public static IReadOnlyList<LogEntry> GetEntriesSnapshot()
        {
            lock (_lock)
            {
                return _buffer.ToArray();
            }
        }

        public static void ApplyOptions(LogOptions? options)
        {
            if (options == null) return;

            lock (_lock)
            {
                _options = Normalize(options);
                TrimBufferIfNeeded();
            }

            // Apply retention policy when options change
            TrimOldLogFiles();
        }

        public static void Log(string message, LogLevel level = LogLevel.Info, string? source = null, Exception? exception = null)
        {
            if (string.IsNullOrWhiteSpace(message) && exception == null) return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message?.Trim() ?? string.Empty,
                Source = source,
                Exception = exception?.ToString()
            };

            lock (_lock)
            {
                entry.Sequence = ++_publicationSequence;
                _buffer.Enqueue(entry);
                TrimBufferIfNeeded();
                _logChannel.Writer.TryWrite(new LogWork(entry));
            }

            try
            {
                EntryPublished?.Invoke(entry);
            }
            catch
            {

            }
        }

        public static void LogInfo(string message, string? source = null) => Log(message, LogLevel.Info, source);
        public static void LogWarning(string message, string? source = null) => Log(message, LogLevel.Warning, source);
        public static void LogError(string message, string? source = null, Exception? exception = null) => Log(message, LogLevel.Error, source, exception);

        public static void MarkSessionStart()
        {
            var divider = new string('-', 64);
            Log(divider, LogLevel.Info, "Session");
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _logChannel.Writer.TryWrite(new LogWork(Clear: true));
            }
        }

        public static void OpenLogFolder()
        {
            try
            {
                var folder = GetLogDirectory();
                if (string.IsNullOrWhiteSpace(folder)) return;

                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch
            {

            }
        }

        public static string GetLogDirectory()
        {
            return Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "logs");
        }

        public static string GetLogFilePath()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Path.Combine(GetLogDirectory(), $"app-{today}.log");
        }

        public static string GetLogFilePath(DateTime date)
        {
            var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return Path.Combine(GetLogDirectory(), $"app-{dateStr}.log");
        }

        private static void RotateIfNeeded(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return;

                var limitBytes = Math.Max(1024, _options.MaxFileSizeKb) * 1024L;
                if (info.Length < limitBytes) return;

                var dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(dir)) return;

                var baseName = Path.GetFileNameWithoutExtension(filePath);
                var archiveName = FormattableString.Invariant($"{baseName}-{DateTime.Now:HHmmssfffffff}-{Guid.NewGuid():N}.log");
                var archivePath = Path.Combine(dir, archiveName);
                File.Move(filePath, archivePath, true);
            }
            catch
            {

            }
        }

        private static void TrimOldLogFiles()
        {
            try
            {
                var dir = GetLogDirectory();
                if (!Directory.Exists(dir)) return;

                var threshold = DateTime.Now.Date.AddDays(-Math.Max(1, _options.RetentionDays));

                foreach (var file in Directory.EnumerateFiles(dir, "app-*.log"))
                {
                    try
                    {
                        var info = new FileInfo(file);

                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.StartsWith("app-") && fileName.Length >= 14)
                        {
                            var dateStr = fileName.Substring(4, 10); // Extract yyyy-MM-dd
                            if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var fileDate))
                            {
                                if (fileDate < threshold)
                                {
                                    info.Delete();
                                    continue;
                                }
                            }
                        }

                        // Fallback: use file's last write time
                        if (info.LastWriteTime < threshold)
                        {
                            info.Delete();
                        }
                    }
                    catch
                    {

                    }
                }
            }
            catch
            {

            }
        }

        public static string FormatEntry(LogEntry entry)
        {
            var level = entry.Level.ToString().ToUpperInvariant();
            var source = string.IsNullOrWhiteSpace(entry.Source) ? string.Empty : $"[{entry.Source}] ";
            var exception = string.IsNullOrWhiteSpace(entry.Exception) ? string.Empty : $" | {entry.Exception}";
            return FormattableString.Invariant(
                $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {source}{entry.Message}{exception}");
        }

        private static void TrimBufferIfNeeded()
        {
            if (_buffer.Count <= _options.MaxEntries) return;

            while (_buffer.Count > _options.MaxEntries) _buffer.Dequeue();
        }

        private static LogOptions Normalize(LogOptions options)
        {
            return new LogOptions
            {
                EnableFileLogging = options.EnableFileLogging,
                MaxEntries = Math.Max(500, options.MaxEntries),
                MaxFileSizeKb = Math.Clamp(options.MaxFileSizeKb, 512, 1024 * 50),
                RetentionDays = Math.Clamp(options.RetentionDays, 1, 60)
            };
        }

    }
}
