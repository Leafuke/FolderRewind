using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Windows.Storage;

namespace FolderRewind.Services
{
    /// <summary>
    /// Centralized logging with in-memory buffer, file persistence, and live stream events.
    /// </summary>
    public static class LogService
    {
        private static readonly object _lock = new();
        private static readonly List<LogEntry> _buffer = new();
        private static LogOptions _options = new();

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
        }

        public static void Log(string message, LogLevel level = LogLevel.Info, string? source = null, Exception? exception = null)
        {
            if (string.IsNullOrWhiteSpace(message) && exception == null) return;
            if (level == LogLevel.Debug && !_options.EnableDebugLogs) return;

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
                _buffer.Add(entry);
                TrimBufferIfNeeded();
            }

            TryAppendToFile(entry);

            try
            {
                EntryPublished?.Invoke(entry);
            }
            catch
            {
                // ignore subscriber failures
            }
        }

        public static void LogInfo(string message, string? source = null) => Log(message, LogLevel.Info, source);
        public static void LogWarning(string message, string? source = null) => Log(message, LogLevel.Warning, source);
        public static void LogError(string message, string? source = null, Exception? exception = null) => Log(message, LogLevel.Error, source, exception);
        public static void LogDebug(string message, string? source = null) => Log(message, LogLevel.Debug, source);

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
            }

            try
            {
                var filePath = GetLogFilePath();
                if (File.Exists(filePath)) File.WriteAllText(filePath, string.Empty);
            }
            catch
            {
                // ignore file IO errors
            }
        }

        public static void OpenLogFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(GetLogFilePath());
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
                // ignore
            }
        }

        public static string GetLogFilePath()
        {
            return Path.Combine(GetWritableAppDataDir(), "FolderRewind", "logs", "app.log");
        }

        private static void TryAppendToFile(LogEntry entry)
        {
            if (!_options.EnableFileLogging) return;
            if (entry.Level == LogLevel.Debug && !_options.EnableDebugLogs) return;

            try
            {
                RotateIfNeeded();

                var filePath = GetLogFilePath();
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.AppendAllText(filePath, FormatLine(entry) + Environment.NewLine);
            }
            catch
            {
                // ignore file IO errors
            }
        }

        private static void RotateIfNeeded()
        {
            var filePath = GetLogFilePath();
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir)) return;

            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var info = new FileInfo(filePath);
                if (!info.Exists) return;

                var limitBytes = Math.Max(1024, _options.MaxFileSizeKb) * 1024L;
                if (info.Length <= limitBytes) return;

                var archivePath = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.Move(filePath, archivePath, true);

                TrimOldArchives(dir);
            }
            catch
            {
                // ignore rotation errors
            }
        }

        private static void TrimOldArchives(string dir)
        {
            try
            {
                var threshold = DateTime.Now.AddDays(-Math.Max(1, _options.RetentionDays));
                foreach (var file in Directory.EnumerateFiles(dir, "app-*.log"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTime < threshold)
                        {
                            info.Delete();
                        }
                    }
                    catch
                    {
                        // ignore single-file errors
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string FormatLine(LogEntry entry)
        {
            var level = entry.Level.ToString().ToUpperInvariant();
            var source = string.IsNullOrWhiteSpace(entry.Source) ? string.Empty : $"[{entry.Source}] ";
            var exception = string.IsNullOrWhiteSpace(entry.Exception) ? string.Empty : $" | {entry.Exception}";
            return $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {source}{entry.Message}{exception}";
        }

        private static void TrimBufferIfNeeded()
        {
            if (_buffer.Count <= _options.MaxEntries) return;

            var toRemove = _buffer.Count - _options.MaxEntries;
            _buffer.RemoveRange(0, toRemove);
        }

        private static LogOptions Normalize(LogOptions options)
        {
            return new LogOptions
            {
                EnableFileLogging = options.EnableFileLogging,
                EnableDebugLogs = options.EnableDebugLogs,
                MaxEntries = Math.Max(500, options.MaxEntries),
                MaxFileSizeKb = Math.Clamp(options.MaxFileSizeKb, 512, 1024 * 50),
                RetentionDays = Math.Clamp(options.RetentionDays, 1, 60)
            };
        }

        private static string GetWritableAppDataDir()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                if (!string.IsNullOrWhiteSpace(localFolder?.Path)) return localFolder.Path;
            }
            catch
            {
                // Unpackaged 下可能抛异常，回退即可
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }
}
