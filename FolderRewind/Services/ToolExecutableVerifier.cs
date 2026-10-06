using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class ToolExecutableVerifier
{
    public static async Task<string> VerifyAsync(string path, string tool, CancellationToken token)
    {
        if (tool is not ("rclone" or "openlist")) throw new ArgumentOutOfRangeException(nameof(tool));
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(I18n.GetString("Onboarding_ToolExecutableInvalid"));
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(path)!
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("version");
        var output = await RcloneConnectionService.RunAsync(start, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output) || (tool == "rclone" && !output.StartsWith("rclone v", StringComparison.OrdinalIgnoreCase))
            || (tool == "openlist" && !output.Contains("Version", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(I18n.GetString("Onboarding_ToolVersionUnconfirmed"));
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
    }

    public static string HashExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
