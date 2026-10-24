using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

/// <summary>Invocation-scoped input; session-owned paths never cross the public Merge contract.</summary>
internal sealed class SemanticMergeSourceView : IMergeSourceView, IDisposable
{
    private readonly MergeTreeManifest _tree;
    private readonly string _ownedRoot;
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _lifetime;
    private readonly MergeInputEntry[] _entries;
    private readonly SemanticMergeCoverage? _coverage;
    private readonly List<SemanticMergeStream> _streams = [];
    private bool _disposed;
    private int _opening;

    internal SemanticMergeSourceView(MergeTreeManifest tree, string ownedRoot, CancellationToken token, SemanticMergeCoverage? coverage = null)
    {
        _tree = tree; _ownedRoot = Path.GetFullPath(ownedRoot);
        _coverage = coverage;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); _token = _lifetime.Token;
        _entries = tree.Files.Select(p => new MergeInputEntry(p.Key, false, p.Value.Length, p.Value.Digest))
            .Concat(tree.Directories.Select(p => new MergeInputEntry(p, true, 0, string.Empty)))
            .OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public ValueTask<MergePathCoverage> InspectPathAsync(string relativePath, CancellationToken cancellationToken)
    {
        RequireActive(); cancellationToken.ThrowIfCancellationRequested(); Canonical(relativePath);
        var entry = _tree.Files.TryGetValue(relativePath, out var file) ? new MergeInputEntry(relativePath, false, file.Length, file.Digest)
            : _tree.Directories.Contains(relativePath) ? new MergeInputEntry(relativePath, true, 0, string.Empty) : null;
        var presence = entry is not null ? MergePathPresence.Present : _coverage?.Inspect(relativePath) ?? MergePathPresence.Unknown;
        if (presence == MergePathPresence.Absent && !_tree.HasCompleteDirectoryInventory)
            presence = MergePathPresence.Unknown;
        return ValueTask.FromResult(new MergePathCoverage(relativePath, presence, entry, []));
    }
    public ValueTask<MergeCoverageResult> CheckCoverageAsync(MergeCoverageQuery query, CancellationToken cancellationToken)
    {
        RequireActive(); cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
        return ValueTask.FromResult(_coverage?.Check(query, linked.Token)
            ?? new(query.RequirementId, string.Empty, MergeCoverageState.Unknown, []));
    }

    public ValueTask<MergeInputPage> ReadPageAsync(string? cursor, int count, CancellationToken cancellationToken)
    {
        RequireActive(); cancellationToken.ThrowIfCancellationRequested();
        if (count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(count));
        var start = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || start < 0 || start > _entries.Length)) throw new InvalidDataException("Invalid Merge inventory cursor.");
        var page = _entries.Skip(start).Take(count).ToArray();
        var next = start + page.Length;
        return ValueTask.FromResult(new MergeInputPage(Array.AsReadOnly(page), next < _entries.Length ? next.ToString(CultureInfo.InvariantCulture) : null));
    }

    public async ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        RequireActive(); cancellationToken.ThrowIfCancellationRequested();
        Canonical(relativePath);
        if (!_tree.Files.TryGetValue(relativePath, out var value)) throw new UnauthorizedAccessException("Merge input is outside the fixed view.");
        return await OpenVerifiedAsync(value, cancellationToken).ConfigureAwait(false);
    }
    internal async ValueTask<Stream> OpenVerifiedAsync(MergeFileValue value, CancellationToken cancellationToken)
    {
        lock (_streams)
        {
            RequireActive(); cancellationToken.ThrowIfCancellationRequested();
            _streams.RemoveAll(stream => stream.IsClosed);
            if (_streams.Count + _opening >= 64) throw new IOException("Merge input exceeds the open-stream budget.");
            _opening++;
        }
        try
        {
            RequireOwned(value.Handle, _ownedRoot);
            var file = new FileStream(value.Handle, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
                if (file.Length != value.Length || await DigestAsync(file, linked.Token).ConfigureAwait(false) != value.Digest)
                    throw new InvalidDataException("Merge input changed after preparation.");
                file.Position = 0;
                lock (_streams)
                {
                    RequireActive();
                    _streams.RemoveAll(stream => stream.IsClosed);
                    if (_streams.Count >= 64) throw new IOException("Merge input exceeds the open-stream budget.");
                    var stream = new SemanticMergeStream(file, RequireActive, null);
                    _streams.Add(stream); return stream;
                }
            }
            catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally { lock (_streams) _opening--; }
    }

    internal static void Canonical(string path)
    {
        if (ArtifactPathRules.NormalizeRelativePath(path) != path)
            throw new InvalidDataException("Merge path is not canonical.");
        _ = RestoreStagingProposalValidator.ValidateAndFreeze(new([new(path, ReadOnlyMemory<byte>.Empty)], []), _ => true);
    }

    internal static void RequireOwned(string path, string root)
    {
        path = Path.GetFullPath(path); root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Merge content is not session-owned.");
        for (var current = path; ; current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Merge content traverses a link.");
            if (StringComparer.OrdinalIgnoreCase.Equals(current, root)) break;
        }
    }

    internal static async Task<string> DigestAsync(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("folderrewind/file/1\0"));
        var buffer = new byte[65536]; int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static MergeFileValue Seal(string path, Stream input, CancellationToken token)
    {
        if (input is FileStream file) file.Flush(true); else input.Flush();
        input.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("folderrewind/file/1\0"));
        var buffer = new byte[65536]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return new(path, Convert.ToHexString(hash.GetHashAndReset()), input.Length);
    }

    private void RequireActive() { ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); }
    public void Dispose()
    {
        lock (_streams)
        {
            if (_disposed) return; _disposed = true;
            _lifetime.Cancel();
            foreach (var stream in _streams) stream.Dispose();
            _streams.Clear();
            _lifetime.Dispose();
        }
    }
}

