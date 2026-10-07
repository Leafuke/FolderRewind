using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

public sealed record NativeArtifactBinding(VersionRepresentation Representation, ConfigSnapshot Config, FolderSnapshot Folder);

/// <summary>Reads only the committed Artifact closure; never falls back to the paired Core archive.</summary>
public sealed class NativePluginArtifactBackend(PluginRuntimeManager plugins, Func<NativeArtifactStore> storeFactory,
    Func<Guid, CancellationToken, Task<NativeArtifactBinding>> resolve) : IPluginArtifactBackend, IInlineVerifiedMaterializer
{
    public bool CanVerifyDuringMaterialization(VersionRepresentation representation)
        => representation.Kind == RepresentationKind.PluginArtifact
            && representation.RepresentationSpecificMetadata.ContainsKey("incrementalEvidence");
    private NativeArtifactStore Store => storeFactory();
    public async ValueTask<PluginArtifactProbeResult> ProbeAsync(Guid root, string pluginId, CancellationToken token)
    {
        if (plugins.GetActiveManifest(new(pluginId)) is null)
            return new(false, false, MaterializationFidelity.Unknown, "Required plugin is inactive.");
        try
        {
            var binding = await resolve(root, token);
            await using var read = await Store.OpenLazyAsync(new(root), token);
            Validate(read, root, pluginId, binding);
            return new(true, true, MaterializationFidelity.Exact, "metadata-and-availability-only");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(true, false, MaterializationFidelity.Unknown, ex.Message); }
    }

    public async ValueTask<PayloadVerificationResult> VerifyAsync(Guid root, string pluginId, CancellationToken token)
    {
        try
        {
            var binding = await resolve(root, token);
            await using var read = await Store.OpenAsync(new(root), token);
            Validate(read, root, pluginId, binding);
            return new(true, "native-envelope-and-logical-closure", string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(false, string.Empty, ex.Message); }
    }

    public async ValueTask MaterializeAsync(Guid root, string pluginId, string stagingDirectory, CancellationToken token)
    {
        if (Directory.Exists(stagingDirectory) && Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            throw new IOException("Materialization requires an empty workspace.");
        var binding = await resolve(root, token);
        var store = Store;
        if (binding.Representation.RepresentationSpecificMetadata.ContainsKey("incrementalEvidence"))
        {
            // Verify every node once and retain those decoded bytes for the actual file reconstruction.
            await using var read = await store.OpenAsync(new(root), token);
            Validate(read, root, pluginId, binding);
            var node = read.Artifacts.Single(a => a.ArtifactId.Value == root);
            using var lease = plugins.TryAcquire<IRestoreMaterializerCapability>(node.RestoreStrategyId.PluginId,
                c => c.RestoreStrategyId == node.RestoreStrategyId, token) ?? throw new InvalidOperationException("Materializer unavailable.");
            if (lease.Capability is not IArtifactFileViewProvider provider) throw new InvalidOperationException("File-view provider unavailable.");
            var evidence = await IncrementalCaptureEvidence.ReadAsync(read, new(root), token);
            if (evidence.VersionId != binding.Representation.VersionId.ToString()
                || evidence.RepresentationId != binding.Representation.RepresentationId.ToString()
                || evidence.Fingerprint() != binding.Representation.StateFingerprint) throw new InvalidDataException("Capture evidence binding mismatch.");
            await using var view = await provider.OpenFileViewAsync(new(binding.Config, binding.Folder, evidence.VersionId,
                new(root), read.Artifacts, read.Read), lease.Context);
            await evidence.CopyViewToAsync(view, stagingDirectory, token);
            return;
        }
        var work = Path.Combine(store.Root, ".materialize", Guid.NewGuid().ToString("N"));
        try
        {
            await using var read = await store.OpenAsync(new(root), token);
            Validate(read, root, pluginId, binding);
            var result = await NativeArtifactCaptureService.MaterializeAsync(plugins, binding.Config, binding.Folder,
                binding.Representation.VersionId.ToString(), new(root), read, Path.Combine(work, "output"), token);
            if (!binding.Representation.RepresentationSpecificMetadata.TryGetValue("materializedTreeDigest", out var expected)
                || (await MergeTreeManifest.ReadAsync(result, _ => true, token)).Digest != expected)
                throw new InvalidDataException("Materialized file tree does not match the committed Exact evidence.");
            Directory.CreateDirectory(stagingDirectory);
            foreach (var file in Directory.EnumerateFiles(result, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(stagingDirectory, Path.GetRelativePath(result, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, false);
            }
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
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
        using var lease = plugins.TryAcquire<IRestoreMaterializerCapability>(new(pluginId), c => c.RestoreStrategyId == node.RestoreStrategyId);
        if (lease is null) throw new InvalidOperationException("Materializer capability unavailable.");
    }
}
