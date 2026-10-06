using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class OnboardingToolInstaller
{
    internal const string VerificationMarker = ".folderrewind-tool-verification";
    private static EnumerationOptions SearchOptions => new()
    {
        RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    internal static async Task<string> EnsureAsync(string tool, string configuredPath, string toolsRoot,
        Architecture osArchitecture, IEnumerable<string> systemCandidates,
        Func<OnboardingToolAsset, CancellationToken, Task<byte[]>> download,
        Func<string, string, CancellationToken, Task<string>> verify,
        Action<string>? progress, CancellationToken token,
        Func<string, Architecture, OnboardingToolAsset>? resolveAsset = null)
    {
        var toolRoot = Path.GetFullPath(Path.Combine(toolsRoot, tool));
        var executableName = tool + ".exe";
        var candidates = new[] { configuredPath }.Concat(ManagedCandidates(toolRoot, executableName)).Concat(systemCandidates);
        foreach (var candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
                var path = Path.GetFullPath(candidate);
                if (!IsReusable(path, toolRoot)) continue;
                await verify(path, tool, token).ConfigureAwait(false);
                progress?.Invoke(I18n.GetString("Onboarding_ComponentReused") + " " + path);
                return path;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // Unusable candidates are left untouched; continue discovery before downloading.
            }
        }

        var asset = (resolveAsset ?? OnboardingToolAssets.Get)(tool, osArchitecture);
        progress?.Invoke(I18n.GetString(tool == "rclone" ? "CloudOnboarding_Status_DownloadRclone" : "CloudOnboarding_Status_DownloadOpenList"));
        var bytes = await download(asset, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(toolRoot);
        var versionDirectory = Path.Combine(toolRoot, asset.Version);
        if (Directory.Exists(versionDirectory)) versionDirectory += "-" + Guid.NewGuid().ToString("N");
        var staging = versionDirectory + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            var executable = ToolArchiveInstaller.ExtractVerified(bytes, asset.Sha256, staging, executableName);
            await verify(executable, tool, token).ConfigureAwait(false);
            File.WriteAllLines(Path.Combine(staging, VerificationMarker),
                ["sha256:" + asset.Sha256, ToolExecutableVerifier.HashExecutable(executable)]);
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(staging, executable);
            Directory.Move(staging, versionDirectory);
            var installed = Path.Combine(versionDirectory, relative);
            progress?.Invoke(I18n.GetString("Onboarding_ToolPrepared") + " " + installed);
            return installed;
        }
        finally
        {
            // Only delete this operation's random staging directory, after verifying its absolute boundary.
            var absoluteStaging = Path.GetFullPath(staging);
            if (Directory.Exists(absoluteStaging)
                && absoluteStaging.StartsWith(toolRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && absoluteStaging.StartsWith(Path.GetFullPath(versionDirectory) + ".staging-", StringComparison.OrdinalIgnoreCase))
                Directory.Delete(absoluteStaging, recursive: true);
        }
    }

    private static IEnumerable<string> ManagedCandidates(string root, string executableName)
    {
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return [];
            return Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).Contains(".staging-", StringComparison.Ordinal)
                    && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(64)
                .SelectMany(d => Directory.EnumerateFiles(d, executableName, SearchOptions).Take(2)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static bool IsReusable(string path, string toolRoot)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var root = Path.GetFullPath(toolRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        if ((File.GetAttributes(toolRoot) & FileAttributes.ReparsePoint) != 0) return false;
        var relative = Path.GetRelativePath(root, path);
        var version = relative.Split(Path.DirectorySeparatorChar)[0];
        if (version.Contains(".staging-", StringComparison.Ordinal)) return false;
        var versionRoot = Path.Combine(root, version);
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
        var marker = Path.Combine(versionRoot, VerificationMarker);
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) return false;
        var fields = File.ReadAllLines(marker);
        return fields.Length == 2 && fields[0].StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            && fields[0].Length == 71 && fields[0][7..].All(char.IsAsciiHexDigit)
            && StringComparer.OrdinalIgnoreCase.Equals(fields[1], ToolExecutableVerifier.HashExecutable(path));
    }

    internal static IEnumerable<string> PathCandidates(string executableName, string? searchPath)
        => (searchPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(d => Path.Combine(d, executableName));
}
