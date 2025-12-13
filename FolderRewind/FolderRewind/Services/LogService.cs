using System;
using System.Collections.Generic;
using System.IO;

namespace FolderRewind.Services
{
    public static class LogService
    {
        private static readonly object _lock = new();
        private static readonly List<string> _buffer = new();
        private const int MaxBufferLines = 2000;

        public static event Action<string>? LogReceived;

        public static IReadOnlyList<string> GetSnapshot()
        {
            lock (_lock)
            {
                return _buffer.ToArray();
            }
        }

        public static void Log(string message)
        {
            if (message == null) return;

            var line = message;

            lock (_lock)
            {
                _buffer.Add(line);
                if (_buffer.Count > MaxBufferLines)
                {
                    _buffer.RemoveRange(0, _buffer.Count - MaxBufferLines);
                }
            }

            try
            {
                AppendToFile(line);
            }
            catch
            {
                // ignore file IO errors
            }

            try
            {
                LogReceived?.Invoke(line);
            }
            catch
            {
                // ignore subscriber failures
            }
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
                // ignore
            }
        }

        private static void AppendToFile(string message)
        {
            var filePath = GetLogFilePath();
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var timePrefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ";
            File.AppendAllText(filePath, timePrefix + message + Environment.NewLine);
        }

        private static string GetLogFilePath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "FolderRewind", "logs", "app.log");
        }
    }
}
