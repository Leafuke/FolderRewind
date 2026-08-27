using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Storage;

public sealed class FileHistoryRepository : IHistoryRepository, IDisposable
{
    private readonly HistoryPackCodec _codec;
    private readonly HistoryRepositoryValidator _validator;
    private readonly HistoryTransactionJournalStore _journals;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileHistoryRepository(
        HistoryConfigId configId,
        HistoryRepositoryPaths paths,
        HistoryPackCodec? codec = null)
    {
        ConfigId = configId;
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _codec = codec ?? new HistoryPackCodec();
        _validator = new HistoryRepositoryValidator(_codec);
        _journals = new HistoryTransactionJournalStore(paths);
    }

    public HistoryConfigId ConfigId { get; }
    public HistoryRepositoryPaths Paths { get; }
    public HistoryTransactionJournalStore Journals => _journals;

    public async Task<HistoryRepositoryDescriptor> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Paths.CreateDirectories();
            var expected = HistoryRepositoryDescriptor.Create(ConfigId);
            var expectedBytes = expected.ToCanonicalBytes();
            if (!File.Exists(Paths.DescriptorPath))
            {
                WriteCreateOnce(Paths.DescriptorPath, expectedBytes);
            }

            var actualBytes = await File.ReadAllBytesAsync(Paths.DescriptorPath, cancellationToken).ConfigureAwait(false);
            var actual = HistoryRepositoryDescriptor.Parse(actualBytes);
            if (actual.ConfigId != ConfigId || !actualBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new HistoryIntegrityConflictException(
                    "History repository descriptor does not match the requested Config identity.");
            }

            return actual;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryPackInstallResult> CommitAsync(
        HistoryCommitPack pack,
        HistoryTransactionJournal? journal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (journal is not null
            && (journal.TransactionId != pack.TransactionId || journal.IntendedPackId != pack.PackId))
        {
            throw new ArgumentException("Journal identity does not match the Commit Pack.", nameof(journal));
        }

        if (journal is not null)
        {
            _journals.Save(journal with { Phase = HistoryTransactionPhase.Prepared });
        }

        var result = (await ImportAsync([_codec.Encode(pack)], cancellationToken).ConfigureAwait(false))[0];
        if (journal is not null)
        {
            _journals.Save(journal with { Phase = HistoryTransactionPhase.PackInstalled });
        }

        return result;
    }

    public async Task<IReadOnlyList<HistoryPackInstallResult>> ImportAsync(
        IEnumerable<ReadOnlyMemory<byte>> packBytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(packBytes);
        var incomingBytes = packBytes.Select(bytes => bytes.ToArray()).ToArray();
        if (incomingBytes.Length == 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitializedInsideGate();
            var existing = await ReadAllPacksInsideGateAsync(cancellationToken).ConfigureAwait(false);
            var decoded = new List<HistoryPackReadResult>(incomingBytes.Length);
            for (var index = 0; index < incomingBytes.Length; index++)
            {
                try
                {
                    decoded.Add(_codec.Decode(incomingBytes[index]));
                }
                catch (HistoryPackCompatibilityException)
                {
                    throw;
                }
                catch (HistoryRepositoryException)
                {
                    Quarantine(incomingBytes[index], TryReadPackId(incomingBytes[index]));
                    throw;
                }
            }

            var duplicatePackIds = decoded.GroupBy(item => item.Pack.PackId)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicatePackIds is not null)
            {
                var first = duplicatePackIds.First().OriginalBytes;
                if (duplicatePackIds.Any(item => !item.OriginalBytes.AsSpan().SequenceEqual(first)))
                {
                    foreach (var item in duplicatePackIds)
                    {
                        Quarantine(item.OriginalBytes, item.Pack.PackId);
                    }
                    throw new HistoryIntegrityConflictException(
                        $"Incoming PackId {duplicatePackIds.Key} has different bytes.");
                }

                decoded = decoded.DistinctBy(item => item.Pack.PackId).ToList();
            }

            var existingByPack = existing.ToDictionary(item => item.Pack.PackId);
            var results = new List<HistoryPackInstallResult>(decoded.Count);
            var newPacks = new List<HistoryPackReadResult>();
            foreach (var incoming in decoded)
            {
                if (existingByPack.TryGetValue(incoming.Pack.PackId, out var current))
                {
                    if (!current.OriginalBytes.AsSpan().SequenceEqual(incoming.OriginalBytes))
                    {
                        Quarantine(incoming.OriginalBytes, incoming.Pack.PackId);
                        throw new HistoryIntegrityConflictException(
                            $"PackId {incoming.Pack.PackId} already exists with different bytes.");
                    }

                    results.Add(new HistoryPackInstallResult(
                        incoming.Pack.PackId,
                        HistoryPackInstallDisposition.Duplicate,
                        incoming.ContainsUnsupportedObjectSchema));
                }
                else
                {
                    newPacks.Add(incoming);
                    results.Add(new HistoryPackInstallResult(
                        incoming.Pack.PackId,
                        HistoryPackInstallDisposition.Installed,
                        incoming.ContainsUnsupportedObjectSchema));
                }
            }

            try
            {
                // 所有跨 Pack 引用都在 existing ∪ staged 的候选视图中一次验证，
                // 绝不按文件枚举顺序把合法 forward reference 判为损坏。
                _validator.Validate(ConfigId, existing.Concat(newPacks));
            }
            catch (HistoryRepositoryException)
            {
                foreach (var incoming in newPacks)
                {
                    Quarantine(incoming.OriginalBytes, incoming.Pack.PackId);
                }
                throw;
            }

            foreach (var incoming in newPacks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteCreateOnce(Paths.GetPackPath(incoming.Pack.PackId), incoming.OriginalBytes);
                var installedBytes = await File.ReadAllBytesAsync(
                    Paths.GetPackPath(incoming.Pack.PackId), cancellationToken).ConfigureAwait(false);
                if (!installedBytes.AsSpan().SequenceEqual(incoming.OriginalBytes))
                {
                    throw new HistoryIntegrityConflictException(
                        $"Installed Pack {incoming.Pack.PackId} failed read-back verification.");
                }
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HistoryPackReadResult>> ReadAllPacksAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitializedInsideGate();
            return await ReadAllPacksInsideGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private async Task<IReadOnlyList<HistoryPackReadResult>> ReadAllPacksInsideGateAsync(
        CancellationToken cancellationToken)
    {
        var results = new List<HistoryPackReadResult>();
        if (!Directory.Exists(Paths.PacksRoot))
        {
            return results;
        }

        foreach (var path in Directory.GetFiles(Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            results.Add(_codec.Decode(bytes));
        }

        return results;
    }

    private void EnsureInitializedInsideGate()
    {
        if (!File.Exists(Paths.DescriptorPath))
        {
            throw new InvalidOperationException("History repository has not been initialized.");
        }

        var descriptor = HistoryRepositoryDescriptor.Parse(File.ReadAllBytes(Paths.DescriptorPath));
        if (descriptor.ConfigId != ConfigId)
        {
            throw new HistoryIntegrityConflictException("Repository Config identity changed after initialization.");
        }
    }

    private void Quarantine(byte[] bytes, PackId? packId)
    {
        Paths.CreateDirectories();
        WriteCreateOnce(Paths.GetQuarantinePath(packId), bytes);
    }

    private static PackId? TryReadPackId(byte[] bytes)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            return PackId.Parse(document.RootElement.GetProperty("packId").GetString() ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCreateOnce(string destinationPath, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            if (!File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(bytes))
            {
                throw new HistoryIntegrityConflictException(
                    $"Create-once file '{fullPath}' already exists with different bytes.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

