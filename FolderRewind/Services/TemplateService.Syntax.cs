using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FolderRewind.Services
{
    public static partial class BackupPresetService
    {
        private static ObservableCollection<TemplatePathSegment> CloneSegments(IEnumerable<TemplatePathSegment>? segments)
        {
            var clone = new ObservableCollection<TemplatePathSegment>();
            foreach (var segment in segments ?? Array.Empty<TemplatePathSegment>())
            {
                clone.Add(new TemplatePathSegment
                {
                    Type = segment.Type,
                    Value = segment.Value
                });
            }

            return clone;
        }

        private static TemplatePathMarker CloneMarker(TemplatePathMarker marker)
        {
            return new TemplatePathMarker
            {
                Type = marker.Type,
                Value = marker.Value
            };
        }

        private static bool IsSensitiveSegment(string value, string userName, string userProfileName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return string.Equals(value, userName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, userProfileName, StringComparison.OrdinalIgnoreCase)
                || IsDynamicCandidate(value);
        }

        private static IEnumerable<(string Token, string Path)> GetKnownRoots()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return ("AppDataLocalLow", Path.Combine(userProfile, "AppData", "LocalLow"));
            yield return ("AppDataRoaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            yield return ("AppDataLocal", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            yield return ("DocumentsMyGames", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"));
            yield return ("SavedGames", Path.Combine(userProfile, "Saved Games"));
            yield return ("ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            foreach (var steamUserDataRoot in GetSteamUserDataRoots())
            {
                yield return ("SteamUserData", steamUserDataRoot);
            }
            yield return ("UserProfile", userProfile);
        }

        private static bool TryParseDisplayPath(string? displayPath, out ObservableCollection<TemplatePathSegment> segments)
        {
            segments = new ObservableCollection<TemplatePathSegment>();
            var normalized = (displayPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var parts = normalized
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();

            if (parts.Count == 0)
            {
                return false;
            }

            // DisplayPath 语法只允许三类：静态目录、{占位符}、{Process:xxx.exe}。
            for (int index = 0; index < parts.Count; index++)
            {
                var part = parts[index];
                if (index == 0 && TryParseDriveRoot(part, out var driveRoot))
                {
                    segments.Add(new TemplatePathSegment
                    {
                        Type = TemplatePathSegmentType.RootPath,
                        Value = driveRoot
                    });
                    continue;
                }

                if (part.StartsWith("{", StringComparison.Ordinal) && part.EndsWith("}", StringComparison.Ordinal) && part.Length > 2)
                {
                    var token = part[1..^1].Trim();
                    TemplatePathSegmentType segmentType;
                    string segmentValue;
                    if (TryParseProcessToken(token, out var processName))
                    {
                        segmentType = TemplatePathSegmentType.ProcessDirectory;
                        segmentValue = processName;
                    }
                    else
                    {
                        if (token.Contains(':'))
                        {
                            return false;
                        }

                        segmentType = IsKnownPlaceholderToken(token)
                            ? TemplatePathSegmentType.Placeholder
                            : TemplatePathSegmentType.EnumerateDirectory;
                        segmentValue = token;
                    }

                    segments.Add(new TemplatePathSegment
                    {
                        Type = segmentType,
                        Value = segmentValue
                    });
                    continue;
                }

                if (part.Contains(':') || part.Contains("..", StringComparison.Ordinal))
                {
                    return false;
                }

                segments.Add(new TemplatePathSegment
                {
                    Type = TemplatePathSegmentType.Static,
                    Value = Path.GetFileName(part)
                });
            }

            return segments.Count > 0;
        }

        private static bool TryParseDriveRoot(string? part, out string driveRoot)
        {
            driveRoot = string.Empty;
            if (string.IsNullOrWhiteSpace(part))
            {
                return false;
            }

            var normalized = part.Trim();
            if (!DriveRootRegex.IsMatch(normalized))
            {
                return false;
            }

            driveRoot = normalized.ToUpperInvariant();
            return true;
        }

        private static bool IsKnownPlaceholderToken(string token)
        {
            return string.Equals(token, "UserProfile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Documents", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "DocumentsMyGames", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "SavedGames", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "AppDataRoaming", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "AppDataLocal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "AppDataLocalLow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "ProgramData", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "SteamUserData", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "UserName", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseProcessToken(string token, out string processName)
        {
            processName = string.Empty;
            if (string.IsNullOrWhiteSpace(token)
                || !token.StartsWith("Process:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rawProcessName = token["Process:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(rawProcessName)
                || rawProcessName.Contains("..", StringComparison.Ordinal)
                || rawProcessName.Contains('\\')
                || rawProcessName.Contains('/')
                || rawProcessName.Contains(':'))
            {
                return false;
            }

            processName = Path.GetFileName(rawProcessName);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                processName += ".exe";
            }

            return true;
        }

        private static bool IsValidFileMatchToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            if (!string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal))
            {
                return false;
            }

            if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            return normalized.IndexOfAny(new[] { '*', '?', '\\', '/' }) < 0;
        }

        private static bool ExistsInDirectoryTree(string rootPath, string name, bool isDirectory, int maxDepth)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return EnumeratePathsWithinDepth(rootPath, maxDepth, isDirectory)
                .Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> EnumerateFilesWithinDepth(string rootPath, int maxDepth)
        {
            return EnumeratePathsWithinDepth(rootPath, maxDepth, isDirectory: false);
        }

        private static IEnumerable<string> EnumeratePathsWithinDepth(string rootPath, int maxDepth, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                yield break;
            }

            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((rootPath, 0));

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                IEnumerable<string> entries;
                try
                {
                    entries = isDirectory
                        ? Directory.EnumerateDirectories(current.Path, "*", SearchOption.TopDirectoryOnly)
                        : Directory.EnumerateFiles(current.Path, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    yield return entry;
                }

                if (current.Depth >= maxDepth)
                {
                    continue;
                }

                IEnumerable<string> childDirectories;
                try
                {
                    childDirectories = Directory.EnumerateDirectories(current.Path, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var childDirectory in childDirectories)
                {
                    pending.Enqueue((childDirectory, current.Depth + 1));
                }
            }
        }

        private static IReadOnlyList<string> GetSteamUserDataRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfExists(string? baseRoot)
            {
                if (string.IsNullOrWhiteSpace(baseRoot))
                {
                    return;
                }

                var userDataRoot = Path.Combine(baseRoot, "userdata");
                if (Directory.Exists(userDataRoot))
                {
                    roots.Add(userDataRoot);
                }
            }

            AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
            AddIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"));

            foreach (var steamRoot in GetSteamInstallRootsFromLibraryFolders())
            {
                AddIfExists(steamRoot);
            }

            return roots.ToList();
        }

        private static IEnumerable<string> GetSteamInstallRootsFromLibraryFolders()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "libraryfolders.vdf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "libraryfolders.vdf"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "steamapps", "libraryfolders.vdf")
            };

            foreach (var filePath in candidates.Where(File.Exists))
            {
                string content;
                try
                {
                    content = File.ReadAllText(filePath);
                }
                catch
                {
                    continue;
                }

                foreach (Match match in Regex.Matches(content, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var rawPath = match.Groups[1].Value
                        .Replace(@"\\", @"\")
                        .Trim();
                    if (!string.IsNullOrWhiteSpace(rawPath))
                    {
                        yield return rawPath;
                    }
                }
            }
        }

    }
}
