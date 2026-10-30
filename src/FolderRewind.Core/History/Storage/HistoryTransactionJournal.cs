using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace FolderRewind.History.Storage;

public enum HistoryTransactionPhase
{
    Prepared = 0,
    PackInstalled = 1,
    LocalStateApplied = 2,
    Complete = 3
}

public sealed record HistoryLocalStateIntent(
    string StateKind,
    long ExpectedRevision,
    JsonElement Delta);

public sealed record HistoryTransactionJournal(
    HistoryTransactionId TransactionId,
    PackId IntendedPackId,
    HistoryTransactionPhase Phase,
    ImmutableArray<HistoryLocalStateIntent> LocalStateIntents,
    ImmutableArray<string> StagedPayloadCleanupHandles)
{
    public static HistoryTransactionJournal Prepared(
        HistoryTransactionId transactionId,
        PackId intendedPackId,
        IEnumerable<HistoryLocalStateIntent>? localStateIntents = null,
        IEnumerable<string>? stagedPayloadCleanupHandles = null)
        => new(
            transactionId,
            intendedPackId,
            HistoryTransactionPhase.Prepared,
            localStateIntents is null ? [] : [.. localStateIntents],
            stagedPayloadCleanupHandles is null ? [] : [.. stagedPayloadCleanupHandles]);
}

