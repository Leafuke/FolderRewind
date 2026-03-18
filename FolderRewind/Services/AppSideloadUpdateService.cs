using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class AppSideloadUpdateService
    {
        private const string Mirror1Prefix = "https://gh-proxy.com/";
        private const string Mirror2Prefix = "https://hk.gh-proxy.org/";

        private enum DownloadSourceOption
        {
            Official = 0,
            Mirror1 = 1,
            Mirror2 = 2,
            Custom = 3
        }

        private static readonly HttpClient Http = CreateClient();

        private sealed class DownloadSourceCandidate
        {
            public required string DisplayName { get; init; }
            public required string PackageUrl { get; init; }
            public required string Sha256Url { get; init; }
        }

        internal sealed class PrepareUpdateResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public string SourceDisplayName { get; set; } = string.Empty;
            public string WorkingDirectory { get; set; } = string.Empty;
            public string InstallScriptPath { get; set; } = string.Empty;
        }

        public static async Task<PrepareUpdateResult> PrepareUpdateAsync(AppUpdateService.UpdateCheckResult update, CancellationToken ct = default)
        {
            if (update == null)
            {
                return Fail(I18n.GetString("Update_Prepare_InvalidContext"));
            }

            if (string.IsNullOrWhiteSpace(update.PackageAssetName)
                || string.IsNullOrWhiteSpace(update.PackageDownloadUrl)
                || string.IsNullOrWhiteSpace(update.PackageSha256Url))
            {
                return Fail(I18n.GetString("Update_Prepare_MissingAssets"));
            }

            var sources = BuildDownloadSourceCandidates(update.PackageDownloadUrl, update.PackageSha256Url);
            if (sources.Count == 0)
            {
                return Fail(I18n.GetString("Update_Prepare_NoDownloadSource"));
            }

            var workingRoot = Path.Combine(
                Path.GetTempPath(),
                "FolderRewind",
                "AppUpdate",
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{SanitizePathPart(update.LatestVersion)}");

            Directory.CreateDirectory(workingRoot);

            var packagePath = Path.Combine(workingRoot, update.PackageAssetName);
            var shaPath = packagePath + ".sha256";
            var extractDir = Path.Combine(workingRoot, "extracted");

            string lastError = string.Empty;
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    TryDeleteFile(packagePath);
                    TryDeleteFile(shaPath);
                    TryDeleteDirectory(extractDir);
                    Directory.CreateDirectory(extractDir);

                    await DownloadFileAsync(source.PackageUrl, packagePath, ct);
                    await DownloadFileAsync(source.Sha256Url, shaPath, ct);

                    var expected = await ParseExpectedSha256Async(shaPath, ct);
                    var actual = await ComputeFileSha256Async(packagePath, ct);
                    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = I18n.Format("Update_Prepare_HashMismatch", source.DisplayName);
                        LogService.LogWarning(lastError, nameof(AppSideloadUpdateService));
                        continue;
                    }

                    var extractResult = await ExtractArchiveAsync(packagePath, extractDir, ct);
                    if (!extractResult.Success)
                    {
                        lastError = I18n.Format("Update_Prepare_ExtractFailedWithSource", source.DisplayName, extractResult.ErrorMessage);
                        LogService.LogWarning(lastError, nameof(AppSideloadUpdateService));
                        continue;
                    }

                    var installScript = FindInstallScript(extractDir);
                    if (string.IsNullOrWhiteSpace(installScript))
                    {
                        return Fail(I18n.GetString("Update_Prepare_InstallScriptMissing"));
                    }

                    return new PrepareUpdateResult
                    {
                        Success = true,
                        SourceDisplayName = source.DisplayName,
                        WorkingDirectory = extractDir,
                        InstallScriptPath = installScript
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = I18n.Format("Update_Prepare_DownloadFailedWithSource", source.DisplayName, ex.Message);
                    LogService.LogWarning(lastError, nameof(AppSideloadUpdateService));
                }
            }

            if (string.IsNullOrWhiteSpace(lastError))
            {
                lastError = I18n.GetString("Update_Prepare_FailedGeneric");
            }

            return Fail(lastError);
        }

        private static PrepareUpdateResult Fail(string message)
        {
            return new PrepareUpdateResult
            {
                Success = false,
                ErrorMessage = message ?? string.Empty
            };
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FolderRewind", "1.0"));
            client.Timeout = TimeSpan.FromMinutes(5);
            return client;
        }

        private static IReadOnlyList<DownloadSourceCandidate> BuildDownloadSourceCandidates(string rawPackageUrl, string rawSha256Url)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings;
            var preferred = NormalizeSourceOption(settings?.AppUpdatePreferredSource ?? (int)DownloadSourceOption.Official);
            var autoFallback = settings?.AppUpdateAutoFallback ?? true;
            var customMirror = settings?.AppUpdateCustomMirrorUrl ?? string.Empty;

            var order = new List<DownloadSourceOption> { preferred };
            if (autoFallback)
            {
                foreach (DownloadSourceOption option in Enum.GetValues(typeof(DownloadSourceOption)))
                {
                    if (option != preferred)
                    {
                        order.Add(option);
                    }
                }
            }

            var candidates = new List<DownloadSourceCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in order)
            {
                var candidate = TryCreateSourceCandidate(source, rawPackageUrl, rawSha256Url, customMirror);
                if (candidate == null)
                {
                    continue;
                }

                var dedupeKey = candidate.PackageUrl + "\n" + candidate.Sha256Url;
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static DownloadSourceOption NormalizeSourceOption(int rawValue)
        {
            return Enum.IsDefined(typeof(DownloadSourceOption), rawValue)
                ? (DownloadSourceOption)rawValue
                : DownloadSourceOption.Official;
        }

        private static DownloadSourceCandidate? TryCreateSourceCandidate(DownloadSourceOption source, string rawPackageUrl, string rawSha256Url, string customMirror)
        {
            return source switch
            {
                DownloadSourceOption.Official => new DownloadSourceCandidate
                {
                    DisplayName = I18n.GetString("Update_Source_Official"),
                    PackageUrl = rawPackageUrl,
                    Sha256Url = rawSha256Url
                },
                DownloadSourceOption.Mirror1 => new DownloadSourceCandidate
                {
                    DisplayName = I18n.GetString("Update_Source_Mirror1"),
                    PackageUrl = RewriteByPrefix(Mirror1Prefix, rawPackageUrl),
                    Sha256Url = RewriteByPrefix(Mirror1Prefix, rawSha256Url)
                },
                DownloadSourceOption.Mirror2 => new DownloadSourceCandidate
                {
                    DisplayName = I18n.GetString("Update_Source_Mirror2"),
                    PackageUrl = RewriteByPrefix(Mirror2Prefix, rawPackageUrl),
                    Sha256Url = RewriteByPrefix(Mirror2Prefix, rawSha256Url)
                },
                DownloadSourceOption.Custom => BuildCustomSource(rawPackageUrl, rawSha256Url, customMirror),
                _ => null
            };
        }

        private static DownloadSourceCandidate? BuildCustomSource(string rawPackageUrl, string rawSha256Url, string customMirror)
        {
            if (string.IsNullOrWhiteSpace(customMirror))
            {
                return null;
            }

            return new DownloadSourceCandidate
            {
                DisplayName = I18n.GetString("Update_Source_Custom"),
                PackageUrl = RewriteByCustom(customMirror, rawPackageUrl),
                Sha256Url = RewriteByCustom(customMirror, rawSha256Url)
            };
        }

        private static string RewriteByPrefix(string prefix, string rawUrl)
        {
            var normalized = prefix?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return rawUrl;
            }

            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return normalized + rawUrl;
        }

        private static string RewriteByCustom(string customMirror, string rawUrl)
        {
            var normalized = customMirror.Trim();
            if (normalized.IndexOf("{url}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Regex.Replace(
                    normalized,
                    "\\{url\\}",
                    rawUrl,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            return normalized + rawUrl;
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(target, ct);
        }

        private static async Task<string> ParseExpectedSha256Async(string sha256Path, CancellationToken ct)
        {
            var text = await File.ReadAllTextAsync(sha256Path, ct);
            var match = Regex.Match(text, @"\b[a-fA-F0-9]{64}\b", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                throw new InvalidDataException(I18n.GetString("Update_Prepare_InvalidShaFile"));
            }

            return match.Value.ToUpperInvariant();
        }

        private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var sha256 = SHA256.Create();
            var hash = await sha256.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash);
        }

        private static async Task<(bool Success, string ErrorMessage)> ExtractArchiveAsync(string archivePath, string extractDir, CancellationToken ct)
        {
            var sevenZipExe = ResolveSevenZipExecutable();
            if (string.IsNullOrWhiteSpace(sevenZipExe))
            {
                return (false, I18n.GetString("BackupService_Log_SevenZipNotFound"));
            }

            var arguments = $"x \"{archivePath}\" -o\"{extractDir}\" -y -aoa";
            var startInfo = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(archivePath) ?? AppContext.BaseDirectory
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                if (!process.Start())
                {
                    return (false, I18n.GetString("Update_Prepare_ExtractStartFailed"));
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(ct);

                var stderr = await stderrTask;
                if (process.ExitCode == 0)
                {
                    return (true, string.Empty);
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    return (false, GetLastMeaningfulLine(stderr));
                }

                var stdout = await stdoutTask;
                return (false, string.IsNullOrWhiteSpace(stdout)
                    ? I18n.Format("CloudSync_Error_ExitCode", process.ExitCode)
                    : GetLastMeaningfulLine(stdout));
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }
        }

        private static string FindInstallScript(string extractDir)
        {
            return Directory
                .EnumerateFiles(extractDir, "Install.ps1", SearchOption.AllDirectories)
                .FirstOrDefault() ?? string.Empty;
        }

        private static string GetLastMeaningfulLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return lines.Length == 0 ? string.Empty : lines[^1];
        }

        private static string? ResolveSevenZipExecutable()
        {
            var candidates = new List<string>();
            var configPath = ConfigService.CurrentConfig?.GlobalSettings?.SevenZipPath;

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;

                try
                {
                    candidates.Add(Path.GetFullPath(path));
                }
                catch
                {
                    candidates.Add(path);
                }
            }

            AddCandidate(configPath);
            if (!string.IsNullOrWhiteSpace(configPath) && !Path.IsPathRooted(configPath))
            {
                AddCandidate(Path.Combine(AppContext.BaseDirectory, configPath));
            }

            string[] exeNames = { "7z.exe", "7zz.exe", "7za.exe" };
            foreach (var exe in exeNames)
            {
                AddCandidate(Path.Combine(AppContext.BaseDirectory, exe));

                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrWhiteSpace(pf))
                {
                    AddCandidate(Path.Combine(pf, "7-Zip", exe));
                }

                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(pf86))
                {
                    AddCandidate(Path.Combine(pf86, "7-Zip", exe));
                }
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = dir.Trim();
                    foreach (var exe in exeNames)
                    {
                        AddCandidate(Path.Combine(trimmed, exe));
                    }
                }
            }

            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        private static string SanitizePathPart(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(text) ? "unknown" : text;
        }
    }
}
