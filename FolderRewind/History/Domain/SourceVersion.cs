using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace FolderRewind.History.Domain;

public enum CaptureScope
{
    FullSource = 0,
    PartialSource = 1
}

public enum CaptureOutcome
{
    Captured = 0,
    Recovered = 1
}

public sealed record SourceDescriptorSnapshot(string DisplayName, string PathHint);

public sealed record SourceVersion
{
    public SourceVersion(
        VersionId versionId,
        HistoryConfigId configId,
        SourceId sourceId,
        IEnumerable<VersionId>? parentVersionIds,
        DateTimeOffset createdAtUtc,
        RunId? createdByRunId,
        CaptureScope captureScope,
        CaptureOutcome outcome,
        IEnumerable<HistoryDiagnostic>? diagnostics,
        SourceDescriptorSnapshot sourceDescriptorSnapshot,
        string? stateFingerprint,
        HistoryProvenance provenance,
        EffectiveSourceBoundarySnapshot? effectiveSourceBoundary = null)
        : this(
            versionId,
            configId,
            sourceId,
            DomainCollections.Freeze(parentVersionIds),
            createdAtUtc,
            createdByRunId,
            captureScope,
            outcome,
            DomainCollections.Freeze(diagnostics),
            sourceDescriptorSnapshot,
            stateFingerprint,
            provenance,
            effectiveSourceBoundary)
    {
    }

    [JsonConstructor]
    public SourceVersion(
        VersionId versionId,
        HistoryConfigId configId,
        SourceId sourceId,
        ImmutableArray<VersionId> parentVersionIds,
        DateTimeOffset createdAtUtc,
        RunId? createdByRunId,
        CaptureScope captureScope,
        CaptureOutcome outcome,
        ImmutableArray<HistoryDiagnostic> diagnostics,
        SourceDescriptorSnapshot sourceDescriptorSnapshot,
        string? stateFingerprint,
        HistoryProvenance provenance,
        EffectiveSourceBoundarySnapshot? effectiveSourceBoundary = null)
    {
        VersionId = versionId;
        ConfigId = configId;
        SourceId = sourceId;
        ParentVersionIds = parentVersionIds.IsDefault ? ImmutableArray<VersionId>.Empty : parentVersionIds;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        CreatedByRunId = createdByRunId;
        CaptureScope = captureScope;
        Outcome = outcome;
        Diagnostics = diagnostics.IsDefault ? ImmutableArray<HistoryDiagnostic>.Empty : diagnostics;
        SourceDescriptorSnapshot = sourceDescriptorSnapshot
            ?? throw new ArgumentNullException(nameof(sourceDescriptorSnapshot));
        StateFingerprint = string.IsNullOrWhiteSpace(stateFingerprint) ? null : stateFingerprint.Trim();
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        EffectiveSourceBoundary = effectiveSourceBoundary ?? EffectiveSourceBoundarySnapshot.All;
    }

    public VersionId VersionId { get; }
    public HistoryConfigId ConfigId { get; }
    public SourceId SourceId { get; }
    public ImmutableArray<VersionId> ParentVersionIds { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public RunId? CreatedByRunId { get; }
    public CaptureScope CaptureScope { get; }
    public CaptureOutcome Outcome { get; }
    public ImmutableArray<HistoryDiagnostic> Diagnostics { get; }
    public SourceDescriptorSnapshot SourceDescriptorSnapshot { get; }
    public string? StateFingerprint { get; }
    public HistoryProvenance Provenance { get; }
    public EffectiveSourceBoundarySnapshot EffectiveSourceBoundary { get; }
    public string EffectiveSourceBoundaryFingerprint => EffectiveSourceBoundary.Fingerprint;
}
