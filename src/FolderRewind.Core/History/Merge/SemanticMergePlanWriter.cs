using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

internal sealed class SemanticMergePlanWriter : IMergePlanWriter, IDisposable
{
    private readonly MergeSessionStore _store;
    private readonly MergeSession _session;
    private readonly SourceId _source;
    private readonly Func<string, bool> _include;
    private readonly MergeLimits _limits;
    private readonly HashSet<string> _files, _directories;
    private readonly object _gate = new();
    private readonly Dictionary<string, MergeOutputGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (SemanticMergeStream Stream, string Path)> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _dataLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MergeFileValue> _sealedData = new(StringComparer.Ordinal);
    private readonly SemanticMergeSourceView _reads;
    private readonly string _root, _inputSignature;
    private readonly CancellationToken _token;
    private long _metadataBytes;
    private int _subjects;
    private bool _disposed;
    private bool _completing;

    internal SemanticMergePlanWriter(MergeSessionStore store, MergeSession session, SourceId source,
        MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t, Func<string, bool> include, CancellationToken token)
    {
        _store = store; _session = session; _source = source; _include = include; _token = token; _limits = MergeLimits.Default;
        _files = b.Files.Keys.Concat(o.Files.Keys).Concat(t.Files.Keys).ToHashSet(StringComparer.Ordinal);
        _directories = b.Directories.Concat(o.Directories).Concat(t.Directories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identities = session.Plan.Sources.Single(s => s.SourceId == source);
        _inputSignature = session.Plan.ProviderVersion + "\0" + session.Plan.PolicyVersion + "\0" + session.Plan.SemanticProvider?.Kind + "\0" + source + "\0"
            + identities.Base?.VersionId + ":" + b.InputRepresentationId + ":" + identities.Base?.EffectiveSourceBoundaryFingerprint + "\0"
            + identities.Ours?.VersionId + ":" + o.InputRepresentationId + ":" + identities.Ours?.EffectiveSourceBoundaryFingerprint + "\0"
            + identities.Theirs?.VersionId + ":" + t.InputRepresentationId + ":" + identities.Theirs?.EffectiveSourceBoundaryFingerprint + "\0" + b.Digest + o.Digest + t.Digest;
        _root = Path.Combine(store.SessionDirectory(session.Id), session.Plan.Revision.ToString("N"), "semantic", source.ToString(), Guid.NewGuid().ToString("N"));
        store.BeginSemanticSource(session, source);
        HostArtifactWorkspace.CreatePrivateDirectory(_root);
        _reads = new(MergeTreeManifest.Empty, _root, token);
    }
    private void Check(CancellationToken token)
    { ObjectDisposedException.ThrowIf(_disposed, this); _token.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested(); }
    private void CheckWritable(CancellationToken token)
    { Check(token); if (_completing) throw new InvalidOperationException("Merge analysis is already sealed."); }
    private void Charge(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _limits.MaximumPlanBytes - _metadataBytes) throw new IOException("Merge analysis exceeds the metadata budget.");
            _metadataBytes += bytes;
        }
    }
    private T Freeze<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > _limits.MaximumRecordBytes) throw new IOException("Merge analysis record exceeds its budget.");
        Charge(bytes.Length);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Merge analysis record is empty.");
    }
    private static void Identity(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)) throw new InvalidDataException("Invalid opaque Merge identity.");
    }
    public ValueTask AddGroupAsync(MergeOutputGroup group, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CheckWritable(cancellationToken); group = Freeze(group); Identity(group.GroupId);
            if (_groups.Count >= ArtifactTransformLimits.Default.MaximumFiles) throw new IOException("Too many Merge output groups.");
            _groups.Add(group.GroupId, group); _store.AddSemanticGroup(_session, _source, group); return ValueTask.CompletedTask;
        }
    }
    public ValueTask ClaimPathsAsync(string groupId, IReadOnlyList<string> inputPaths, IReadOnlyList<string> outputPaths, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CheckWritable(cancellationToken);
            if (inputPaths.Count + outputPaths.Count > _limits.MaximumBatchSize || !_groups.ContainsKey(groupId))
                throw new InvalidDataException("Invalid Merge path batch or group.");
            var inputs = inputPaths.ToArray(); var outputs = outputPaths.ToArray();
            foreach (var path in inputs.Concat(outputs))
            {
                SemanticMergeSourceView.Canonical(path);
                if (!_include(path)) throw new UnauthorizedAccessException("Merge path escapes the Source boundary.");
                Charge(Encoding.UTF8.GetByteCount(path) + 32);
            }
            if (inputs.Any(path => !_files.Contains(path))) throw new InvalidDataException("Merge claimed an absent input file.");
            _store.ClaimSemanticPaths(_session, _source, groupId, inputs, outputs); return ValueTask.CompletedTask;
        }
    }
    public ValueTask AddSubjectsAsync(IReadOnlyList<MergeSubject> subjects, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CheckWritable(cancellationToken);
            if (subjects.Count > _limits.MaximumBatchSize || subjects.Count > _limits.MaximumSubjects - _subjects)
                throw new IOException("Merge subjects exceed their budget.");
            var conflicts = new List<MergeConflict>();
            foreach (var item in subjects)
            {
                var subject = Freeze(item); Identity(subject.SubjectId); Identity(subject.InputIdentity);
                if (!_groups.TryGetValue(subject.GroupId, out var group) || subject.AllowedDecisions.Any(d => !Enum.IsDefined(d) || d == MergeDecisionKind.Automatic)
                    || subject.AllowedDecisions.Count != subject.AllowedDecisions.Distinct().Count()
                    || subject.IsAutomatic && !subject.CanOverrideAutomatic && subject.AllowedDecisions.Count != 0
                    || subject.CanOverrideAutomatic && (!subject.IsAutomatic || subject.IsBlocked || subject.AllowedDecisions.Count == 0)
                    || subject.IsBlocked && (subject.IsAutomatic || subject.AllowedDecisions.Count != 0)
                    || !subject.IsAutomatic && !subject.IsBlocked && subject.AllowedDecisions.Count == 0)
                    throw new InvalidDataException("Invalid Merge subject decisions or group.");
                if (subject.SpatialLocations.Count > 128 || subject.SpatialLocations.Any(p =>
                    string.IsNullOrWhiteSpace(p.WorldId) || p.WorldId.Length > 4096 || p.LayerId?.Length > 256
                    || p.Bounds is { } b && (!double.IsFinite(b.MinX) || !double.IsFinite(b.MinY)
                        || !double.IsFinite(b.MaxX) || !double.IsFinite(b.MaxY) || b.MinX >= b.MaxX || b.MinY >= b.MaxY)))
                    throw new InvalidDataException("Invalid Merge spatial projection.");
                var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("folderrewind/semantic-conflict/1\0"
                    + _inputSignature + "\0" + JsonSerializer.Serialize(group) + "\0" + JsonSerializer.Serialize(subject))));
                conflicts.Add(new(signature, new(_source, [], subject.SubjectId), MergeConflictKind.ModifyModify, signature,
                    ImmutableSortedDictionary<string, MergeFileValue>.Empty, ImmutableSortedDictionary<string, MergeFileValue>.Empty,
                    ImmutableSortedDictionary<string, MergeFileValue>.Empty)
                { SemanticSubject = subject });
            }
            _store.AddSemanticConflicts(_session, conflicts); _subjects += subjects.Count; return ValueTask.CompletedTask;
        }
    }
    public ValueTask<Stream> OpenDataWriteAsync(string key, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            CheckWritable(cancellationToken); SemanticMergeSourceView.Canonical(key);
            if (_data.Count >= ArtifactTransformLimits.Default.MaximumFiles || _data.ContainsKey(key)) throw new InvalidDataException("Duplicate or excessive Merge plan data.");
            if (_data.Values.Count(v => !v.Stream.IsClosed) >= 64) throw new IOException("Too many open Merge plan writers.");
            Charge(Encoding.UTF8.GetByteCount(key) + 128);
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            var stream = new SemanticMergeStream(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true),
                () => Check(default), end =>
                {
                    lock (_gate)
                    {
                        var previous = _dataLengths.GetValueOrDefault(key); var extra = Math.Max(0, end - previous);
                        Charge(extra); _dataLengths[key] = Math.Max(end, previous);
                        return ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(path, extra));
                    }
                }, input => SealData(key, path, input));
            _data.Add(key, (stream, path)); return ValueTask.FromResult<Stream>(stream);
        }
    }
    private void SealData(string key, string path, Stream input)
    {
        lock (_gate)
        {
            if (_disposed || _token.IsCancellationRequested) return;
            _sealedData.Add(key, SemanticMergeSourceView.Seal(path, input, _token));
        }
    }
    public async ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken cancellationToken)
    {
        Check(cancellationToken); SemanticMergeSourceView.Canonical(key);
        MergeFileValue value;
        lock (_gate)
        {
            if (!_data.TryGetValue(key, out var written) || !written.Stream.IsClosed || !_sealedData.TryGetValue(key, out value!))
                throw new InvalidOperationException("Only closed, sealed data from this analysis can be read.");
        }
        return await _reads.OpenVerifiedAsync(value, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<HashSet<string>> CompleteAsync()
    {
        lock (_gate)
        {
            Check(default); _completing = true;
            if (_data.Values.Any(item => !item.Stream.IsClosed)) throw new InvalidDataException("Merge analysis left a plan stream open.");
        }
        var paths = _store.SemanticPaths(_session, _source);
        if (paths.Count > ArtifactTransformLimits.Default.MaximumFiles || paths.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            throw new InvalidDataException("Merge path budget or case ownership conflict.");
        var inputs = paths.Where(p => p.Input).Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        if (_groups.Keys.Any(id => !paths.Any(path => path.Group == id))
            || paths.Any(p => p.Output && _files.Contains(p.Path) && !p.Input))
            throw new InvalidDataException("Merge group did not claim the files it replaces.");
        var outputs = paths.Where(p => p.Output).Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (outputs.Overlaps(_directories)) throw new InvalidDataException("Merge output replaces an unclaimed directory.");
        var finalPaths = _files.Except(inputs).Concat(outputs).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in finalPaths)
            for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (finalPaths.Contains(path[..slash])) throw new InvalidDataException("Merge output has a file/directory conflict.");
                if (slash == 0) break;
            }
        foreach (var pair in _data)
        {
            await using var input = await OpenDataReadAsync(pair.Key, _token).ConfigureAwait(false);
            var value = _sealedData[pair.Key];
            _store.SaveSemanticData(_session, _source, pair.Key, value.Handle, value.Length, value.Digest);
        }
        return inputs;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var item in _data.Values) item.Stream.Dispose();
            _reads.Dispose();
        }
    }
}
