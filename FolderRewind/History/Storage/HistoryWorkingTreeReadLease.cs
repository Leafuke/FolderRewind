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
        try { _parents = new WindowsPhysicalDirectoryLease(binding.TargetDirectory); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        _root = _parents.PhysicalPath;
        _binding = binding with { TargetDirectory = _root, LogicalDirectory = binding.ComparisonRoot };
        try
        {
            _directories.Add(_root, _parents.Identity);
            WindowsDirectoryReadLease? parent = null;
            string? parentPath = null;
            try
            {
                foreach (var entry in TrackedEntries())
                {
                    if (entry.Directory) { _directories.Add(entry.FullPath, WindowsRestoreFileSystem.Identity(entry.FullPath)); continue; }
                    var directory = Path.GetDirectoryName(entry.FullPath)!;
                    if (directory != parentPath)
                    {
                        parent?.Dispose(); parent = new WindowsDirectoryReadLease(entry.FullPath); parentPath = directory;
                        if (!_directories.TryGetValue(directory, out var expected) || WindowsRestoreFileSystem.Identity(parent.PhysicalDirectory) != expected)
                            throw new IOException("Managed file parent changed before protected read.");
                    }
                    var stream = WindowsDirectoryReadLease.OpenPayload(entry.FullPath, allowRename: true);
                    try { _files.Add(entry.FullPath, (WindowsRestoreFileSystem.Identity(stream.SafeFileHandle), stream)); }
                    catch { stream.Dispose(); throw; }
                }
            }
            finally { parent?.Dispose(); }
        }
        catch { Dispose(); throw; }
    }
    internal void Validate()
    {
        if (_disposed) return;
        bool present = Directory.Exists(_root);
        var entries = present ? TrackedEntries().ToArray() : [];
        var files = entries.Where(e => !e.Directory).Select(e => e.FullPath).ToArray();
        if (files.Length != _files.Count || files.Any(path => !_files.TryGetValue(path, out var known)
            || WindowsRestoreFileSystem.Identity(path) != known.Id))
            throw new IOException("Current files changed during Quick restore preparation.");
        var directories = entries.Where(e => e.Directory).Select(e => e.FullPath).Concat(present ? [_root] : Array.Empty<string>()).ToArray();
        if (directories.Length != _directories.Count || directories.Any(path => !_directories.TryGetValue(path, out var known)
            || WindowsRestoreFileSystem.Identity(path) != known)) throw new IOException("Current directories changed during Quick restore preparation.");
    }
    internal string PhysicalRoot => _root;
    private IEnumerable<RestoreTreeEntry> TrackedEntries()
    {
        var scope = new HistoryRestoreTreeScope(_binding);
        return scope.Enumerate(_root, includeExcluded: true).Where(e => e.Managed || e.Directory && scope.MayDescend(e.RelativePath));
    }
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
