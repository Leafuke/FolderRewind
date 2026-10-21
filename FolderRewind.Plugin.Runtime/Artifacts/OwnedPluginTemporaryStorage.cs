using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>A private process-owned container for plugin snapshots. Other live containers are never reclaimed.</summary>
public sealed class OwnedPluginTemporaryStorage : IPluginTemporaryStorage, IAsyncDisposable
{
    private sealed record Owner(int Version, string RootHash);
    private readonly string _directory;
    private FileStream? _ownership;
    private OwnedPluginTemporaryStorage(string directory, FileStream ownership)
    { _directory = directory; _ownership = ownership; }

    // Process current directories still have a Windows length limit. Keep snapshots
    // short without copying them again or creating aliases into a source directory.
    public static string ForLogicalRoot(string root)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var identity = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Path.Combine(Path.GetTempPath(), "FolderRewind-v3", identity);
    }

    public static async Task<OwnedPluginTemporaryStorage> CreateAsync(string root, CancellationToken token = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        HostArtifactWorkspace.CreatePrivateDirectory(root);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)));
        foreach (var directory in Directory.EnumerateDirectories(root, "session-*"))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory)[8..], "N", out _)) continue;
            try
            {
                var owner = ArtifactMetadataFile.ReadJson<Owner>(Path.Combine(directory, "owner.json"), 1024);
                if (owner != new Owner(1, identity)) continue;
                // Closing this lock before deletion is safe: no process adopts an old random session ID.
                using (new FileStream(Path.Combine(directory, "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                HostArtifactWorkspace.DeleteOwnedDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { /* An active or unprovable owner retains its data. */ }
        }
        var current = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        HostArtifactWorkspace.CreatePrivateDirectory(current);
        var ownership = new FileStream(Path.Combine(current, "owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await NativeArtifactStore.WriteAsync(Path.Combine(current, "owner.json"), new Owner(1, identity), token);
            return new(current, ownership);
        }
        catch { ownership.Dispose(); HostArtifactWorkspace.DeleteOwnedDirectory(current); throw; }
    }

    public ValueTask<string> CreateDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_ownership is null, this);
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        HostArtifactWorkspace.CreatePrivateDirectory(path);
        return ValueTask.FromResult(path);
    }

    public ValueTask<IDisposable> ReserveAsync(string directory, long bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_ownership is null, this);
        var path = Path.GetFullPath(directory);
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
            throw new InvalidDataException("Temporary reservation does not belong to this plugin session.");
        HostArtifactReadService.EnsureNoReparsePoints(_directory, path);
        return ValueTask.FromResult(ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(path, bytes)));
    }

    public ValueTask DisposeAsync()
    { Interlocked.Exchange(ref _ownership, null)?.Dispose(); return ValueTask.CompletedTask; }
}
