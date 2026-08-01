using FolderRewind.Models;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        private static List<FileInfo> BuildReverseCompatibilityChain(DirectoryInfo backupDir, FileInfo targetFile, BackupConfig? config = null, string? folderName = null)
        {
            if (!backupDir.Exists)
            {
                return new List<FileInfo>();
            }

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };

            var candidates = backupDir
                .EnumerateFiles("*", enumOptions)
                .Where(f => string.Equals(f.Extension, targetFile.Extension, StringComparison.OrdinalIgnoreCase))
                .Where(f => f.LastWriteTimeUtc >= targetFile.LastWriteTimeUtc
                    || string.Equals(f.FullName, targetFile.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name)
                .ToList();

            var chain = new List<FileInfo>();
            foreach (var candidate in candidates)
            {
                chain.Add(candidate);
                if (string.Equals(candidate.FullName, targetFile.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (!chain.Any(f => string.Equals(f.FullName, targetFile.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                chain.Add(targetFile);
            }

            return chain
                .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static void CleanupInternalRestoreMarkers(string targetDir)
        {
            try
            {
                string internalDir = Path.Combine(targetDir, InternalRestoreMarkerDirectoryName);
                if (Directory.Exists(internalDir))
                {
                    ClearReadonlyAttributes(internalDir);
                    Directory.Delete(internalDir, true);
                }
            }
            catch
            {
            }
        }

        private static void CopyRestoreWhitelistEntries(
            string sourceDir,
            string targetDir,
            PathRuleMatcher? whitelistMatcher,
            string? whitelistRootDir = null)
        {
            if (string.IsNullOrWhiteSpace(sourceDir)
                || string.IsNullOrWhiteSpace(targetDir)
                || whitelistMatcher == null)
            {
                return;
            }

            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            string effectiveWhitelistRootDir = string.IsNullOrWhiteSpace(whitelistRootDir) ? sourceDir : whitelistRootDir;

            foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories).OrderBy(d => d.Length))
            {
                if (!IsPathOrAncestorInRestoreWhitelist(
                    dir,
                    sourceDir,
                    effectiveWhitelistRootDir,
                    whitelistMatcher))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(sourceDir, dir);
                Directory.CreateDirectory(Path.Combine(targetDir, relPath));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (!IsPathOrAncestorInRestoreWhitelist(
                    file,
                    sourceDir,
                    effectiveWhitelistRootDir,
                    whitelistMatcher))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(sourceDir, file);
                string destFile = Path.Combine(targetDir, relPath);
                string? destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrWhiteSpace(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }
                File.Copy(file, destFile, true);
            }
        }

        private static bool IsPathOrAncestorInRestoreWhitelist(
            string entryPath,
            string rootDir,
            string whitelistRootDir,
            PathRuleMatcher whitelistMatcher)
        {
            if (IsInRestoreWhitelist(entryPath, rootDir, whitelistRootDir, whitelistMatcher))
            {
                return true;
            }

            string rootFullPath = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? current = Directory.Exists(entryPath) ? entryPath : Path.GetDirectoryName(entryPath);

            while (!string.IsNullOrWhiteSpace(current))
            {
                string currentFullPath = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(currentFullPath, rootFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (IsInRestoreWhitelist(currentFullPath, rootDir, whitelistRootDir, whitelistMatcher))
                {
                    return true;
                }

                current = Path.GetDirectoryName(currentFullPath);
            }

            return false;
        }

        private static void ClearReadonlyAttributes(string dir)
        {
            try
            {
                ClearReadonlyAttribute(dir);

                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    ClearReadonlyAttribute(file);
                }

                foreach (var childDir in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    ClearReadonlyAttribute(childDir);
                }
            }
            catch (Exception ex)
            {
                Log($"[Restore][Debug] Failed to enumerate paths while clearing readonly attributes: {ex.Message}", LogLevel.Debug);
            }
        }

        private static void ClearReadonlyAttribute(string path)
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                {
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception ex)
            {
                Log($"[Restore][Debug] Failed to clear readonly attribute: {path} - {ex.Message}", LogLevel.Debug);
            }
        }

        private static bool IsInRestoreWhitelist(
            string entryPath,
            string rootDir,
            string whitelistRootDir,
            PathRuleMatcher whitelistMatcher)
        {
            string comparisonEntryPath = GetRestoreWhitelistComparisonPath(
                entryPath,
                rootDir,
                whitelistRootDir);
            return whitelistMatcher.IsMatch(comparisonEntryPath);
        }

        private static string GetRestoreWhitelistComparisonPath(string entryPath, string physicalRootDir, string comparisonRootDir)
        {
            string entryFullPath = Path.GetFullPath(entryPath);
            string physicalRootFullPath = Path.GetFullPath(physicalRootDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string relativePath;
            try
            {
                relativePath = Path.GetRelativePath(physicalRootFullPath, entryFullPath);
            }
            catch (Exception ex)
            {
                Log($"[Filter][Debug] Failed to compute restore whitelist comparison path: {ex.Message}", LogLevel.Debug);
                return entryFullPath;
            }

            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                return entryFullPath;
            }

            return Path.GetFullPath(Path.Combine(comparisonRootDir, relativePath));
        }

    }
}
