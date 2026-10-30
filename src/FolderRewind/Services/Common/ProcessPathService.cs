using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FolderRewind.Services
{
    internal static class ProcessPathService
    {
        public static IReadOnlyList<string> GetRunningProcessDirectories(string? executableName)
        {
            var normalizedExecutableName = NormalizeExecutableName(executableName);
            if (string.IsNullOrWhiteSpace(normalizedExecutableName))
            {
                return Array.Empty<string>();
            }

            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processName = Path.GetFileNameWithoutExtension(normalizedExecutableName);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return Array.Empty<string>();
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    I18n.Format("ProcessPathService_Log_QueryFailed", normalizedExecutableName, ex.Message),
                    nameof(ProcessPathService));
                return Array.Empty<string>();
            }

            var inspectFailures = 0;
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        var processPath = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(processPath))
                        {
                            continue;
                        }

                        var resolvedExecutableName = Path.GetFileName(processPath);
                        if (!string.Equals(resolvedExecutableName, normalizedExecutableName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var directory = Path.GetDirectoryName(processPath);
                        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                        {
                            directories.Add(directory);
                        }
                    }
                    catch
                    {
                        inspectFailures++;
                    }
                }
            }

            if (inspectFailures > 0)
            {
                LogService.LogWarning(
                    I18n.Format("ProcessPathService_Log_InspectPartialFailed", normalizedExecutableName, inspectFailures.ToString()),
                    nameof(ProcessPathService));
            }

            return directories
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeExecutableName(string? executableName)
        {
            var normalized = executableName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            normalized = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : normalized + ".exe";
        }
    }
}
