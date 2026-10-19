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
    private readonly WindowsPhysicalDirectoryLease? _parents;
    private readonly string _root;
    private bool _disposed;
    internal HistoryWorkingTreeReadLease(HistoryRestoreSourceBinding binding)
    {
        _root = binding.TargetDirectory;
        _binding = binding;
        if (!Directory.Exists(binding.TargetDirectory)) return;
        _parents = new WindowsPhysicalDirectoryLease(binding.TargetDirectory);
        _root = _parents.PhysicalPath;
        _binding = binding with { TargetDirectory = _root, LogicalDirectory = binding.ComparisonRoot };
        try
        {
            _directories.Add(_root, _parents.Identity);
            foreach (var entry in new HistoryRestoreTreeScope(_binding).Enumerate(_root))
            {
                if (entry.Directory) { _directories.Add(entry.FullPath, WindowsRestoreFileSystem.Identity(entry.FullPath)); continue; }
                var stream = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try { _files.Add(entry.FullPath, (WindowsRestoreFileSystem.Identity(stream.SafeFileHandle), stream)); }
                catch { stream.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }
    internal void Validate()
    {
        if (_disposed) return;
        var entries = Directory.Exists(_root) ? new HistoryRestoreTreeScope(_binding).Enumerate(_root).ToArray() : [];
        var files = entries.Where(e => !e.Directory).Select(e => e.FullPath).ToArray();
        if (files.Length != _files.Count || files.Any(path => !_files.TryGetValue(path, out var known)
            || WindowsRestoreFileSystem.Identity(path) != known.Id))
            throw new IOException("Current files changed during Quick restore preparation.");
        var directories = entries.Where(e => e.Directory).Select(e => e.FullPath).Prepend(_root).ToArray();
        if (directories.Length != _directories.Count || directories.Any(path => !_directories.TryGetValue(path, out var known)
            || WindowsRestoreFileSystem.Identity(path) != known)) throw new IOException("Current directories changed during Quick restore preparation.");
    }
    internal string PhysicalRoot => _root;
    internal FileStream GetStream(string relative)
    {
        var stream = _files[Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar))].Stream;
        stream.Position = 0;
        return stream;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var file in _files.Values) file.Stream.Dispose(); _files.Clear(); _parents?.Dispose();
    }
}
