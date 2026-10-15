using FolderRewind.History.Application;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Storage;

/// <summary>Quick 比较及保留补丁消费同一当前对象；不以 size/mtime 推断内容相等。</summary>
internal sealed class HistoryWorkingTreeReadLease : IDisposable
{
    private readonly HistoryRestoreSourceBinding _binding;
    private readonly Dictionary<string, (RestoreObjectIdentity Id, FileStream Stream)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RestoreObjectIdentity> _directories = new(StringComparer.Ordinal);
    private readonly WindowsDirectoryReadLease? _parents;
    private bool _disposed;
    internal HistoryWorkingTreeReadLease(HistoryRestoreSourceBinding binding)
    {
        _binding = binding;
        if (!Directory.Exists(binding.TargetDirectory)) return;
        _parents = new WindowsDirectoryReadLease(Path.Combine(binding.TargetDirectory, "unused-child"));
        try
        {
            foreach (var file in ManagedFiles())
            {
                var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try { _files.Add(file, (WindowsRestoreFileSystem.Identity(stream.SafeFileHandle), stream)); }
                catch { stream.Dispose(); throw; }
            }
            foreach (var directory in ManagedDirectories()) _directories.Add(directory, WindowsRestoreFileSystem.Identity(directory));
        }
        catch { Dispose(); throw; }
    }
    internal void Validate()
    {
        if (_disposed) return;
        var files = ManagedFiles().ToArray();
        if (files.Length != _files.Count || files.Any(path => !_files.TryGetValue(path, out var known)
            || WindowsRestoreFileSystem.Identity(path) != known.Id))
            throw new IOException("Current files changed during Quick restore preparation.");
        var directories = ManagedDirectories().ToArray();
        if (directories.Length != _directories.Count || directories.Any(path => !_directories.TryGetValue(path, out var known)
            || WindowsRestoreFileSystem.Identity(path) != known)) throw new IOException("Current directories changed during Quick restore preparation.");
    }
    private IEnumerable<string> ManagedDirectories()
    {
        if (!Directory.Exists(_binding.TargetDirectory)) yield break;
        yield return Path.GetFullPath(_binding.TargetDirectory);
        var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(_binding);
        var pending = new Stack<string>(); pending.Push(_binding.TargetDirectory);
        while (pending.TryPop(out var parent))
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Quick restore cannot follow current-world links.");
                if ((attributes & FileAttributes.Directory) == 0) continue;
                pending.Push(path);
                if (include(Path.GetRelativePath(_binding.TargetDirectory, path).Replace('\\', '/'))) yield return Path.GetFullPath(path);
            }
    }
    private IEnumerable<string> ManagedFiles()
    {
        if (!Directory.Exists(_binding.TargetDirectory)) yield break;
        var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(_binding);
        var pending = new Stack<string>(); pending.Push(_binding.TargetDirectory);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Quick restore cannot follow current-world links.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else if (include(Path.GetRelativePath(_binding.TargetDirectory, path).Replace('\\', '/'))) yield return Path.GetFullPath(path);
            }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var file in _files.Values) file.Stream.Dispose(); _files.Clear(); _parents?.Dispose();
    }
}
