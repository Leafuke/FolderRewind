using System.IO.Compression;
using System.Security.Cryptography;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Packaging;

public sealed record PluginPackageLimits(
    int MaximumEntries,
    long MaximumTotalBytes,
    long MaximumEntryBytes,
    double MaximumCompressionRatio,
    int MaximumManifestBytes)
{
    public static PluginPackageLimits Default { get; } = new(10_000, 1L << 30, 256L << 20, 100, 1 << 20);
}

public sealed record ValidatedPluginPackage(
    string PackagePath,
    string Sha256,
    ParsedPluginPackageManifest Manifest,
    IReadOnlyList<PluginPackageEntry> Entries);

public sealed record PluginPackageEntry(string CanonicalPath, long Length, long CompressedLength, bool IsDirectory);

public static class PluginPackageValidator
{
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    public static async ValueTask<ValidatedPluginPackage> ValidateAsync(
        string packagePath,
        string? expectedSha256 = null,
        PluginPackageLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var bounded = limits ?? PluginPackageLimits.Default;
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".frplugin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin package must be an existing .frplugin file.");
        var hash = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !StringComparer.OrdinalIgnoreCase.Equals(hash, expectedSha256.Trim()))
            throw new InvalidDataException("Plugin package SHA-256 does not match the trusted source.");

        using var archive = ZipFile.OpenRead(fullPath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > bounded.MaximumEntries)
            throw new InvalidDataException("Plugin package entry count is outside the allowed range.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<PluginPackageEntry>();
        long total = 0;
        ZipArchiveEntry? manifestEntry = null;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            var canonical = ValidateEntryPath(entry.FullName, isDirectory);
            if (!seen.Add(canonical)) throw new InvalidDataException("Plugin package contains a canonical or case-insensitive path collision.");
            RejectLink(entry);
            if (entry.Length > bounded.MaximumEntryBytes) throw new InvalidDataException("Plugin package entry exceeds the expanded size limit.");
            total = checked(total + entry.Length);
            if (total > bounded.MaximumTotalBytes) throw new InvalidDataException("Plugin package exceeds the total expanded size limit.");
            if (entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > bounded.MaximumCompressionRatio))
                throw new InvalidDataException("Plugin package entry exceeds the compression ratio limit.");
            if (string.Equals(canonical, "FolderRewind.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase)
                || canonical.EndsWith("/FolderRewind.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin package must not bundle FolderRewind.Plugin.Abstractions.dll.");
            if (string.Equals(canonical, "manifest.json", StringComparison.Ordinal)) manifestEntry = entry;
            entries.Add(new PluginPackageEntry(canonical, entry.Length, entry.CompressedLength, isDirectory));
        }
        if (manifestEntry is null) throw new InvalidDataException("Plugin package root must contain manifest.json.");
        if (manifestEntry.Length > bounded.MaximumManifestBytes) throw new InvalidDataException("Plugin manifest exceeds its size limit.");
        byte[] manifestBytes;
        await using (var stream = manifestEntry.Open())
        {
            using var memory = new MemoryStream((int)manifestEntry.Length);
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            manifestBytes = memory.ToArray();
        }
        var manifest = PluginPackageManifestReader.Parse(manifestBytes);
        PluginManifestContractValidator.ValidateStatic(manifest.Contract, manifest.Contract.PluginId);
        RequirePayloadEntry(entries, manifest.Contract.EntryAssembly, "entryAssembly");
        RequirePayloadEntry(entries, manifest.Contract.SettingsSchema, "settingsSchema");
        ValidateArchitecture(manifest.Architectures);
        ValidateHighImpactServices(manifest.Contract);
        return new ValidatedPluginPackage(fullPath, hash, manifest, entries);
    }

    public static async ValueTask ExtractAsync(
        ValidatedPluginPackage package,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(stagingDirectory);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Plugin staging path already exists.");
        Directory.CreateDirectory(root);
        try
        {
            using var archive = ZipFile.OpenRead(package.PackagePath);
            var byPath = archive.Entries.ToDictionary(
                value => ValidateEntryPath(value.FullName, value.FullName.EndsWith("/", StringComparison.Ordinal)),
                StringComparer.Ordinal);
            foreach (var fact in package.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveUnderRoot(root, fact.CanonicalPath);
                if (fact.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = byPath[fact.CanonicalPath].Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length != fact.Length) throw new InvalidDataException("Extracted plugin entry length changed.");
            }
        }
        catch
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static string ValidateEntryPath(string value, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) throw new InvalidDataException("Plugin package contains an empty path.");
        var path = value.Replace('\\', '/');
        if (path.StartsWith('/') || path.StartsWith("//") || Path.IsPathRooted(path))
            throw new InvalidDataException("Plugin package contains an absolute path.");
        if (isDirectory) path = path.TrimEnd('/');
        var segments = path.Split('/');
        if (segments.Any(segment => segment is "" or "." or "..")) throw new InvalidDataException("Plugin package contains a non-canonical path.");
        foreach (var segment in segments)
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.Contains(':'))
                throw new InvalidDataException("Plugin package path is unsafe on Windows.");
            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem)) throw new InvalidDataException("Plugin package path uses a reserved Windows name.");
        }
        return string.Join('/', segments);
    }

    private static void RejectLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode is 0xA000 or 0x6000) throw new InvalidDataException("Plugin package links and devices are not allowed.");
    }

    private static string ResolveUnderRoot(string root, string relative)
    {
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Plugin entry escapes staging.");
        return result;
    }

    private static void RequirePayloadEntry(IReadOnlyList<PluginPackageEntry> entries, string path, string field)
    {
        if (!entries.Any(value => !value.IsDirectory && string.Equals(value.CanonicalPath, path, StringComparison.Ordinal)))
            throw new InvalidDataException($"Manifest {field} is missing from the package.");
    }

    private static void ValidateArchitecture(IReadOnlyList<string> architectures)
    {
        if (architectures.Count == 0 || architectures.Any(value => value is not ("any" or "x86" or "x64" or "arm64")))
            throw new InvalidDataException("Plugin architectures must use any, x86, x64, or arm64.");
    }

    private static void ValidateHighImpactServices(PluginManifestContract manifest)
    {
        if (manifest.Capabilities.Contains(PluginCapabilityKind.BackupArtifactTransformer)
            && (!manifest.RequestedHostServices.Contains(HostServiceKind.ArtifactRead)
                || !manifest.RequestedHostServices.Contains(HostServiceKind.ArtifactTransformStaging)))
            throw new InvalidDataException("Artifact Transformer capability must disclose ArtifactRead and ArtifactTransformStaging.");
        if (manifest.Capabilities.Contains(PluginCapabilityKind.RestoreMaterializer)
            && (!manifest.RequestedHostServices.Contains(HostServiceKind.ArtifactRead)
                || !manifest.RequestedHostServices.Contains(HostServiceKind.RestoreMaterializationWorkspace)))
            throw new InvalidDataException("Restore Materializer capability must disclose its high-impact services.");
    }

    private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static HashSet<string> BuildReservedNames()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var index = 1; index <= 9; index++) { values.Add("COM" + index); values.Add("LPT" + index); }
        return values;
    }
}
