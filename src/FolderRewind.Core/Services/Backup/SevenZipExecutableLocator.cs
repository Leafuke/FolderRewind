using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.Services
{
    internal static class SevenZipExecutableLocator
    {
        private static readonly object CacheLock = new();
        private static string? _cachedExecutable;
        private static string? _cachedConfiguredPath;
        private static string? _cachedPathEnvironment;

        public static string? Resolve(string? configuredPath)
        {
            string? pathEnvironment = Environment.GetEnvironmentVariable("PATH");
            lock (CacheLock)
            {
                if (string.Equals(_cachedConfiguredPath, configuredPath, StringComparison.Ordinal)
                    && string.Equals(_cachedPathEnvironment, pathEnvironment, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_cachedExecutable)
                    && File.Exists(_cachedExecutable))
                {
                    return _cachedExecutable;
                }
            }

            string resolved = FindFirstExisting(
                configuredPath,
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                pathEnvironment,
                File.Exists);

            lock (CacheLock)
            {
                _cachedExecutable = string.IsNullOrWhiteSpace(resolved) ? null : resolved;
                _cachedConfiguredPath = configuredPath;
                _cachedPathEnvironment = pathEnvironment;
            }

            return _cachedExecutable;
        }

        internal static string FindFirstExisting(
            string? configuredPath,
            string applicationBaseDirectory,
            string? programFilesDirectory,
            string? programFilesX86Directory,
            string? pathEnvironment,
            Func<string, bool> fileExists)
        {
            foreach (var candidate in BuildCandidates(
                configuredPath,
                applicationBaseDirectory,
                programFilesDirectory,
                programFilesX86Directory,
                pathEnvironment))
            {
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        internal static IReadOnlyList<string> BuildCandidates(
            string? configuredPath,
            string applicationBaseDirectory,
            string? programFilesDirectory,
            string? programFilesX86Directory,
            string? pathEnvironment)
        {
            var candidates = new List<string>();

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                try
                {
                    candidates.Add(Path.GetFullPath(path));
                }
                catch
                {
                    candidates.Add(path);
                }
            }

            AddCandidate(configuredPath);
            if (!string.IsNullOrWhiteSpace(configuredPath) && !Path.IsPathRooted(configuredPath))
            {
                AddCandidate(Path.Combine(applicationBaseDirectory, configuredPath));
            }

            string[] executableNames = { "7z.exe", "7zz.exe", "7za.exe" };
            foreach (var executableName in executableNames)
            {
                AddCandidate(Path.Combine(applicationBaseDirectory, executableName));

                if (!string.IsNullOrWhiteSpace(programFilesDirectory))
                {
                    AddCandidate(Path.Combine(programFilesDirectory, "7-Zip", executableName));
                }

                if (!string.IsNullOrWhiteSpace(programFilesX86Directory))
                {
                    AddCandidate(Path.Combine(programFilesX86Directory, "7-Zip", executableName));
                }
            }

            if (!string.IsNullOrWhiteSpace(pathEnvironment))
            {
                foreach (var directory in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmedDirectory = directory.Trim();
                    foreach (var executableName in executableNames)
                    {
                        AddCandidate(Path.Combine(trimmedDirectory, executableName));
                    }
                }
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
