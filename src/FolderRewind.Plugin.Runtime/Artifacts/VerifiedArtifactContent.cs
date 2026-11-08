using System.ComponentModel;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Host-only proof for one decoded tree. Read handles keep verified bytes and namespace bindings stable.</summary>
internal sealed class VerifiedArtifactContent : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private readonly List<FileStream> _files = [];
    private readonly HashSet<string> _pinnedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private IO.DirectoryPathLease? _ancestors;
    private bool _disposed;
    internal string Root { get; }
    internal IReadOnlyList<ArtifactFileEntry> Files { get; private set; } = [];
    internal string Sha256 { get; private set; } = "";
    internal long Size { get; private set; }
    private VerifiedArtifactContent(string root) => Root = Path.GetFullPath(root);

    internal static async Task<VerifiedArtifactContent> CreateAsync(string root, CancellationToken token, IReadOnlySet<string>? allowedPaths = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Verified inventory reuse requires Windows read protection.");
        var proof = new VerifiedArtifactContent(root);
        byte[]? hashBuffer = null;
        try
        {
            using var phase = ArtifactDiagnostics.Source.StartActivity("logical.hash");
            bool single = File.Exists(proof.Root);
            proof.PinAncestors(single ? Path.GetDirectoryName(proof.Root)! : proof.Root);
            if (!single && allowedPaths is null)
            {
                var pending = new Stack<string>(); pending.Push(proof.Root);
                while (pending.TryPop(out var directory))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Decoded Artifact contains a filesystem alias.");
                        if ((attributes & FileAttributes.Directory) != 0) { proof.PinDirectory(entry); pending.Push(entry); }
                    }
            }
            IReadOnlyList<(string RelativePath, string FullPath)> paths = single ? new[] { (RelativePath: "payload", FullPath: proof.Root) }
                : allowedPaths is null ? HostArtifactReadService.EnumerateSafeFiles(proof.Root)
                : allowedPaths.Order(StringComparer.Ordinal).Select(path => (RelativePath: ArtifactPathRules.NormalizeRelativePath(path),
                    FullPath: ArtifactPathRules.ResolveUnderRoot(proof.Root, path))).ToArray();
            if (paths.Count == 0) throw new InvalidDataException("Artifact payload cannot be empty.");
            var files = new List<ArtifactFileEntry>(paths.Count);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (!single && allowedPaths is not null)
                    for (var parent = Path.GetDirectoryName(path.FullPath)!; !StringComparer.OrdinalIgnoreCase.Equals(parent, proof.Root); parent = Path.GetDirectoryName(parent)!)
                    {
                        if (string.IsNullOrEmpty(parent)) throw new InvalidDataException("Protected input escaped its root.");
                        proof.PinDirectory(parent);
                    }
                var handle = Open(path.FullPath, directory: false);
                FileStream stream;
                // Retained guards must not keep one large buffer per file. Use one bounded shared
                // hash buffer instead; tiny asynchronous hash reads otherwise dominate Full inputs.
                try { stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true); }
                catch { handle.Dispose(); throw; }
                proof._files.Add(stream);
                hashBuffer ??= ArrayPool<byte>.Shared.Rent(64 * 1024);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = await stream.ReadAsync(hashBuffer.AsMemory(0, 64 * 1024), token).ConfigureAwait(false)) != 0)
                    digest.AppendData(hashBuffer, 0, read);
                var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                files.Add(new(path.RelativePath, stream.Length, hash));
            }
            proof.Files = files.AsReadOnly();
            proof._paths.UnionWith(files.Select(file => file.RelativePath));
            if (single) { proof.Sha256 = files[0].Sha256; proof.Size = files[0].Length; }
            else (proof.Sha256, proof.Size) = HostArtifactReadService.ComputeInventoryFacts(proof.Files);
            return proof;
        }
        catch { proof.Dispose(); throw; }
        finally { if (hashBuffer is not null) ArrayPool<byte>.Shared.Return(hashBuffer, clearArray: true); }
    }
    internal void RequireRoot(string root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Root, Path.GetFullPath(root)))
            throw new InvalidOperationException("Verified inventory belongs to another decoded tree.");
    }
    internal void RequireFile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_paths.Contains(ArtifactPathRules.NormalizeRelativePath(path)))
            throw new FileNotFoundException("Path is outside the verified Artifact inventory.", path);
    }
    private void PinAncestors(string directory)
    {
        _ancestors = new IO.DirectoryPathLease(Path.GetDirectoryName(Path.GetFullPath(directory))!);
        PinDirectory(directory);
    }
    private void PinDirectory(string path)
    {
        if (!_pinnedDirectories.Add(Path.GetFullPath(path))) return;
        _directories.Add(Open(path, directory: true));
    }
    private static SafeFileHandle Open(string path, bool directory)
    {
        // OPEN_REPARSE_POINT ensures attributes are checked on the opened object, not a followed link.
        // The Host can run without process-wide long-path opt-in. Keep the same
        // extended-path spelling as DirectoryPathLease for decoded trees as well.
        var extended = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = CreateFileW(extended, directory ? 0x81u : 0x80000000u, 1, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0x48000000u), IntPtr.Zero);
        if (handle.IsInvalid)
        { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Cannot protect decoded Artifact content.", new Win32Exception(error)); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot inspect decoded Artifact content.", new Win32Exception(Marshal.GetLastWin32Error()));
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
                throw new InvalidDataException("Decoded Artifact binding is not an ordinary filesystem object.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var file in _files) file.Dispose(); _files.Clear();
        for (int i = _directories.Count - 1; i >= 0; i--) _directories[i].Dispose();
        _directories.Clear();
        _ancestors?.Dispose(); _ancestors = null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
