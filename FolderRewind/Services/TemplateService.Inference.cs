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
        private static ObservableCollection<TemplatePathRule> InferPathRules(BackupConfig sourceConfig)
        {
            var rules = new ObservableCollection<TemplatePathRule>();
            if (sourceConfig?.SourceFolders == null)
            {
                return rules;
            }

            var currentUserName = Environment.UserName;
            var userProfileName = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            foreach (var folder in sourceConfig.SourceFolders)
            {
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
                {
                    continue;
                }

                foreach (var rule in BuildRules(folder, currentUserName, userProfileName))
                {
                    if (rule == null)
                    {
                        continue;
                    }

                    // 去重按 DisplayPath 做，避免同一路径被不同推断分支重复塞入。
                    if (rules.Any(existing => string.Equals(existing.DisplayPath, rule.DisplayPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    rules.Add(rule);
                }
            }

            return rules;
        }

        private static IEnumerable<TemplatePathRule> BuildRules(ManagedFolder folder, string currentUserName, string userProfileName)
        {
            if (!TryGetSegmentsWithRootPlaceholder(folder.Path, out var segments, out _))
            {
                yield break;
            }

            const double defaultConfidence = 0.8;
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                if (segment.Type != TemplatePathSegmentType.Static)
                {
                    continue;
                }

                if (string.Equals(segment.Value, currentUserName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment.Value, userProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    segment.Type = TemplatePathSegmentType.Placeholder;
                    segment.Value = "UserName";
                    continue;
                }

                if (IsDynamicCandidate(segment.Value))
                {
                    segment.Type = TemplatePathSegmentType.EnumerateDirectory;
                    segment.Value = "UserIdCandidate";
                }
            }
            var markers = BuildMarkers(folder.Path);

            var wildcardSegments = BuildSiblingWildcardSegments(folder.Path);
            if (wildcardSegments != null)
            {
                // “集合规则”用于匹配同层级多个存档目录（比如多个世界/角色）。
                yield return new TemplatePathRule
                {
                    Name = BuildCollectionRuleName(FolderNameConflictService.ResolveDisplayName(folder)),
                    Segments = wildcardSegments,
                    Markers = markers,
                    Confidence = defaultConfidence,
                    AutoAdd = true
                };
            }

            yield return new TemplatePathRule
            {
                Name = FolderNameConflictService.ResolveDisplayName(folder),
                Segments = segments,
                Markers = markers,
                Confidence = defaultConfidence,
                AutoAdd = true
            };

            var relaxedSegments = CloneSegments(segments);
            var relaxedChanged = false;
            for (int i = 0; i < relaxedSegments.Count; i++)
            {
                var segment = relaxedSegments[i];
                if (segment.Type != TemplatePathSegmentType.Static)
                {
                    continue;
                }

                if (LooksLikeVariableDirectory(segment.Value))
                {
                    segment.Type = TemplatePathSegmentType.EnumerateDirectory;
                    segment.Value = "VariableDirectory";
                    relaxedChanged = true;
                }
            }

            if (relaxedChanged)
            {
                // 放一个低置信度兜底规则，留给用户手动勾选，不抢默认选择。
                yield return new TemplatePathRule
                {
                    Name = FolderNameConflictService.ResolveDisplayName(folder),
                    Segments = relaxedSegments,
                    Markers = markers,
                    Confidence = defaultConfidence,
                    AutoAdd = false
                };
            }
        }

        private static ObservableCollection<TemplatePathSegment>? BuildSiblingWildcardSegments(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return null;
            }

            string? parentPath;
            string? folderName;
            try
            {
                parentPath = Directory.GetParent(folderPath)?.FullName;
                folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(folderName)
                || !Directory.Exists(parentPath))
            {
                return null;
            }

            if (!TryGetSegmentsWithRootPlaceholder(parentPath, out var parentSegments, out var parentConfidence))
            {
                return null;
            }

            var siblingNames = GetChildDirectoryNames(parentPath);
            if (!ShouldInferWildcardCollection(parentPath, folderName, siblingNames))
            {
                return null;
            }

            parentSegments.Add(new TemplatePathSegment
            {
                Type = TemplatePathSegmentType.EnumerateDirectory,
                Value = string.Empty
            });

            return parentConfidence >= 0.6 ? parentSegments : null;
        }

        private static List<string> GetChildDirectoryNames(string parentPath)
        {
            try
            {
                return Directory.EnumerateDirectories(parentPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static bool ShouldInferWildcardCollection(string parentPath, string folderName, IReadOnlyList<string> siblingNames)
        {
            if (siblingNames.Count < 2)
            {
                return false;
            }

            var parentName = Path.GetFileName(parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (LooksLikeCollectionDirectory(parentName))
            {
                return true;
            }

            var normalizedNames = siblingNames
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedNames.Count < 2)
            {
                return false;
            }

            var variableCount = normalizedNames.Count(name => LooksLikeVariableDirectory(name) || LooksLikeWorldOrProfileName(name));
            if (variableCount >= 2)
            {
                return true;
            }

            return LooksLikeWorldOrProfileName(folderName) && normalizedNames.Count >= 3;
        }

        private static bool LooksLikeCollectionDirectory(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("saves", StringComparison.OrdinalIgnoreCase)
                || value.Equals("savegames", StringComparison.OrdinalIgnoreCase)
                || value.Equals("userdata", StringComparison.OrdinalIgnoreCase)
                || value.Equals("profiles", StringComparison.OrdinalIgnoreCase)
                || value.Equals("worlds", StringComparison.OrdinalIgnoreCase)
                || value.Equals("characters", StringComparison.OrdinalIgnoreCase)
                || value.Equals("games", StringComparison.OrdinalIgnoreCase)
                || value.Equals("slots", StringComparison.OrdinalIgnoreCase)
                || value.Equals("remote", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeWorldOrProfileName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (LooksLikeCollectionDirectory(value) || IsDynamicCandidate(value))
            {
                return true;
            }

            return value.Any(char.IsLetter)
                && !value.Contains('.', StringComparison.Ordinal)
                && !value.Contains("config", StringComparison.OrdinalIgnoreCase)
                && !value.Contains("cache", StringComparison.OrdinalIgnoreCase)
                && !value.Contains("temp", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCollectionRuleName(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return I18n.GetString("Template_Preview_UnnamedRule");
            }

            return I18n.Format("Template_PathRule_CollectionName", baseName);
        }

        private static bool TryGetSegmentsWithRootPlaceholder(
            string path,
            out ObservableCollection<TemplatePathSegment> segments,
            out double confidence)
        {
            segments = new ObservableCollection<TemplatePathSegment>();
            confidence = 0.0;

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            var roots = GetKnownRoots()
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .OrderByDescending(r => r.Path.Length)
                .ToList();

            foreach (var root in roots)
            {
                if (!normalized.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalized.Length > root.Path.Length)
                {
                    var boundary = normalized[root.Path.Length];
                    if (boundary != Path.DirectorySeparatorChar && boundary != Path.AltDirectorySeparatorChar)
                    {
                        continue;
                    }
                }

                segments.Add(new TemplatePathSegment
                {
                    Type = TemplatePathSegmentType.Placeholder,
                    Value = root.Token
                });

                var relative = normalized.Substring(root.Path.Length)
                    .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrWhiteSpace(relative))
                {
                    foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        segments.Add(new TemplatePathSegment
                        {
                            Type = TemplatePathSegmentType.Static,
                            Value = segment
                        });
                    }
                }

                confidence = 0.6;
                return true;
            }

            return false;
        }

        private static ObservableCollection<TemplatePathMarker> BuildMarkers(string folderPath)
        {
            var markers = new ObservableCollection<TemplatePathMarker>();
            try
            {
                var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fileName in KnownMarkerFiles)
                {
                    if (ExistsInDirectoryTree(folderPath, fileName, isDirectory: false, MarkerSearchDepth)
                        && added.Add("F:" + fileName))
                    {
                        markers.Add(new TemplatePathMarker
                        {
                            Type = TemplatePathMarkerType.RequiredFile,
                            Value = fileName
                        });
                    }
                }

                foreach (var dirName in KnownMarkerDirectories)
                {
                    if (ExistsInDirectoryTree(folderPath, dirName, isDirectory: true, MarkerSearchDepth)
                        && added.Add("D:" + dirName))
                    {
                        markers.Add(new TemplatePathMarker
                        {
                            Type = TemplatePathMarkerType.OptionalDirectory,
                            Value = dirName
                        });
                    }
                }

                if (markers.Count == 0)
                {
                    var candidateFile = EnumerateFilesWithinDepth(folderPath, MarkerSearchDepth)
                        .Select(Path.GetFileName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && IsLikelyStableFileName(name));

                    if (!string.IsNullOrWhiteSpace(candidateFile))
                    {
                        markers.Add(new TemplatePathMarker
                        {
                            Type = TemplatePathMarkerType.OptionalFile,
                            Value = candidateFile
                        });
                    }
                }
            }
            catch
            {
                // 标记提取失败不阻断规则创建。
            }

            return markers;
        }

        private static bool IsLikelyStableFileName(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return fileName.Length <= 40;
            }

            return extension.Equals(".sav", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ini", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase);
        }

    }
}
