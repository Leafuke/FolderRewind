using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed class HostArtifactReadService : IArtifactReadService
{
    private readonly IReadOnlyDictionary<ArtifactContentHandle, string> _roots;

    public HostArtifactReadService(IReadOnlyDictionary<ArtifactContentHandle, string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots.ToDictionary(pair => pair.Key, pair => Path.GetFullPath(pair.Value));
    }

    public ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(
        ArtifactContentHandle content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RequireRoot(content);
        IReadOnlyList<ArtifactFileEntry> entries = EnumerateSafeFiles(root)
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new ArtifactFileEntry(file.RelativePath, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            })
            .ToArray();
        return ValueTask.FromResult(entries);
    }

    public ValueTask<Stream> OpenReadAsync(
        ArtifactContentHandle content,
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = RequireRoot(content);
        var path = ArtifactPathRules.ResolveUnderRoot(root, relativePath);
        EnsureNoReparsePoints(root, path);
        if (!File.Exists(path)) throw new FileNotFoundException("Artifact logical file was not found.", relativePath);
        return ValueTask.FromResult<Stream>(new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    internal static IReadOnlyList<(string RelativePath, string FullPath)> EnumerateSafeFiles(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException("Artifact content root does not exist.");
        var rootInfo = new DirectoryInfo(fullRoot);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Artifact content root cannot be a reparse point.");
        }

        var entries = new List<(string RelativePath, string FullPath)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Artifact content cannot contain reparse points.");
            }
            if (Directory.Exists(path)) continue;
            var relative = ArtifactPathRules.NormalizeRelativePath(Path.GetRelativePath(fullRoot, path));
            if (!paths.Add(relative)) throw new InvalidDataException("Artifact content contains a case-insensitive path collision.");
            entries.Add((relative, path));
        }
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray();
    }

    internal static async ValueTask<(string Sha256, long Size)> ComputeLogicalFactsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var files = EnumerateSafeFiles(root);
        if (files.Count == 0) throw new InvalidDataException("Artifact payload cannot be empty.");
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var length = new byte[sizeof(long)];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            aggregate.AppendData([0]);
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
            aggregate.AppendData(length);
            aggregate.AppendData(hash);
            total += stream.Length;
        }
        return (Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(), total);
    }

    internal static void EnsureNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetDirectoryName(path);
        while (current is not null && current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current)
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Artifact path traverses a reparse point.");
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(current.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar))) break;
            current = Path.GetDirectoryName(current);
        }
    }

    private string RequireRoot(ArtifactContentHandle content)
        => _roots.TryGetValue(content, out var root)
            ? root
            : throw new InvalidOperationException("Artifact content handle is not valid for this operation.");
}

public sealed class ArtifactTransformStagingArea : IArtifactTransformStaging, IAsyncDisposable
{
    private readonly string _root;
    private readonly int _maximumFiles;
    private readonly long _maximumBytes;
    private readonly Dictionary<ArtifactStagingHandle, Allocation> _allocations = new();
    private long _reservedBytes;
    private int _reservedFiles;
    private int _openStreams;
    private bool _sealed;

