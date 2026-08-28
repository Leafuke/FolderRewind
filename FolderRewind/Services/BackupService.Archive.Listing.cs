using FolderRewind.Models;
using FolderRewind.History.Capture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        /// <summary>
        /// Deep-verifies that the archive's logical file listing exactly matches the captured managed-scope state.
        /// File names and lengths must agree in both directions; unsafe or duplicate archive paths are rejected.
        /// </summary>
        private static async Task<bool> ValidateArchiveLogicalStateAsync(
            string sevenZipExe,
            string archivePath,
            string? password,
            IReadOnlyDictionary<string, SourceCaptureFileState> expectedStates)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = sevenZipExe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };
                string? workingDirectory = Path.GetDirectoryName(archivePath);
                if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
                startInfo.ArgumentList.Add("l");
                startInfo.ArgumentList.Add("-slt");
                startInfo.ArgumentList.Add("-sccUTF-8");
                startInfo.ArgumentList.Add(archivePath);
                if (!string.IsNullOrWhiteSpace(password)) startInfo.ArgumentList.Add($"-p{password}");
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start()) return false;
                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string output = await standardOutput.ConfigureAwait(false);
                string error = await standardError.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    Log($"[Rolling] 7z listing failed: {error}", LogLevel.Warning);
                    return false;
                }

                if (!SevenZipArchiveListingParser.TryParse(output, out var entries)) return false;
                var expectedSizes = expectedStates.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Size,
                    StringComparer.OrdinalIgnoreCase);
                return ArchiveLogicalStateVerifier.Matches(expectedSizes, entries);
            }
            catch (Exception ex)
            {
                Log($"[Rolling] Archive listing verification failed: {ex.Message}", LogLevel.Warning);
                return false;
            }
        }
    }
}
