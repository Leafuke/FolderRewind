using System.ComponentModel;
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
    private bool _disposed;
    internal string Root { get; }
    internal IReadOnlyList<ArtifactFileEntry> Files { get; private set; } = [];
    internal string Sha256 { get; private set; } = "";
    internal long Size { get; private set; }
    private VerifiedArtifactContent(string root) => Root = Path.GetFullPath(root);

    internal static async Task<VerifiedArtifactContent> CreateAsync(string root, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Verified inventory reuse requires Windows read protection.");
        var proof = new VerifiedArtifactContent(root);
        try
        {
            using var phase = ArtifactDiagnostics.Source.StartActivity("logical.hash");
            bool single = File.Exists(proof.Root);
            proof.PinAncestors(single ? Path.GetDirectoryName(proof.Root)! : proof.Root);
            if (!single)
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
                : HostArtifactReadService.EnumerateSafeFiles(proof.Root);
            if (paths.Count == 0) throw new InvalidDataException("Artifact payload cannot be empty.");
            var files = new List<ArtifactFileEntry>(paths.Count);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var handle = Open(path.FullPath, directory: false);
                FileStream stream;
                // The hash API owns its read buffer. Retained guard streams must not retain one 64 KiB buffer per file.
                try { stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true); }
                catch { handle.Dispose(); throw; }
                proof._files.Add(stream);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
                files.Add(new(path.RelativePath, stream.Length, hash));
            }
            proof.Files = files.AsReadOnly();
            proof._paths.UnionWith(files.Select(file => file.RelativePath));
            if (single) { proof.Sha256 = files[0].Sha256; proof.Size = files[0].Length; }
            else (proof.Sha256, proof.Size) = HostArtifactReadService.ComputeInventoryFacts(proof.Files);
            return proof;
        }
        catch { proof.Dispose(); throw; }
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
        var pending = new Stack<string>();
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent) pending.Push(item.FullName);
        while (pending.TryPop(out var path)) PinDirectory(path);
    }
    private void PinDirectory(string path)
    {
        if (!_pinnedDirectories.Add(Path.GetFullPath(path))) return;
        _directories.Add(Open(path, directory: true));
    }
    private static SafeFileHandle Open(string path, bool directory)
    {
        // OPEN_REPARSE_POINT ensures attributes are checked on the opened object, not a followed link.
        var handle = CreateFileW(path, directory ? 0x81u : 0x80000000u, 1, IntPtr.Zero, 3,
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
