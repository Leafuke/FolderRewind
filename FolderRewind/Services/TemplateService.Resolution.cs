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
        private static IReadOnlyList<string> ResolveRulePaths(TemplatePathRule rule)
        {
            if (rule == null || rule.Segments == null || rule.Segments.Count == 0)
            {
                return Array.Empty<string>();
            }

            var current = new List<string> { string.Empty };
            // 规则允许枚举目录，必须限流，避免在超大目录树里无限扩散。
            var scannedDirectories = 0;

            for (int i = 0; i < rule.Segments.Count; i++)
            {
                var segment = rule.Segments[i];
                if (segment == null)
                {
                    continue;
                }

                var next = new List<string>();

                switch (segment.Type)
                {
                    case TemplatePathSegmentType.RootPath:
                        next.AddRange(ResolveRootPath(segment.Value));
                        break;

                    case TemplatePathSegmentType.Placeholder:
                        var placeholders = ResolvePlaceholderPaths(segment.Value);
                        if (placeholders.Count == 0)
                        {
                            return Array.Empty<string>();
                        }

                        foreach (var basePath in current)
                        {
                            foreach (var placeholder in placeholders)
                            {
                                var combined = CombinePathSegment(basePath, placeholder);
                                if (Directory.Exists(combined))
                                {
                                    next.Add(combined);
                                }
                            }
                        }
                        break;

                    case TemplatePathSegmentType.ProcessDirectory:
                        var processDirectories = ProcessPathService.GetRunningProcessDirectories(segment.Value);
                        if (processDirectories.Count == 0)
                        {
                            return Array.Empty<string>();
                        }

                        foreach (var basePath in current)
                        {
                            foreach (var processDirectory in processDirectories)
                            {
                                var combined = CombinePathSegment(basePath, processDirectory);
                                if (Directory.Exists(combined))
                                {
                                    next.Add(combined);
                                }
                            }
                        }
                        break;

                    case TemplatePathSegmentType.EnumerateDirectory:
                        foreach (var basePath in current)
                        {
                            if (string.IsNullOrWhiteSpace(basePath))
                            {
                                foreach (var root in GetReadyDriveRoots())
                                {
                                    next.Add(root);
                                }

                                continue;
                            }

                            if (!Directory.Exists(basePath))
                            {
                                continue;
                            }

                            IEnumerable<string> dirs;
                            try
                            {
                                dirs = Directory.EnumerateDirectories(basePath, "*", SearchOption.TopDirectoryOnly);
                            }
                            catch
                            {
                                continue;
                            }

                            foreach (var dir in dirs)
                            {
                                scannedDirectories++;
                                if (scannedDirectories > ScanDirectoryLimit)
                                {
                                    break;
                                }

                                if (GetRelativeDepth(basePath, dir) > ScanDepthLimit)
                                {
                                    continue;
                                }

                                next.Add(dir);
                            }

                            if (scannedDirectories > ScanDirectoryLimit)
                            {
                                break;
                            }
                        }
                        break;

                    default:
                        foreach (var basePath in current)
                        {
                            var combined = CombinePathSegment(basePath, segment.Value);
                            if (Directory.Exists(combined))
                            {
                                next.Add(combined);
                            }
                        }
                        break;
                }

                current = next
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (current.Count == 0)
                {
                    return Array.Empty<string>();
                }
            }

            return current
                .Where(path => ValidateMarkers(path, rule.Markers))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool ValidateMarkers(string path, IEnumerable<TemplatePathMarker>? markers)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (markers == null)
            {
                return true;
            }

            foreach (var marker in markers)
            {
                if (marker == null || string.IsNullOrWhiteSpace(marker.Value))
                {
                    continue;
                }

                var safeName = Path.GetFileName(marker.Value);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    continue;
                }

                var matched = marker.Type switch
                {
                    TemplatePathMarkerType.RequiredDirectory => ExistsInDirectoryTree(path, safeName, isDirectory: true, MarkerSearchDepth),
                    TemplatePathMarkerType.RequiredFile => ExistsInDirectoryTree(path, safeName, isDirectory: false, MarkerSearchDepth),
                    TemplatePathMarkerType.OptionalDirectory => true,
                    TemplatePathMarkerType.OptionalFile => true,
                    _ => true
                };

                if (!matched)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool UpdateTemplatePathRules(
            string templateId,
            IEnumerable<TemplateRuleEditItem>? items,
            out string message)
        {
            message = string.Empty;
            var template = GetTemplateById(templateId);
            if (template == null)
            {
                message = I18n.GetString("Template_Update_TemplateNotFound");
                return false;
            }

            var rules = new ObservableCollection<TemplatePathRule>();
            foreach (var item in items ?? Array.Empty<TemplateRuleEditItem>())
            {
                if (string.IsNullOrWhiteSpace(item.DisplayPath))
                {
                    continue;
                }

                if (!TryParseDisplayPath(item.DisplayPath, out var segments))
                {
                    message = I18n.Format("Template_Manager_PathRuleInvalid", item.DisplayPath);
                    return false;
                }

                var existingRule = template.PathRules?
                    .FirstOrDefault(rule => string.Equals(rule.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                if (!TryBuildRuleMarkers(item.FileMatchList, out var requiredFileMarkers, out var invalidFileMatch))
                {
                    message = I18n.Format("Template_Manager_PathRuleFileMatchInvalid", invalidFileMatch ?? string.Empty);
                    return false;
                }

                var mergedMarkers = new ObservableCollection<TemplatePathMarker>(
                    (existingRule?.Markers?.AsEnumerable() ?? Array.Empty<TemplatePathMarker>())
                        .Where(marker => marker != null && marker.Type != TemplatePathMarkerType.RequiredFile)
                        .Select(CloneMarker));
                foreach (var marker in requiredFileMarkers)
                {
                    mergedMarkers.Add(marker);
                }

                rules.Add(new TemplatePathRule
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                    Name = item.Name?.Trim() ?? string.Empty,
                    Segments = segments,
                    Markers = mergedMarkers,
                    Confidence = Math.Clamp(item.Confidence, 0.0, 1.0),
                    AutoAdd = item.AutoAdd
                });
            }

            var validationErrors = ValidatePathRules(rules);
            if (validationErrors.Count > 0)
            {
                message = string.Join(Environment.NewLine, validationErrors);
                return false;
            }

            template.PathRules = rules;
            template.UpdatedUtc = DateTime.UtcNow;
            ConfigService.Save();
            message = I18n.GetString("Template_Manager_PathRulesUpdated");
            return true;
        }

        private static bool TryBuildRuleMarkers(
            string? fileMatchList,
            out ObservableCollection<TemplatePathMarker> markers,
            out string? invalidFileMatch)
        {
            markers = new ObservableCollection<TemplatePathMarker>();
            invalidFileMatch = null;

            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPart in (fileMatchList ?? string.Empty).Split(';', StringSplitOptions.TrimEntries))
            {
                var value = rawPart?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!IsValidFileMatchToken(value))
                {
                    invalidFileMatch = value;
                    return false;
                }

                if (!added.Add(value))
                {
                    continue;
                }

                markers.Add(new TemplatePathMarker
                {
                    Type = TemplatePathMarkerType.RequiredFile,
                    Value = value
                });
            }

            return true;
        }

        private static IReadOnlyList<string> ResolvePlaceholderPaths(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Array.Empty<string>();
            }

            var normalizedToken = token.Trim();
            if (string.Equals(normalizedToken, "UserProfile", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            if (string.Equals(normalizedToken, "Documents", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            }

            if (string.Equals(normalizedToken, "DocumentsMyGames", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"));
            }

            if (string.Equals(normalizedToken, "SavedGames", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"));
            }

            if (string.Equals(normalizedToken, "AppDataRoaming", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            }

            if (string.Equals(normalizedToken, "AppDataLocal", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            }

            if (string.Equals(normalizedToken, "AppDataLocalLow", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"));
            }

            if (string.Equals(normalizedToken, "ProgramData", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            }

            if (string.Equals(normalizedToken, "SteamUserData", StringComparison.OrdinalIgnoreCase))
            {
                return GetSteamUserDataRoots().ToList();
            }

            if (string.Equals(normalizedToken, "UserName", StringComparison.OrdinalIgnoreCase))
            {
                return SinglePath(Environment.UserName);
            }

            return Array.Empty<string>();
        }

        private static IReadOnlyList<string> SinglePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? Array.Empty<string>()
                : new[] { path };
        }

        private static string CombinePathSegment(string? basePath, string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return basePath ?? string.Empty;
            }

            if (Path.IsPathRooted(segment))
            {
                return segment;
            }

            if (string.IsNullOrWhiteSpace(basePath))
            {
                return segment;
            }

            return Path.Combine(basePath, segment);
        }

        private static IReadOnlyList<string> ResolveRootPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            var normalized = value.Trim();
            if (!DriveRootRegex.IsMatch(normalized))
            {
                return Array.Empty<string>();
            }

            var root = normalized.ToUpperInvariant() + Path.DirectorySeparatorChar;
            return Directory.Exists(root)
                ? new[] { root }
                : Array.Empty<string>();
        }

        private static IReadOnlyList<string> GetReadyDriveRoots()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(drive => drive.IsReady)
                    .Select(drive => drive.RootDirectory.FullName)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static int GetRelativeDepth(string parent, string child)
        {
            try
            {
                var relative = Path.GetRelativePath(parent, child);
                if (string.IsNullOrWhiteSpace(relative) || relative == ".")
                {
                    return 0;
                }

                return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private static bool IsDynamicCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return SidRegex.IsMatch(value)
                || GuidRegex.IsMatch(value)
                || LongDigitsRegex.IsMatch(value)
                || LongHexRegex.IsMatch(value);
        }

        private static bool LooksLikeVariableDirectory(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (IsDynamicCandidate(value))
            {
                return true;
            }

            return value.Contains("user", StringComparison.OrdinalIgnoreCase)
                || value.Contains("profile", StringComparison.OrdinalIgnoreCase)
                || value.Contains("account", StringComparison.OrdinalIgnoreCase)
                || value.Contains("player", StringComparison.OrdinalIgnoreCase)
                || value.Contains("slot", StringComparison.OrdinalIgnoreCase)
                || value.Contains("save", StringComparison.OrdinalIgnoreCase) && value.Any(char.IsDigit);
        }

    }
}
