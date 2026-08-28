using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Capture;

public sealed record SourceCaptureFileState(long Size, DateTime LastWriteTimeUtc);

public sealed record SourceCaptureBaseline(
    SourceId SourceId,
    long Revision,
    VersionId BaseVersionId,
    RepresentationId BaseRepresentationId,
    RepresentationKind BaseRepresentationKind,
    string PayloadPath,
    int ConsecutiveSmartCaptures,
    ImmutableSortedDictionary<string, SourceCaptureFileState> FileStates)
{
    public const int CurrentFormatVersion = 1;
    public int FormatVersion => CurrentFormatVersion;
}

public sealed record SourceCaptureBaselineCandidate(
    long ExpectedRevision,
    string PayloadPath,
    int ConsecutiveSmartCaptures,
    ImmutableSortedDictionary<string, SourceCaptureFileState> FileStates);

public sealed class SourceCaptureBaselineCache : IDisposable
{
    public const long MissingRevision = -1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SourceCaptureBaselineCache(string localStateRoot)
    {
        _root = Path.Combine(
            Path.GetFullPath(localStateRoot ?? throw new ArgumentNullException(nameof(localStateRoot))),
            "capture-baselines");
    }

    public async Task<SourceCaptureBaseline?> LoadAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadInsideGate(sourceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        SourceId sourceId,
        SourceCaptureBaselineCandidate candidate,
        VersionId committedVersionId,
        VersionRepresentation committedRepresentation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.FileStates);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = LoadInsideGate(sourceId);
            var currentRevision = current?.Revision ?? MissingRevision;
            if (currentRevision != candidate.ExpectedRevision)
                throw new InvalidOperationException("Source capture baseline changed before the Native History commit completed.");
            var baseline = new SourceCaptureBaseline(
                sourceId,
                checked(candidate.ExpectedRevision + 1),
                committedVersionId,
                committedRepresentation.RepresentationId,
                committedRepresentation.Kind,
                Path.GetFullPath(candidate.PayloadPath),
                Math.Max(0, candidate.ConsecutiveSmartCaptures),
                candidate.FileStates);
            await AtomicFileService.WriteAsync(
                PathFor(sourceId),
                (stream, token) => JsonSerializer.SerializeAsync(stream, baseline, JsonOptions, token),
                cancellationToken).ConfigureAwait(false);
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

    private SourceCaptureBaseline? LoadInsideGate(SourceId sourceId)
    {
        var path = PathFor(sourceId);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("formatVersion", out var formatVersion)
                || !formatVersion.TryGetInt32(out var parsedFormatVersion)
                || parsedFormatVersion != SourceCaptureBaseline.CurrentFormatVersion)
                return null;
            var value = JsonSerializer.Deserialize<SourceCaptureBaseline>(bytes, JsonOptions);
            if (value is null
                || value.SourceId != sourceId
                || value.Revision < 0
                || value.FileStates is null
                || !Path.IsPathFullyQualified(value.PayloadPath))
                return null;
            return value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // This cache is only an optimization. Any unreadable state forces the next capture to Full.
            return null;
        }
    }

    private string PathFor(SourceId sourceId) => Path.Combine(_root, sourceId + ".json");
}
