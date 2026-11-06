using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FolderRewind.Services;

internal static class RcloneCredentialStore
{
    internal const string OriginSuffix = ".folderrewind-origin.json";
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> Roots = new(StringComparer.OrdinalIgnoreCase);
    internal static string Scope(string root, string source) => Path.Combine(root, "by-source", RcloneConnectionDiagnostics.StorageKey(source));
    internal static void Track(string source, string root, bool managed)
        => Roots.GetOrAdd(Path.GetFullPath(source), _ => new(StringComparer.OrdinalIgnoreCase))[Path.GetFullPath(root)] = managed;
    internal static bool HasTrackedEvidence(string source) => Roots.TryGetValue(Path.GetFullPath(source), out var roots)
        && roots.Any(root => RcloneCredentialRefresh.HasPendingEvidence(root.Key, source, root.Value));

    // Only explicit creation/import into a new, Host-owned directory calls this; automatic path migration never does.
    internal static void RegisterFresh(string source, byte[] bytes)
    {
        var path = source + OriginSuffix;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new CredentialOrigin(1, RcloneConnectionDiagnostics.StorageKey(source),
                Convert.ToHexString(SHA256.HashData(bytes))), CredentialRefreshJsonContext.Default.CredentialOrigin);
        RcloneExecutionContext.ProtectConfigurationFile(path);
    }

    internal static bool HasFreshOrigin(string source, bool managed)
    {
        var path = source + OriginSuffix;
        if (!managed || !File.Exists(path)) return false;
        try
        {
            RcloneCredentialRefresh.RejectReparsePoints(path);
            RcloneCredentialRefresh.RejectHardLinks(path);
            var bytes = ReadMetadata(path);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new JsonException();
            var origin = JsonSerializer.Deserialize(bytes, CredentialRefreshJsonContext.Default.CredentialOrigin);
            if (origin is null || origin.Version != 1 || origin.StorageKey != RcloneConnectionDiagnostics.StorageKey(source)
                || origin.InitialRevision is not { Length: 64 } || !origin.InitialRevision.All(Uri.IsHexDigit)) throw new JsonException();
            return RcloneCredentialRefresh.AcceptsFrozenRevision(source, origin.InitialRevision, RcloneExecutionContext.ReadConfigurationBytes(source));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.InvalidEvidence, CredentialFailureStage.Origin, source);
        }
    }

    internal static byte[] ReadMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new InvalidDataException();
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }

    // Attribute the bounded header before parsing another store's payload or attempting recovery.
    internal static string? Owner(string directory)
    {
        try
        {
            RcloneCredentialRefresh.RejectReparsePoints(directory);
            string? owner = null;
            foreach (var name in new[] { "snapshot.json", "attempt.json" })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                RcloneCredentialRefresh.RejectReparsePoints(path); RcloneCredentialRefresh.RejectHardLinks(path);
                using var document = JsonDocument.Parse(ReadMetadata(path));
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)
                    || !value.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number) || number != 1
                    || !value.TryGetProperty("SourcePath", out var source) || source.ValueKind != JsonValueKind.String
                    || source.GetString() is not { } text || !Path.IsPathFullyQualified(text)) return null;
                var normalized = Path.GetFullPath(text);
                if (owner is not null && !StringComparer.OrdinalIgnoreCase.Equals(owner, normalized)) return null;
                owner = normalized;
            }
            return owner;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    internal static IEnumerable<(string Directory, bool Legacy)> Enumerate(string root, string source)
    {
        foreach (var (parent, legacy) in new[] { (Scope(root, source), false), (root, true) })
        {
            if (!Directory.Exists(parent)) continue;
            RcloneCredentialRefresh.RejectReparsePoints(parent);
            var directories = Directory.EnumerateDirectories(parent, "task-*").Take(1001).ToArray();
            if (directories.Length > 1000)
                throw RcloneConnectionDiagnostics.CredentialFailure(CredentialFailureReason.InvalidEvidence, CredentialFailureStage.Recover, source);
            foreach (var directory in directories) yield return (directory, legacy);
        }
    }
}

internal sealed record CredentialOrigin(int Version, string StorageKey, string InitialRevision);
