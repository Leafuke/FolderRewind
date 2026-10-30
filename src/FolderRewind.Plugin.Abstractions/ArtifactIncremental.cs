namespace FolderRewind.Plugin.Abstractions;

/// <summary>Physical input coverage, independent of the resulting logical Source coverage.</summary>
public enum ArtifactCaptureInputKind { Full = 0, ChangesOnly = 1 }

/// <summary>Host-selected, immutable capture context. A provider never elects a baseline.</summary>
public sealed record ArtifactIncrementalInput(
    Guid CaptureId,
    ArtifactCaptureInputKind Kind,
    string BoundaryFingerprint,
    string DetectionPolicy,
    string? BaseVersionId,
    string? BaseRepresentationId,
    ArtifactId? BaseRootArtifactId,
    IReadOnlyList<string> AddedFiles,
    IReadOnlyList<string> ModifiedFiles,
    IReadOnlyList<string> DeletedFiles,
    bool Checkpoint);

/// <summary>A context-independent reference to an immutable reconstruction recipe.</summary>
public sealed record ArtifactRecipeReference(
    ArtifactId ArtifactId, string RelativePath, long Offset, long Length,
    string Sha256, string Format, int FormatVersion);

public sealed record ArtifactMaterializedFile(
    string RelativePath, long Length, string Sha256, ArtifactRecipeReference Recipe);

public sealed record ArtifactFileViewRequest(
    ConfigSnapshot Config, FolderSnapshot Folder, string VersionId, ArtifactId RootArtifactId,
    IReadOnlyList<BackupArtifactSnapshot> ArtifactsTopologicallySorted, IArtifactReadService ArtifactRead);

/// <summary>
/// Optional extension on the registered materializer; not a separate capability registration.
/// Views reconstruct only from their fixed Artifact closure, using the same decoder as Restore.
/// The Host retains the capability/read leases until the view and all streams are disposed.
/// </summary>
public interface IArtifactFileViewProvider
{
    ValueTask<IArtifactFileView> OpenFileViewAsync(ArtifactFileViewRequest request, PluginInvocationContext context);
}

public interface IArtifactFileView : IAsyncDisposable
{
    IReadOnlyList<ArtifactMaterializedFile> Files { get; }
    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
}
