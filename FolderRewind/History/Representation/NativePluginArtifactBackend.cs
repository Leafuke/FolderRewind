using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

public sealed record NativeArtifactBinding(VersionRepresentation Representation, ConfigSnapshot Config, FolderSnapshot Folder)
{
    public bool LocalRegistered { get; init; } = true;
}

/// <summary>Reads the versioned Native closure and verifies reconstructed source bytes before use.</summary>
public sealed class NativePluginArtifactBackend(PluginRuntimeManager plugins, Func<NativeArtifactStore> storeFactory,
    Func<Guid, CancellationToken, Task<NativeArtifactBinding>> resolve) : IPluginArtifactBackend, IInlineVerifiedMaterializer
{
    public static bool IsSupported(VersionRepresentation representation)
        => representation.Kind == RepresentationKind.PluginArtifact
            && representation.RepresentationSpecificMetadata.TryGetValue("nativeArtifactContractVersion", out var contract) && contract == "2"
            && representation.RepresentationSpecificMetadata.TryGetValue("incrementalEvidence", out var evidence) && evidence == "2";

    public bool CanVerifyDuringMaterialization(VersionRepresentation representation) => IsSupported(representation);

    public async ValueTask<PluginArtifactProbeResult> ProbeAsync(Guid root, string pluginId, CancellationToken token)
    {
        var binding = await resolve(root, token);
        if (!IsSupported(binding.Representation))
            return new(false, false, MaterializationFidelity.Unknown,
                "[history.unsupported-experimental-format] Native evidence v2 is required.");
        var manifest = plugins.GetActiveManifest(new(pluginId));
        if (manifest is null)
            return new(false, false, MaterializationFidelity.Unknown, "Required plugin is inactive.");
        if (!manifest.RestoreStrategies.Any(s => s.SupportedFormats.Any(f => f.Format.FormatId == binding.Representation.Format)))
            return new(false, false, MaterializationFidelity.Unknown, "Installed provider cannot read the protected format.");
        if (!binding.LocalRegistered)
            return new(true, false, MaterializationFidelity.Exact, "Native closure is not registered on this device.");
        try
        {
            await using var read = await storeFactory().OpenLazyAsync(new(root), token);
            Validate(read, root, pluginId, binding);
            return new(true, true, MaterializationFidelity.Exact, "metadata-and-availability-only");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(true, false, MaterializationFidelity.Exact, ex.Message); }
    }

    public async ValueTask<PayloadVerificationResult> VerifyAsync(Guid root, string pluginId, CancellationToken token)
    {
        try
        {
            await ReadVersionAsync(root, pluginId, null, token);
            return new(true, "native-v2-closure-and-reconstructed-source-tree", string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(false, string.Empty, ex.Message); }
    }

    public ValueTask MaterializeAsync(Guid root, string pluginId, string stagingDirectory, CancellationToken token)
    {
        if (Directory.Exists(stagingDirectory) && Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            throw new IOException("Materialization requires an empty workspace.");
        return new ValueTask(ReadVersionAsync(root, pluginId, stagingDirectory, token));
    }

    private async Task ReadVersionAsync(Guid root, string pluginId, string? destination, CancellationToken token)
    {
        var binding = await resolve(root, token);
        if (!IsSupported(binding.Representation))
            throw new InvalidDataException("[history.unsupported-experimental-format] Native evidence v2 is required.");
        if (!binding.LocalRegistered) throw new IOException("Native closure is not registered on this device.");
        await using var read = await storeFactory().OpenAsync(new(root), token);
        Validate(read, root, pluginId, binding);
        var node = read.Artifacts.Single(a => a.ArtifactId.Value == root);
        using var lease = plugins.TryAcquire<IRestoreMaterializerCapability>(node.RestoreStrategyId.PluginId,
            c => c.RestoreStrategyId == node.RestoreStrategyId, token)
            ?? throw new InvalidOperationException("Materializer unavailable.");
        if (lease.Capability is not IArtifactFileViewProvider provider)
            throw new InvalidOperationException("File-view provider unavailable.");
        var evidence = await IncrementalCaptureEvidence.ReadAsync(read, new(root), token);
        var entries = await read.Read.ListFilesAsync(node.Content, token);
        if (evidence.VersionId != binding.Representation.VersionId.ToString()
            || evidence.RepresentationId != binding.Representation.RepresentationId.ToString()
            || evidence.Fingerprint() != binding.Representation.StateFingerprint
            || !binding.Representation.RepresentationSpecificMetadata.TryGetValue("captureEvidenceSha256", out var expectedEvidence)
            || !StringComparer.OrdinalIgnoreCase.Equals(entries.Single(f => f.RelativePath == IncrementalCaptureEvidence.Path).Sha256, expectedEvidence))
            throw new InvalidDataException("Capture evidence binding mismatch.");
        await using var view = await provider.OpenFileViewAsync(new(binding.Config, binding.Folder, evidence.VersionId,
            new(root), read.Artifacts, read.Read), lease.Context);
        if (destination is null) await evidence.VerifyViewAsync(view, token);
        else await evidence.CopyViewToAsync(view, destination, token);
        lease.Context.OperationCancellation.ThrowIfCancellationRequested();
    }

    private void Validate(NativeArtifactReadLease read, Guid root, string pluginId, NativeArtifactBinding binding)
    {
        var manifest = plugins.GetActiveManifest(new(pluginId)) ?? throw new InvalidOperationException("Materializer plugin unavailable.");
        var node = read.Artifacts.Single(a => a.ArtifactId.Value == root);
        var stored = read.Nodes.Single(a => a.ArtifactId.Value == root);
        if (!binding.Representation.RepresentationSpecificMetadata.TryGetValue("storageSha256", out var expectedStorage)
            || !StringComparer.OrdinalIgnoreCase.Equals(expectedStorage, stored.StorageSha256)
            || binding.Representation.Format != node.Format.FormatId)
            throw new InvalidDataException("Artifact format or storage evidence differs from committed History.");
        if (node.ConfigId != binding.Config.ConfigId || node.FolderId != binding.Folder.FolderId
            || node.RestoreStrategyId.PluginId.Value != pluginId || node.Completeness != ArtifactCompleteness.Complete)
            throw new InvalidDataException("Artifact binding mismatch.");
        if (!manifest.RestoreStrategies.Any(s => s.RestoreStrategyId == node.RestoreStrategyId
            && s.SupportedFormats.Any(f => f.Format == node.Format && f.MinimumVersion <= node.FormatVersion && f.MaximumVersion >= node.FormatVersion)))
            throw new InvalidDataException("Installed provider cannot read the protected format version.");
    }
}
