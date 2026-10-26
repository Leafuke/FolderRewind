using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
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
    Func<Guid, CancellationToken, Task<NativeArtifactBinding>> resolve, Func<object>? verificationPolicy = null) : IPluginArtifactBackend, IInlineVerifiedMaterializer
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
        using var operation = plugins.TryAcquireOperation(new(pluginId), token);
        var manifest = operation?.Manifest;
        if (manifest is null)
            return new(false, false, MaterializationFidelity.Unknown, "Required plugin is inactive.");
        if (!manifest.RestoreStrategies.Any(s => s.SupportedFormats.Any(f => f.Format.FormatId == binding.Representation.Format)))
            return new(false, false, MaterializationFidelity.Unknown, "Installed provider cannot read the protected format.");
        if (!binding.LocalRegistered)
            return new(true, false, MaterializationFidelity.Exact, "Native closure is not registered on this device.");
        try
        {
            await using var read = await storeFactory().OpenLazyAsync(new(root), token);
            Validate(read, root, pluginId, binding, manifest);
            return new(true, true, MaterializationFidelity.Exact, "metadata-and-availability-only");
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("[history.unsupported-experimental-format]", StringComparison.Ordinal))
        { return new(false, false, MaterializationFidelity.Unknown, ex.Message); }
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

    internal async ValueTask<PayloadVerificationResult> VerifyAsync(Guid root, string pluginId,
        VersionRepresentation representation, ArchiveRestoreVerificationContext context, CancellationToken token)
    {
        if (verificationPolicy is null) return await VerifyAsync(root, pluginId, token);
        var store = storeFactory();
        var binding = await resolve(root, token);
        // Pin the plugin through the context's proof owner as well as envelope bytes.
        using var operation = plugins.TryAcquireOperation(new(pluginId), token)
            ?? throw new InvalidOperationException("Materializer unavailable.");
        string identity;
        string[] paths;
        await using (var read = await store.OpenLazyAsync(new(root), token))
        {
            Validate(read, root, pluginId, binding, operation.Manifest!);
            identity = JsonSerializer.Serialize(new { store.Root, binding, Manifest = operation.Manifest,
                Nodes = read.Nodes.OrderBy(node => node.ArtifactId.Value) });
            paths = read.Nodes.SelectMany(node => new[] { store.PayloadPath(node),
                Path.Combine(Path.GetDirectoryName(store.PayloadPath(node))!, "envelope.json") }).ToArray();
        }
        // Do not retain the Artifact store gate across candidates. The outer proof holds read-only file bindings.
        return await context.VerifyProtectedNativeAsync(this, representation, verificationPolicy(), identity, paths,
            cancellation => VerifyAsync(root, pluginId, cancellation), token,
            () => plugins.TryAcquireOperation(new(pluginId), token) ?? throw new InvalidOperationException("Materializer unavailable."));
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
        using var operation = plugins.TryAcquireOperation(new(pluginId), token) ?? throw new InvalidOperationException("Materializer unavailable.");
        var manifest = operation.Manifest ?? throw new InvalidOperationException("Materializer manifest unavailable.");
        await using var read = await storeFactory().OpenAsync(new(root), token);
        Validate(read, root, pluginId, binding, manifest);
        var node = read.Artifacts.Single(a => a.ArtifactId.Value == root);
        var materializer = operation.Find<IRestoreMaterializerCapability>(c => c.RestoreStrategyId == node.RestoreStrategyId)
            ?? throw new InvalidOperationException("Materializer unavailable.");
        if (materializer is not IArtifactFileViewProvider provider)
            throw new InvalidOperationException("File-view provider unavailable.");
        var evidence = await IncrementalCaptureEvidence.ReadAsync(read, new(root), token);
        var entries = await read.Read.ListFilesAsync(node.Content, token);
        if (evidence.VersionId != binding.Representation.VersionId.ToString()
            || evidence.RepresentationId != binding.Representation.RepresentationId.ToString()
            || evidence.Fingerprint() != binding.Representation.StateFingerprint
            || !binding.Representation.RepresentationSpecificMetadata.TryGetValue("captureEvidenceSha256", out var expectedEvidence)
            || !StringComparer.OrdinalIgnoreCase.Equals(entries.Single(f => f.RelativePath == IncrementalCaptureEvidence.Path).Sha256, expectedEvidence))
            throw new InvalidDataException("Capture evidence binding mismatch.");
        // History normalizes GUID spelling; the immutable node retains the original plugin snapshot identity.
        var snapshot = binding.Config with { ConfigId = node.ConfigId };
        ValueTask<IArtifactFileView> OpenView() => provider.OpenFileViewAsync(new(snapshot, binding.Folder, evidence.VersionId,
            new(root), read.Artifacts, read.Read), operation.Context);
        if (destination is null)
        {
            await using var view = await OpenView();
            await evidence.VerifyViewAsync(view, token);
        }
        else await evidence.MaterializeViewToAsync(OpenView, destination, token);
        operation.Context.OperationCancellation.ThrowIfCancellationRequested();
    }

    private static void Validate(NativeArtifactReadLease read, Guid root, string pluginId, NativeArtifactBinding binding, PluginManifestContract manifest)
    {
        var node = read.Artifacts.Single(a => a.ArtifactId.Value == root);
        var stored = read.Nodes.Single(a => a.ArtifactId.Value == root);
        if (!binding.Representation.RepresentationSpecificMetadata.TryGetValue("storageSha256", out var expectedStorage)
            || !StringComparer.OrdinalIgnoreCase.Equals(expectedStorage, stored.StorageSha256)
            || binding.Representation.Format != node.Format.FormatId)
            throw new InvalidDataException("Artifact format or storage evidence differs from committed History.");
        if (new HistoryConfigId(node.ConfigId) != new HistoryConfigId(binding.Config.ConfigId) || node.FolderId != binding.Folder.FolderId
            || node.RestoreStrategyId.PluginId.Value != pluginId || node.Completeness != ArtifactCompleteness.Complete)
            throw new InvalidDataException("Artifact binding mismatch.");
        if (!manifest.RestoreStrategies.Any(s => s.RestoreStrategyId == node.RestoreStrategyId
            && s.SupportedFormats.Any(f => f.Format == node.Format && f.MinimumVersion <= node.FormatVersion && f.MaximumVersion >= node.FormatVersion)))
            throw new InvalidDataException("[history.unsupported-experimental-format] Installed provider cannot read the protected format version.");
    }
}
