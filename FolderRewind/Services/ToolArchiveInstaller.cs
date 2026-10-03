using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace FolderRewind.Services;

public static class ToolArchiveInstaller
{
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    public const long MaximumExtractedBytes = 512L * 1024 * 1024;
    public static string ExtractVerified(byte[] bytes, string sha256, string staging, string executableName)
    {
        if (bytes.LongLength > MaximumArchiveBytes || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)
            || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha256))
            throw new InvalidDataException("Tool archive checksum or size verification failed.");
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var memory = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        long total = 0;
        if (archive.Entries.Count > 2048) throw new InvalidDataException("Tool archive has too many entries.");
        foreach (var entry in archive.Entries)
        {
            total = checked(total + entry.Length);
            var candidate = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (total > MaximumExtractedBytes || !candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Split(['/', '\\']).Any(part => part == "..") || entry.FullName.Contains(':')
                || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Tool archive contains an unsafe entry.");
        }
        Directory.CreateDirectory(staging);
        long written = 0;
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew);
            var buffer = new byte[65536];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                written = checked(written + count);
                if (written > MaximumExtractedBytes) throw new InvalidDataException("Extracted tool archive exceeds its size bound.");
                output.Write(buffer, 0, count);
            }
        }
        var executables = Directory.GetFiles(staging, executableName, SearchOption.AllDirectories);
        if (executables.Length != 1) throw new InvalidDataException("Tool executable entry is ambiguous or missing.");
        return executables[0];
    }
}
