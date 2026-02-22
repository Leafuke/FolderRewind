using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Windows.Storage;

namespace FolderRewind.Services
{
    /// <summary>
    /// Centralized logging with in-memory buffer, file persistence (per-day files), and live stream events.
    /// </summary>
    public static class LogService
    {
        private static readonly object _lock = new();
        private static readonly List<LogEntry> _buffer = new();
        private static LogOptions _options = new();
        private static string _currentLogDate = string.Empty;

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
            }

            try
            {
                var filePath = GetLogFilePath();
                if (File.Exists(filePath)) File.WriteAllText(filePath, string.Empty);
            }
            catch
            {

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
            return Path.Combine(GetWritableAppDataDir(), "FolderRewind", "logs");
        }

        public static string GetLogFilePath()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            return Path.Combine(GetLogDirectory(), $"app-{today}.log");
        }

        public static string GetLogFilePath(DateTime date)
        {
            var dateStr = date.ToString("yyyy-MM-dd");
            return Path.Combine(GetLogDirectory(), $"app-{dateStr}.log");
        }

        private static void TryAppendToFile(LogEntry entry)
        {
            if (!_options.EnableFileLogging) return;

            try
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var filePath = GetLogFilePath();
                var dir = GetLogDirectory();

                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (_currentLogDate != today)
                {
                    _currentLogDate = today;
                    TrimOldLogFiles();
                }

                RotateIfNeeded(filePath);

                File.AppendAllText(filePath, FormatLine(entry) + Environment.NewLine);
            }
            catch
            {

            }
        }

        private static void RotateIfNeeded(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists) return;

                var limitBytes = Math.Max(1024, _options.MaxFileSizeKb) * 1024L;
                if (info.Length <= limitBytes) return;

                var dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(dir)) return;

                var baseName = Path.GetFileNameWithoutExtension(filePath);
                var archivePath = Path.Combine(dir, $"{baseName}-{DateTime.Now:HHmmss}.log");
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
