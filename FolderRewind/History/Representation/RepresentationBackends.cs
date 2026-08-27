using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public sealed record PayloadVerificationResult(
    bool Success,
    string Evidence,
    string Diagnostic);

public sealed record ArchiveMaterializationInput(
    VersionRepresentation Representation,
    string LocalPath);

public interface IArchiveRepresentationBackend
{
    ValueTask<PayloadVerificationResult> VerifyAsync(
        VersionRepresentation representation,
        string localPath,
        CancellationToken cancellationToken);

    ValueTask MaterializeAsync(
        IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
        string stagingDirectory,
        CancellationToken cancellationToken);
}

public sealed record PluginArtifactProbeResult(
    bool PluginAvailable,
    bool ArtifactAvailable,
    MaterializationFidelity Fidelity,
    string Diagnostic);

public interface IPluginArtifactBackend
{
    ValueTask<PluginArtifactProbeResult> ProbeAsync(
        Guid artifactRootId,
        string pluginId,
        CancellationToken cancellationToken);

    ValueTask<PayloadVerificationResult> VerifyAsync(
        Guid artifactRootId,
        string pluginId,
        CancellationToken cancellationToken);

    ValueTask MaterializeAsync(
        Guid artifactRootId,
        string pluginId,
        string stagingDirectory,
        CancellationToken cancellationToken);
}

