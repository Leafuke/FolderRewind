using System.Text.Json;

namespace FolderRewind.Plugin.Abstractions;

public enum ArtifactCompleteness
{
    Complete = 0,
    Partial = 1
}

public enum CoreCaptureMode
{
    Full = 0,
    Smart = 1,
    Rolling = 2
}

public enum ArtifactTransformFailureBehavior
{
    KeepPrimaryWithWarnings = 0,
    RequireTransform = 1
}

public enum RestoreMode
{
    Clean = 0,
    Overwrite = 1
}

public sealed record ArtifactTransformPolicy(
    ArtifactTransformerId TransformerId,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    ArtifactTransformFailureBehavior FailureBehavior);

public sealed record ArtifactFileEntry(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record BackupArtifactSnapshot(
    ArtifactId ArtifactId,
    ArtifactFormatRef Format,
    int FormatVersion,
    RestoreStrategyId RestoreStrategyId,
    ArtifactContentHandle Content,
    string LogicalSha256,
    long LogicalSize,
    ArtifactCompleteness Completeness,
    CoreCaptureMode CoreCaptureMode,
    string ConfigId,
    Guid FolderId,
    IReadOnlyList<ArtifactId> Dependencies);

public interface IArtifactReadService
{
    ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(
        ArtifactContentHandle content,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(
        ArtifactContentHandle content,
        string relativePath,
        CancellationToken cancellationToken);
}

public sealed record ArtifactStagingAllocation(
    ArtifactId ArtifactId,
    ArtifactStagingHandle Staging);

public interface IArtifactTransformStaging
{
    ValueTask<ArtifactStagingAllocation> CreateArtifactAsync(CancellationToken cancellationToken);

    ValueTask<Stream> OpenWriteAsync(
        ArtifactStagingHandle staging,
        string relativePath,
        CancellationToken cancellationToken);
}

public interface IRestoreMaterializationWorkspace
{
    ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken);
}

public sealed record PluginProgress(double Fraction, string Stage, string Message);

public interface IPluginOperationProgress
{
    void Report(PluginProgress progress);
}

public interface IBackupArtifactTransformerCapability : IPluginCapability
{
    ArtifactTransformerId TransformerId { get; }
    ValueTask<ArtifactTransformResult> TransformAsync(
        ArtifactTransformRequest request,
        PluginInvocationContext context);
}

public sealed record ArtifactTransformRequest(
    ConfigSnapshot Config,
    FolderSnapshot Folder,
    string VersionId,
    ArtifactGraphRevision ExpectedGraphRevision,
    BackupArtifactSnapshot Primary,
    IReadOnlyList<BackupArtifactSnapshot> CompatibleCandidates,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    IArtifactReadService ArtifactRead,
    IArtifactTransformStaging Staging,
    IPluginOperationProgress Progress);

public sealed record StagedArtifactNode(
    ArtifactId ArtifactId,
    ArtifactStagingHandle Staging,
    ArtifactFormatRef Format,
    int FormatVersion,
    RestoreStrategyId RestoreStrategyId,
    ArtifactCompleteness Completeness,
    CoreCaptureMode CoreCaptureMode,
    IReadOnlyList<ArtifactId> Dependencies);

public sealed record ArtifactGraphPatch(
    ArtifactGraphRevision ExpectedRevision,
    IReadOnlyList<StagedArtifactNode> AddedArtifacts,
    ArtifactId ResultRootArtifactId);

public sealed record ArtifactTransformResult(
    OperationOutcome Outcome,
    ArtifactGraphPatch? Patch,
    IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IBackupCompletionObserverCapability : IPluginCapability
{
    ValueTask<BackupCompletionObserverResult> ObserveAsync(
        BackupCompletionSnapshot snapshot,
        PluginInvocationContext context);
}

public sealed record BackupCompletionSnapshot(
    string RunVersionId,
    ConfigSnapshot Config,
    FolderSnapshot Folder,
    string VersionId,
    string RepresentationId,
    ArtifactId RootArtifactId,
    ArtifactGraphRevision GraphRevision,
    OperationOutcome CoreOutcome,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    bool CloudQueueCommitted);

public sealed record BackupCompletionObserverResult(IReadOnlyList<PluginDiagnostic> Diagnostics);

public interface IRestoreMaterializerCapability : IPluginCapability
{
    RestoreStrategyId RestoreStrategyId { get; }
    ValueTask<RestoreMaterializationResult> MaterializeAsync(
        RestoreMaterializationRequest request,
        PluginInvocationContext context);
}

public sealed record RestoreMaterializationRequest(
    ConfigSnapshot Config,
    FolderSnapshot Folder,
    string VersionId,
    ArtifactId RootArtifactId,
    IReadOnlyList<BackupArtifactSnapshot> ArtifactsTopologicallySorted,
    RestoreMode RequestedMode,
    RestoreMode EffectiveMode,
    IArtifactReadService ArtifactRead,
    IRestoreMaterializationWorkspace Workspace,
    IPluginOperationProgress Progress);

public sealed record RestoreMaterializationResult(
    OperationOutcome Outcome,
    IReadOnlyList<PluginDiagnostic> Diagnostics);
