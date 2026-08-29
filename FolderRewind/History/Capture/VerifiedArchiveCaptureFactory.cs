using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Capture;

/// <summary>
/// Creates the durable candidate shape accepted by Native History after the caller has
/// verified the final archive. State fingerprints remain optional until capture can
/// produce a trustworthy logical-state fingerprint.
/// </summary>
public static class VerifiedArchiveCaptureFactory
{
    public static SourceCaptureResult Create(
        SourceId sourceId,
        CaptureScope captureScope,
        string payloadPath,
        RepresentationKind kind,
        string format,
        IReadOnlyDictionary<string, SourceCaptureFileState> currentStates,
        SourceCaptureBaseline? baseline,
        IEnumerable<RepresentationId> dependencies,
        int consecutiveSmartCaptures,
        VersionId? expectedBaseVersionId = null,
        IEnumerable<string>? deletedFiles = null,
        string? stateFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(currentStates);
        ArgumentNullException.ThrowIfNull(dependencies);
        var absolutePath = Path.GetFullPath(payloadPath);
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("Verified capture payload is missing.", absolutePath);

        var representationId = RepresentationId.New();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fileName"] = Path.GetFileName(absolutePath)
        };
        var deleted = deletedFiles?.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray() ?? [];
        if (deleted.Length > 0)
            metadata["deletedFiles"] = string.Join('\n', deleted);

        var representation = new RepresentationCandidate(
            representationId,
            kind,
            format,
            dependencies,
            captureScope == CaptureScope.PartialSource ? RestoreStrategy.Overlay : RestoreStrategy.Exact,
            logicalSha256: null,
            stateFingerprint,
            metadata);
        var localReplica = new LocalReplicaCandidate(
            LocalReplicaId.New(),
            representationId,
            LocalReplicaLocator.ControlledAbsolute(absolutePath),
            CapturePayloadState.VerifiedFinal,
            DateTimeOffset.UtcNow);
        var payload = new CapturePayloadCandidate(
            absolutePath,
            CapturePayloadState.VerifiedFinal,
            new FileInfo(absolutePath).Length,
            ExpectedStorageSha256: null);

        return new SourceCaptureResult(
            sourceId,
            SourceCaptureOutcome.Captured,
            captureScope,
            stateFingerprint,
            existingVersionId: null,
            representation,
            localReplica,
            payload,
            expectedWorkspaceRevision: -1,
            expectedBaseVersionId,
            new DeleteUncommittedArchiveCleanupHandle(absolutePath),
            diagnostics: [],
            baselineCandidate: new SourceCaptureBaselineCandidate(
                baseline?.Revision ?? SourceCaptureBaselineCache.MissingRevision,
                absolutePath,
                consecutiveSmartCaptures,
                currentStates.ToImmutableSortedDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)));
    }

    private sealed class DeleteUncommittedArchiveCleanupHandle(string path) : ICaptureCleanupHandle
    {
        public ValueTask CleanupAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                File.Delete(path);
            }
            return ValueTask.CompletedTask;
        }
    }
}