public sealed class HistoryTransactionJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HistoryRepositoryPaths _paths;
    private readonly AsyncLocal<CompletedReadScope?> _readScope = new();

    /// <summary>
    /// Reuse terminal journal evidence only while its file is held against writes/deletion.
    /// The caller owns the configuration operation; no evidence survives this scope.
    /// </summary>
    internal IDisposable BeginReadScope()
    {
        var scope = new CompletedReadScope(this, _readScope.Value);
        _readScope.Value = scope;
        return scope;
    }

    public HistoryTransactionJournalStore(HistoryRepositoryPaths paths)
        => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public void Save(HistoryTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var path = _paths.GetJournalPath(journal.TransactionId);
        for (var scope = _readScope.Value; scope is not null; scope = scope.Previous)
            scope.Invalidate(path);
        AtomicFileService.Write(
            path,
            stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
    }

    public HistoryTransactionJournal Load(string path)
    {
        if (_readScope.Value is { } scope) return scope.Load(path);
        using var stream = File.OpenRead(path);
        return Deserialize(stream, path);
    }

    private static HistoryTransactionJournal Deserialize(Stream stream, string path)
    {
        HistoryOperationDiagnostics.Current?.Count("history-journal-reads");
        return JsonSerializer.Deserialize<HistoryTransactionJournal>(stream, JsonOptions)
            ?? throw new HistoryRepositoryValidationException($"Journal '{path}' is empty.");
    }

    private sealed class CompletedReadScope(HistoryTransactionJournalStore owner, CompletedReadScope? previous) : IDisposable
    {
        // Bound both open handles and retained journal payloads. Overflow takes the normal read path.
        private const int MaximumEntries = 1024;
        private const long MaximumBytes = 8 * 1024 * 1024;
        private readonly object _sync = new();
        private readonly Dictionary<string, (FileStream Stream, SafeFileHandle Parent, HistoryTransactionJournal Journal)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly WindowsPhysicalDirectoryLease? _root = TryProtectRoot(owner._paths.TransactionsRoot);
        private long _bytes;
        private bool _disposed;
        internal CompletedReadScope? Previous { get; } = previous;

        internal HistoryTransactionJournal Load(string path)
        {
            path = Path.GetFullPath(path);
            lock (_sync)
            {
                if (!_disposed && _entries.TryGetValue(path, out var entry))
                {
                    HistoryOperationDiagnostics.Current?.Count("history-journal-read-reuses");
                    return entry.Journal;
                }
                TryOpenProtectedJournal(path, out var stream, out var parent);
                stream ??= new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                try
                {
                    var journal = Deserialize(stream, path);
                    if (!_disposed && parent is not null && journal.Phase == HistoryTransactionPhase.Complete
                        && StringComparer.OrdinalIgnoreCase.Equals(path, owner._paths.GetJournalPath(journal.TransactionId))
                        && _entries.Count < MaximumEntries && stream.Length <= MaximumBytes - _bytes)
                    {
                        _entries.Add(path, (stream, parent, journal));
                        _bytes += stream.Length;
                        stream = null;
                        parent = null; // The scope now owns both leases.
                    }
                    return journal;
                }
                finally { stream?.Dispose(); parent?.Dispose(); }
            }
        }

        private static WindowsPhysicalDirectoryLease? TryProtectRoot(string path)
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(path)) return null;
            try { return new WindowsPhysicalDirectoryLease(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private void TryOpenProtectedJournal(string path, out FileStream? stream, out SafeFileHandle? parent)
        {
            stream = null; parent = null;
            if (_disposed || _root is null || _entries.Count >= MaximumEntries || _bytes >= MaximumBytes) return;
            var directory = Path.GetDirectoryName(path);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(directory), owner._paths.TransactionsRoot)
                || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), "journal.json")) return;
            try
            {
                // Pin the shared root/ancestors once, then the ordinary transaction directory and file.
                // Reparse children/unsupported paths use uncached reads; no path/stat cache is trusted.
                var physicalParent = Path.Combine(_root.PhysicalPath, Path.GetFileName(directory)!);
                parent = WindowsRestoreFileSystem.Open(physicalParent, 0x81, 1);
                if ((WindowsRestoreFileSystem.Attributes(parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("Journal parent is not an ordinary directory.");
                var handle = WindowsRestoreFileSystem.Open(Path.Combine(physicalParent, "journal.json"), 0x80000000, 1);
                try
                {
                    if ((WindowsRestoreFileSystem.Attributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("Journal is not an ordinary file.");
                    stream = new FileStream(handle, FileAccess.Read, 1, false);
                    handle = null!;
                }
                finally { handle?.Dispose(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stream?.Dispose(); stream = null;
                parent?.Dispose(); parent = null;
            }
        }

        internal void Invalidate(string path)
        {
            lock (_sync)
            {
                if (!_entries.Remove(path, out var entry)) return;
                _bytes -= entry.Stream.Length;
                entry.Stream.Dispose();
                entry.Parent.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var entry in _entries.Values) { entry.Stream.Dispose(); entry.Parent.Dispose(); }
                _entries.Clear();
                _root?.Dispose();
                _bytes = 0;
            }
            if (ReferenceEquals(owner._readScope.Value, this)) owner._readScope.Value = Previous;
        }
    }

    public IReadOnlyList<string> EnumerateJournalPaths()
        => Directory.Exists(_paths.TransactionsRoot)
            ? Directory.GetFiles(_paths.TransactionsRoot, "journal.json", SearchOption.AllDirectories)
            : [];

    internal void RequireRecovered()
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("history-journal-health");
        foreach (var path in EnumerateJournalPaths())
            if (Load(path).Phase != HistoryTransactionPhase.Complete)
                throw new InvalidOperationException("An incomplete history commit requires recovery before further mutations or GC.");
    }

    public async Task RecoverAsync(
        Func<HistoryTransactionJournal, CancellationToken, Task> applyCommittedState,
        Func<HistoryTransactionJournal, CancellationToken, Task> rollbackPreparedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyCommittedState);
        ArgumentNullException.ThrowIfNull(rollbackPreparedState);
        using var timing = HistoryOperationDiagnostics.Current?.Measure("history-journal-recovery");
        foreach (var path in EnumerateJournalPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = Load(path);
            if (journal.Phase == HistoryTransactionPhase.Complete)
            {
                continue;
            }

            if (File.Exists(_paths.GetPackPath(journal.IntendedPackId)))
            {
                await applyCommittedState(journal, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await rollbackPreparedState(journal, cancellationToken).ConfigureAwait(false);
            }

            Save(journal with { Phase = HistoryTransactionPhase.Complete });
        }
    }
}