    public ArtifactTransformStagingArea(
        string root,
        int maximumFiles = 10_000,
        long maximumBytes = 8L * 1024 * 1024 * 1024)
    {
        if (maximumFiles <= 0 || maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        _maximumFiles = maximumFiles;
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_root);
        if (File.GetAttributes(_root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Artifact staging root cannot be a reparse point.");
        }
        var drive = new DriveInfo(Path.GetPathRoot(_root)!);
        if (drive.AvailableFreeSpace < maximumBytes)
        {
            throw new IOException("Artifact staging cannot reserve its configured byte quota.");
        }
    }

    public ValueTask<ArtifactStagingAllocation> CreateArtifactAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_allocations)
        {
            ThrowIfSealed();
            var artifactId = new ArtifactId(Guid.NewGuid());
            var handle = new ArtifactStagingHandle(Guid.NewGuid().ToString("N"));
            var path = Path.Combine(_root, artifactId.Value.ToString("N"));
            Directory.CreateDirectory(path);
            _allocations.Add(handle, new Allocation(artifactId, path));
            return ValueTask.FromResult(new ArtifactStagingAllocation(artifactId, handle));
        }
    }

    public ValueTask<Stream> OpenWriteAsync(
        ArtifactStagingHandle staging,
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_allocations)
        {
            ThrowIfSealed();
            if (!_allocations.TryGetValue(staging, out var allocation))
            {
                throw new InvalidOperationException("Artifact staging handle is not valid for this transaction.");
            }
            var normalized = ArtifactPathRules.NormalizeRelativePath(relativePath);
            if (!allocation.Paths.Add(normalized))
            {
                throw new InvalidDataException("Artifact staging paths must be unique without regard to case.");
            }
            if (++_reservedFiles > _maximumFiles)
            {
                _reservedFiles--;
                allocation.Paths.Remove(normalized);
                throw new IOException("Artifact staging file quota exceeded.");
            }

            var path = ArtifactPathRules.ResolveUnderRoot(allocation.Path, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            HostArtifactReadService.EnsureNoReparsePoints(allocation.Path, path);
            var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _openStreams++;
            return ValueTask.FromResult<Stream>(new QuotaWriteStream(stream, ReserveBytes, StreamClosed));
        }
    }

    public async ValueTask<IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts>> SealAsync(
        string contentPathPrefix,
        CancellationToken cancellationToken = default)
    {
        lock (_allocations)
        {
            ThrowIfSealed();
            if (_openStreams != 0) throw new InvalidOperationException("All staging streams must be closed before sealing.");
            _sealed = true;
        }

        var facts = new Dictionary<ArtifactStagingHandle, StagedArtifactFacts>();
        foreach (var (handle, allocation) in _allocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (logicalHash, total) = await HostArtifactReadService.ComputeLogicalFactsAsync(
                allocation.Path,
                cancellationToken).ConfigureAwait(false);
            var relative = ArtifactPathRules.NormalizeRelativePath(
                $"{contentPathPrefix.TrimEnd('/', '\\')}/{allocation.ArtifactId.Value:N}");
            facts.Add(handle, new StagedArtifactFacts(
                allocation.ArtifactId,
                handle,
                relative,
                logicalHash,
                total,
                logicalHash,
                total));
        }
        return facts;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    internal string GetAllocationPath(ArtifactStagingHandle handle)
        => _allocations.TryGetValue(handle, out var allocation)
            ? allocation.Path
            : throw new InvalidOperationException("Unknown staging handle.");

    private void ReserveBytes(int count)
    {
        var total = Interlocked.Add(ref _reservedBytes, count);
        if (total <= _maximumBytes) return;
        Interlocked.Add(ref _reservedBytes, -count);
        throw new IOException("Artifact staging byte quota exceeded.");
    }

    private void StreamClosed() => Interlocked.Decrement(ref _openStreams);
    private void ThrowIfSealed()
    {
        if (_sealed) throw new InvalidOperationException("Artifact staging is already sealed.");
    }

    private sealed record Allocation(ArtifactId ArtifactId, string Path)
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class RestoreMaterializationWorkspace : IRestoreMaterializationWorkspace, IAsyncDisposable
{
    private readonly ArtifactTransformStagingArea _storage;
    private readonly ArtifactStagingAllocation _allocation;

    private RestoreMaterializationWorkspace(
        ArtifactTransformStagingArea storage,
        ArtifactStagingAllocation allocation,
        string rootPath)
    {
        _storage = storage;
        _allocation = allocation;
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public static async ValueTask<RestoreMaterializationWorkspace> CreateAsync(
        string root,
        int maximumFiles = 100_000,
        long maximumBytes = 16L * 1024 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var storage = new ArtifactTransformStagingArea(root, maximumFiles, maximumBytes);
        var allocation = await storage.CreateArtifactAsync(cancellationToken).ConfigureAwait(false);
        return new RestoreMaterializationWorkspace(storage, allocation, storage.GetAllocationPath(allocation.Staging));
    }

    public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
        => _storage.OpenWriteAsync(_allocation.Staging, relativePath, cancellationToken);

    public async ValueTask<IReadOnlyList<ArtifactFileEntry>> SealAsync(CancellationToken cancellationToken = default)
    {
        await _storage.SealAsync("restore", cancellationToken).ConfigureAwait(false);
        var read = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string>
        {
            [new ArtifactContentHandle("workspace")] = RootPath
        });
        return await read.ListFilesAsync(new ArtifactContentHandle("workspace"), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _storage.DisposeAsync();
}

internal sealed class QuotaWriteStream(Stream inner, Action<int> reserve, Action closed) : Stream
{
    private bool _closed;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_closed;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        reserve(count);
        inner.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        reserve(buffer.Length);
        inner.Write(buffer);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        reserve(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        reserve(count);
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        if (_closed) return;
        _closed = true;
        if (disposing) inner.Dispose();
        closed();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        await inner.DisposeAsync().ConfigureAwait(false);
        closed();
        GC.SuppressFinalize(this);
    }
}
