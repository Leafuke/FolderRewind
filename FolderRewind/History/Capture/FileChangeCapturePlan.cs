using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.History.Capture;

public sealed record FileChangeCapturePlan(ArtifactIncrementalInput Input,
    ImmutableSortedDictionary<string, SourceCaptureFileState> FileStates,
    SourceCaptureBaseline? Baseline)
{
    public static FileChangeCapturePlan Create(SourceCaptureResult capture, SourceCaptureBaseline? baseline, bool requestCheckpoint = false)
    {
        var states = capture.BaselineCandidate?.FileStates ?? throw new InvalidOperationException("File-state witness required.");
        if (capture.CaptureScope != CaptureScope.FullSource) throw new InvalidOperationException("Incremental policy requires a complete effective boundary.");
        var changesOnly = capture.RepresentationCandidate?.Kind == RepresentationKind.CoreSmartDelta;
        return Create(states, baseline, capture.EffectiveSourceBoundary, changesOnly, requestCheckpoint);
    }

    public static FileChangeCapturePlan Create(ImmutableSortedDictionary<string, SourceCaptureFileState> states,
        SourceCaptureBaseline? baseline, EffectiveSourceBoundarySnapshot boundary, bool changesOnly, bool requestCheckpoint = false)
    {
        ArtifactId? baseRoot = null;
        if (changesOnly)
        {
            if (baseline?.BaseRepresentationKind != RepresentationKind.PluginArtifact || baseline.ArtifactRootId is not { } root
                || baseline.BoundaryFingerprint != boundary.Fingerprint)
                throw new InvalidOperationException("Changes-only input requires a matching semantic baseline.");
            baseRoot = new(root);
        }
        var old = changesOnly ? baseline!.FileStates : ImmutableSortedDictionary<string, SourceCaptureFileState>.Empty;
        var diff = ScopeAwareSourceCaptureDiff.Compute(old, states, _ => true);
        return new(new(Guid.NewGuid(), changesOnly ? ArtifactCaptureInputKind.ChangesOnly : ArtifactCaptureInputKind.Full,
            boundary.Fingerprint, "size-mtime-v1",
            changesOnly ? baseline!.BaseVersionId.ToString() : null,
            changesOnly ? baseline!.BaseRepresentationId.ToString() : null, baseRoot,
            diff.AddedFiles, diff.ModifiedFiles, diff.DeletedFiles,
            !changesOnly || requestCheckpoint), states, changesOnly ? baseline : null);
    }
}
