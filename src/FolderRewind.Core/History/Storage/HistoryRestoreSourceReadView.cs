using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Storage;

/// <summary>Creates inventory only when consumed; borrowed current input remains owned by the restore operation.</summary>
internal sealed class HistoryRestoreSourceReadView(string root, HistoryRestoreSourceBinding binding,
    HistoryWorkingTreeReadLease? borrowed = null) : IRestoreSourceView, IDisposable
{
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.Ordinal);
    private HistoryWorkingTreeReadLease? _read;
    private IReadOnlyList<string> _all = [], _managed = [];
    private bool _loaded, _disposed;
    private bool _ownsRead;

    private void EnsureLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loaded) return;
        try
        {
            var viewBinding = binding with { TargetDirectory = root, LogicalDirectory = binding.ComparisonRoot };
            if (borrowed is not null)
            {
                if (!borrowed.Matches(viewBinding)) throw new IOException("Borrowed restore input belongs to another source boundary.");
                _read = borrowed;
                Services.HistoryOperationDiagnostics.Current?.Count("restore-preparation-lease-reuses");
            }
            else { _read = new(viewBinding); _ownsRead = true; }
            var all = new List<string>();
            if (Directory.Exists(_read.PhysicalRoot))
                foreach (var entry in new HistoryRestoreTreeScope(binding).Enumerate(_read.PhysicalRoot, includeExcluded: true))
                {
                    if (entry.Directory) continue;
                    all.Add(entry.RelativePath);
                    if (entry.Managed) _files.Add(entry.RelativePath, _read.GetStream(entry.RelativePath));
                }
            _all = all.AsReadOnly();
            _managed = Array.AsReadOnly(_files.Keys.Order(StringComparer.Ordinal).ToArray());
            _loaded = true;
            Services.HistoryOperationDiagnostics.Current?.Count("restore-preparation-inventory-loads");
        }
        catch { Dispose(); throw; }
    }
    public IReadOnlyList<string> RelativePaths { get { EnsureLoaded(); return _managed; } }
    public IEnumerable<string> EnumerateAllRelativePaths()
    {
        EnsureLoaded();
        foreach (var path in _all) yield return path;
    }
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureLoaded();
        var path = ArtifactPathRules.NormalizeRelativePath(relativePath);
        if (!_files.TryGetValue(path, out var input)) throw new FileNotFoundException(path);
        lock (input)
        {
            if (input.Length > 64 * 1024 * 1024) throw new IOException("Restore preparation input exceeds limit.");
            input.Position = 0;
            using var buffer = new MemoryStream(); input.CopyTo(buffer);
            return ValueTask.FromResult<Stream>(new MemoryStream(buffer.ToArray(), writable: false));
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _files.Clear();
        if (_ownsRead) _read?.Dispose();
    }
}
