using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
    ImmutableSortedDictionary<string, SourceCaptureFileState> FileStates,
    string BoundaryFingerprint = "")
{
    public const int CurrentFormatVersion = 4;
    public int FormatVersion => CurrentFormatVersion;
    public Guid? ArtifactRootId { get; init; }
    public string? EvidenceIdentity { get; init; }
    public bool RequiresContentValidation { get; init; }
    public Guid ContentRealizationId { get; init; }
    public string SourcePathFingerprint { get; init; } = "";
    public ImmutableArray<string> Directories { get; init; } = [];
}

public sealed record SourceCaptureBaselineCandidate(
    long ExpectedRevision,
    string PayloadPath,
    int ConsecutiveSmartCaptures,
    ImmutableSortedDictionary<string, SourceCaptureFileState> FileStates,
    string BoundaryFingerprint = "")
{
    public Guid? ArtifactRootId { get; init; }
    public string? EvidenceIdentity { get; init; }
    public bool RequiresContentValidation { get; init; }
    public Guid ContentRealizationId { get; init; }
    public string SourcePathFingerprint { get; init; } = "";
    public ImmutableArray<string> Directories { get; init; } = [];
}

public static class SourceCaptureBaselinePolicy
{
    public static bool IsApplicableToWorkspace(
        SourceCaptureBaseline baseline,
        HistoryWorkspace? workspace)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var workspaceBaseline = workspace?.SourceBaselines
            .FirstOrDefault(item => item.SourceId == baseline.SourceId);
        return workspaceBaseline is
            {
                Relation: WorkspaceBaselineRelation.Exact or WorkspaceBaselineRelation.Derived,
                RequiresFullCapture: false,
                BaseVersionId: { } workspaceVersionId
            }
            && workspaceVersionId == baseline.BaseVersionId;
    }
}

public sealed class SourceCaptureBaselineCache : IDisposable
{
    public const long MissingRevision = -1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
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
            using var timing = HistoryOperationDiagnostics.Current?.Measure("capture-baseline-save");
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
                candidate.FileStates,
                candidate.BoundaryFingerprint)
            { ArtifactRootId = candidate.ArtifactRootId ?? (committedRepresentation.Kind == RepresentationKind.PluginArtifact
                    && committedRepresentation.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var rootText)
                    && Guid.TryParse(rootText, out var parsedRoot) ? parsedRoot : null),
                EvidenceIdentity = candidate.EvidenceIdentity ?? committedRepresentation.RepresentationSpecificMetadata.GetValueOrDefault("captureEvidenceSha256"),
                RequiresContentValidation = candidate.RequiresContentValidation,
                ContentRealizationId = candidate.ContentRealizationId, SourcePathFingerprint = candidate.SourcePathFingerprint,
                Directories = candidate.Directories };
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

    public async Task<bool> RemoveAsync(
        SourceId sourceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = LoadInsideGate(sourceId);
            if (current is null) return expectedRevision == MissingRevision;
            if (current.Revision != expectedRevision) return false;
            var path = PathFor(sourceId);
            if (!File.Exists(path)) return true;
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            File.Delete(path);
            return true;
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
        using var timing = HistoryOperationDiagnostics.Current?.Measure("capture-baseline-load");
        var path = PathFor(sourceId);
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 64L * 1024 * 1024) return null;
            var bytes = FolderRewind.Plugin.Runtime.Artifacts.ArtifactMetadataFile.ReadBytes(path, 64 * 1024 * 1024);
            HistoryOperationDiagnostics.Current?.Count("capture-baseline-read-bytes", bytes.Length);
            if (ReadFormatVersion(bytes) is not (2 or SourceCaptureBaseline.CurrentFormatVersion))
                return null;
            var value = JsonSerializer.Deserialize<SourceCaptureBaseline>(bytes, JsonOptions);
            if (value is null
                || value.SourceId != sourceId
                || value.Revision < 0
                || value.FileStates is null || value.Directories.IsDefault
                || !Path.IsPathFullyQualified(value.PayloadPath))
                return null;
            if (value.ArtifactRootId is not null) SourceDirectorySnapshot.Validate(value.Directories, value.FileStates.Keys);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // This cache is only an optimization. Any unreadable state forces the next capture to Full.
            return null;
        }
    }

    // Check the envelope without retaining a second DOM for the full file-state table.
    // JsonDocument.TryGetProperty used the last duplicate property; retain that behavior.
    private static int? ReadFormatVersion(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        int? version = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return null;
            bool isVersion = reader.ValueTextEquals("formatVersion"u8);
            if (!reader.Read()) return null;
            if (isVersion)
                version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) ? number : null;
            reader.Skip();
        }
        return version;
    }

    private string PathFor(SourceId sourceId) => Path.Combine(_root, sourceId + ".json");
}