/// <summary>Restricted stream which is revoked with its invocation; writes reserve real volume space.</summary>
internal sealed class SemanticMergeStream(Stream inner, Action requireActive, Func<long, IDisposable>? reserve, Action<Stream>? seal = null) : Stream
{
    private bool _disposed;
    internal bool IsClosed => _disposed;
    private void Check() { ObjectDisposedException.ThrowIf(_disposed, this); requireActive(); }
    public override bool CanRead => !_disposed && reserve is null && inner.CanRead;
    public override bool CanSeek => !_disposed && inner.CanSeek;
    public override bool CanWrite => !_disposed && reserve is not null && inner.CanWrite;
    public override long Length { get { Check(); return inner.Length; } }
    public override long Position { get { Check(); return inner.Position; } set { Check(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); inner.Position = value; } }
    private void CheckRead() { Check(); if (!CanRead) throw new NotSupportedException("Merge write streams cannot be read before sealing."); }
    public override int Read(byte[] buffer, int offset, int count) { CheckRead(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { CheckRead(); return inner.Read(buffer); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { CheckRead(); var count = await inner.ReadAsync(buffer, token).ConfigureAwait(false); Check(); return count; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override long Seek(long offset, SeekOrigin origin) { Check(); return inner.Seek(offset, origin); }
    public override void SetLength(long value) => throw new NotSupportedException();
    private IDisposable Reserve(int count)
    {
        Check(); if (!CanWrite) throw new NotSupportedException();
        return reserve!(checked(inner.Position + count));
    }
    public override void Write(byte[] buffer, int offset, int count) { using var budget = Reserve(count); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { using var budget = Reserve(buffer.Length); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { using var budget = Reserve(buffer.Length); await inner.WriteAsync(buffer, token).ConfigureAwait(false); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Flush() { Check(); inner.Flush(); }
    public override Task FlushAsync(CancellationToken token) { Check(); return inner.FlushAsync(token); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { seal?.Invoke(inner); }
            finally { inner.Dispose(); }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try { seal?.Invoke(inner); }
            finally { await inner.DisposeAsync().ConfigureAwait(false); }
        }
        GC.SuppressFinalize(this);
    }
}

internal sealed class SemanticMergeOutputBudget(long initialBytes = 0, int initialFiles = 0)
{
    private readonly object _gate = new();
    private long _bytes = initialBytes;
    private int _files = initialFiles;
    internal void Charge(long bytes, int files = 0)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > ArtifactTransformLimits.Default.MaximumBytes - _bytes
                || files < 0 || files > ArtifactTransformLimits.Default.MaximumFiles - _files)
                throw new IOException("Merge output exceeds the Source budget.");
            _bytes += bytes; _files += files;
        }
    }
}

internal sealed class SemanticMergeOutputWorkspace : IMergeOutputWorkspace, IDisposable
{
    private readonly string _root;
    private readonly IReadOnlyDictionary<MergeInputSide, MergeTreeManifest> _inputs;
    private readonly HashSet<string> _claimed, _outputs, _disposedPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MergeFileValue> _reused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (SemanticMergeStream Stream, string Path)> _written = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MergeFileValue> _sealed = new(StringComparer.Ordinal);
    private readonly SemanticMergeSourceView _reads;
    private readonly object _gate = new();
    private readonly CancellationToken _token;
    private readonly SemanticMergeOutputBudget _budget;
    private bool _disposed;
    private bool _completing;

    internal SemanticMergeOutputWorkspace(string root, IReadOnlyDictionary<MergeInputSide, MergeTreeManifest> inputs,
        IEnumerable<string> claimed, IEnumerable<string> outputs, CancellationToken token, SemanticMergeOutputBudget? budget = null)
    {
        _root = root; _inputs = inputs; _claimed = claimed.ToHashSet(StringComparer.Ordinal); _outputs = outputs.ToHashSet(StringComparer.Ordinal); _token = token;
        _budget = budget ?? new();
        HostArtifactWorkspace.CreatePrivateDirectory(root);
        _reads = new(MergeTreeManifest.Empty, root, token);
    }
    private void Check(string path, CancellationToken token, bool writing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
        if (_completing) throw new InvalidOperationException("Merge output is already sealed.");
        SemanticMergeSourceView.Canonical(path);
        if (writing ? !_outputs.Contains(path) : !_claimed.Contains(path)) throw new UnauthorizedAccessException("Merge output was not declared.");
        if (!_disposedPaths.Add(path)) throw new InvalidDataException("Merge output has multiple dispositions.");
    }
    public ValueTask ReuseAsync(string relativePath, MergeInputSide side, string sourceRelativePath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Check(relativePath, cancellationToken, true);
            if (!_claimed.Contains(sourceRelativePath) || !_inputs.TryGetValue(side, out var tree) || !tree.Files.TryGetValue(sourceRelativePath, out var file))
                throw new UnauthorizedAccessException("Merge reuse is outside the group's fixed inputs.");
            _budget.Charge(file.Length, 1);
            _reused.Add(relativePath, file); return ValueTask.CompletedTask;
        }
    }
    public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Check(relativePath, cancellationToken, true);
            _budget.Charge(0, 1);
            var path = ArtifactPathRules.ResolveUnderRoot(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_written.Values.Count(v => !v.Stream.IsClosed) >= 64) throw new IOException("Too many open Merge writers.");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
            var stream = new SemanticMergeStream(file, () => { ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); }, end =>
            {
                lock (_lengths)
                {
                    var old = _lengths.GetValueOrDefault(relativePath);
                    var extra = Math.Max(0, end - old);
                    var reservation = ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(path, extra));
                    try { _budget.Charge(extra); }
                    catch { reservation.Dispose(); throw; }
                    _lengths[relativePath] = Math.Max(old, end); return reservation;
                }
            }, input =>
            {
                lock (_gate)
                    if (!_disposed && !_token.IsCancellationRequested)
                        _sealed.Add(relativePath, SemanticMergeSourceView.Seal(path, input, _token));
            });
            _written.Add(relativePath, (stream, path)); return ValueTask.FromResult<Stream>(stream);
        }
    }
    public async ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        RequireActive(); cancellationToken.ThrowIfCancellationRequested();
        SemanticMergeSourceView.Canonical(relativePath);
        MergeFileValue value;
        lock (_gate)
        {
            if (!_written.TryGetValue(relativePath, out var written) || !written.Stream.IsClosed || !_sealed.TryGetValue(relativePath, out value!))
                throw new InvalidOperationException("Only closed, sealed files generated by this output group can be read.");
        }
        return await _reads.OpenVerifiedAsync(value, cancellationToken).ConfigureAwait(false);
    }
    private void RequireActive() { ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); }
    public ValueTask DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        lock (_gate)
        { Check(relativePath, cancellationToken, false); return ValueTask.CompletedTask; }
    }

    internal async Task<MergeTreeManifest> CompleteAsync()
    {
        lock (_gate)
        {
            RequireActive(); _completing = true;
            if (!_claimed.IsSubsetOf(_disposedPaths)) throw new InvalidDataException("Merge provider omitted an input disposition.");
            if (_written.Values.Any(value => !value.Stream.IsClosed)) throw new InvalidDataException("Merge provider left an output stream open.");
        }
        HostArtifactWorkspace.ValidateOwnedDirectory(_root, _token);
        var generated = await MergeTreeManifest.ReadAsync(_root, _ => true, _token).ConfigureAwait(false);
        if (!generated.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(_written.Keys)) throw new InvalidDataException("Merge provider wrote undeclared output.");
        if (generated.Files.Any(p => !_sealed.TryGetValue(p.Key, out var value) || value.Digest != p.Value.Digest || value.Length != p.Value.Length))
            throw new InvalidDataException("Merge output changed after sealing.");
        return MergeTreeManifest.Create(_reused.Concat(generated.Files));
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var item in _written.Values) item.Stream.Dispose();
            _reads.Dispose();
        }
    }
}
