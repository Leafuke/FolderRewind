using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FolderRewind.History.Representation;

public enum AssessmentDepth
{
    Fast = 0,
    Deep = 1
}

public enum HistoryReadiness
{
    Ready = 0,
    PreparationRequired = 1,
    Blocked = 2,
    Unavailable = 3
}

public enum MaterializationFidelity
{
    Exact = 0,
    Overlay = 1,
    Unknown = 2
}

public enum RepresentationEvidenceKind
{
    LocalReplica = 0,
    SharedReplica = 1,
    DeepVerification = 2,
    Dependency = 3,
    Plugin = 4
}

public sealed record RepresentationEvidence(
    RepresentationEvidenceKind Kind,
    string Detail,
    DateTimeOffset ObservedAtUtc,
    ReplicaAvailabilityObservation Availability,
    ReplicaIntegrityObservation Integrity);

public sealed record RepresentationAssessment
{
    public RepresentationAssessment(
        RepresentationId representationId,
        HistoryReadiness readiness,
        MaterializationFidelity fidelity,
        IEnumerable<RepresentationEvidence>? evidence,
        IEnumerable<string>? diagnostics,
        string? selectedLocalPath = null)
    {
        RepresentationId = representationId;
        Readiness = readiness;
        Fidelity = fidelity;
        Evidence = evidence is null ? [] : [.. evidence];
        Diagnostics = diagnostics is null ? [] : [.. diagnostics];
        SelectedLocalPath = selectedLocalPath;
    }

    public RepresentationId RepresentationId { get; }
    public HistoryReadiness Readiness { get; }
    public MaterializationFidelity Fidelity { get; }
    public ImmutableArray<RepresentationEvidence> Evidence { get; }
    public ImmutableArray<string> Diagnostics { get; }
    public string? SelectedLocalPath { get; }
}

public sealed record VersionAssessment(
    VersionId VersionId,
    AssessmentDepth Depth,
    MaterializationFidelity RequiredFidelity,
    HistoryReadiness Readiness,
    RepresentationAssessment? Selected,
    ImmutableArray<RepresentationAssessment> Candidates);

