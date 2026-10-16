using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

public sealed class NativeArtifactCaptureService(PluginRuntimeManager plugins, NativeArtifactStore store,
    IArchiveRepresentationBackend archives, bool encrypted)
{
    public async Task<SourceCaptureResult> TransformAsync(SourceCaptureResult capture, ConfigSnapshot config,
        FolderSnapshot folder, ArtifactTransformPolicy policy, CancellationToken token)
    {
        if (capture.Outcome != SourceCaptureOutcome.Captured) return capture;
        try
        {
            if (capture.CaptureScope != CaptureScope.FullSource || capture.RepresentationCandidate?.Kind != RepresentationKind.CoreFull)
                throw new InvalidOperationException("Native Artifact Full only accepts Complete CoreFull captures.");
            var version = VersionId.New(); var representation = RepresentationId.New();
            var work = Path.Combine(store.Root, ".capture", Guid.NewGuid().ToString("N"));
            var primary = Path.Combine(work, "primary");
            Directory.CreateDirectory(work);
            try
            {
                var core = capture.RepresentationCandidate.ToFact(version);
                var validation = await archives.VerifyAsync(core, capture.PayloadCandidate!.AbsolutePath, token);
                if (!validation.Success) throw new InvalidDataException(validation.Diagnostic);
                if (archives is not IBoundedPrimaryArchiveBackend bounded)
                    throw new InvalidOperationException("Native transform requires a bounded primary materializer.");
                await bounded.MaterializePrimaryAsync(capture.PayloadCandidate.AbsolutePath, primary, token);
                var expected = await MergeTreeManifest.ReadAsync(primary, _ => true, token);
                await using var prepared = await store.PrepareAsync(plugins, config, folder, version.ToString(), representation.ToString(), primary, policy, encrypted, token,
                    capture.PayloadCandidate.AbsolutePath, core.RepresentationId.ToString());
                try
                {
                    await prepared.SealAndCommitGraphAsync(token);
                    await using (var read = await prepared.OpenAsync(token))
                    {
                        var actualRoot = await MaterializeAsync(plugins, config, folder, version.ToString(), prepared.Intent.Root, read,
                            Path.Combine(work, "verification"), token);
                        if ((await MergeTreeManifest.ReadAsync(actualRoot, _ => true, token)).Digest != expected.Digest)
                            throw new InvalidDataException("Plugin materialization differs from CoreFull capture.");
                    }
                    var node = prepared.Ledger.Artifacts.Single(a => a.ArtifactId == prepared.Intent.Root);
                    var payload = store.PayloadPath(node);
                    var metadata = new Dictionary<string, string>
                    {
                        [PluginArtifactRepresentationHandler.ArtifactRootIdMetadataKey] = node.ArtifactId.Value.ToString("D"),
                        [PluginArtifactRepresentationHandler.PluginIdMetadataKey] = node.Format.OwnerId.Value,
                        ["pairedCoreRepresentationId"] = core.RepresentationId.ToString(),
                        ["storageSha256"] = node.StorageSha256,
                        ["materializedTreeDigest"] = expected.Digest,
                        ["nativeArtifactEnvelope"] = "1",
                        ["fileName"] = Path.GetFileName(payload)
                    };
                    var extra = new AdditionalCaptureRepresentation(
                        new(representation, RepresentationKind.PluginArtifact, node.Format.FormatId, [], MaterializationFidelity.Exact,
                            capture.RepresentationCandidate.LogicalSha256, capture.StateFingerprint, metadata),
                        new(LocalReplicaId.New(), representation, LocalReplicaLocator.ControlledAbsolute(payload), CapturePayloadState.VerifiedFinal, DateTimeOffset.UtcNow),
                        new(payload, CapturePayloadState.VerifiedFinal, node.StorageSize, node.StorageSha256));
                    var diagnostics = prepared.Diagnostics.Select(d => new HistoryDiagnostic(d.Code,
                        d.Severity == DiagnosticSeverity.Error ? HistoryDiagnosticSeverity.Error : HistoryDiagnosticSeverity.Warning,
                        d.Code + ": " + System.Text.Json.JsonSerializer.Serialize(d.Arguments)));
                    return capture.WithVersionMetadata(capture.VersionMetadataCandidates, diagnostics) with
                    {
                        ReservedVersionId = version, AdditionalRepresentations = [extra],
                        CommitParticipant = new Participant(store, prepared.Intent)
                    };
                }
                catch { store.CompletePending(prepared.Intent.Id); throw; }
            }
            finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (policy.FailureBehavior == ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings)
        {
            return capture.WithVersionMetadata(capture.VersionMetadataCandidates,
                [new HistoryDiagnostic("plugin.artifact_primary_kept", HistoryDiagnosticSeverity.Warning, ex.Message)]);
        }
    }

    public static async Task<string> MaterializeAsync(PluginRuntimeManager plugins, ConfigSnapshot config, FolderSnapshot folder,
        string version, ArtifactId root, NativeArtifactReadLease read, string workspace, CancellationToken token)
    {
        var snapshot = read.Artifacts.Single(a => a.ArtifactId == root);
        if (snapshot.ConfigId != config.ConfigId || snapshot.FolderId != folder.FolderId) throw new InvalidDataException("Artifact ownership mismatch.");
        using var lease = plugins.TryAcquire<IRestoreMaterializerCapability>(snapshot.RestoreStrategyId.PluginId,
            c => c.RestoreStrategyId == snapshot.RestoreStrategyId, token) ?? throw new InvalidOperationException("Materializer unavailable.");
        await using var target = await RestoreMaterializationWorkspace.CreateAsync(workspace, cancellationToken: token);
        var result = await lease.Capability.MaterializeAsync(new(config, folder, version, root, read.Artifacts,
            Plugin.Abstractions.RestoreMode.Clean, Plugin.Abstractions.RestoreMode.Clean, read.Read, target,
            NullPluginOperationProgress.Instance), lease.Context);
        lease.Context.OperationCancellation.ThrowIfCancellationRequested();
        if (result.Outcome is not (OperationOutcome.Success or OperationOutcome.SuccessWithWarnings)
            || result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("Materializer failed.");
        var files = await target.SealAsync(token);
        var resultRoot = workspace + "-result";
        Directory.CreateDirectory(resultRoot);
        foreach (var file in files)
        {
            var destination = Path.Combine(resultRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(target.RootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), destination);
        }
        return resultRoot;
    }

    internal sealed class Participant(NativeArtifactStore store, NativeArtifactPending intent, bool cleanupPrimary = false) : ICaptureCommitParticipant
    {
        public async Task PrepareCommitAsync(PackId packId, HistoryTransactionId transactionId, CancellationToken token)
        {
            using var held = await store.EnterAsync(token);
            intent = intent with { HistoryPackId = packId.ToString(), HistoryTransactionId = transactionId.ToString(), Phase = "ArtifactGraphCommitted" };
            await store.SavePendingAsync(intent, token);
        }
        public async Task AbortCommitAsync(CancellationToken token)
        {
            using var held = await store.EnterAsync(token);
            store.CompletePending(intent.Id);
        }
        public async Task HistoryCommittedAsync(CancellationToken token)
        {
            using var held = await store.EnterAsync(token);
            await store.SavePendingAsync(intent with { Phase = "HistoryPackCommitted" }, token);
        }
        public async Task CompleteCommitAsync(CancellationToken token)
        {
            using var held = await store.EnterAsync(token);
            await store.SavePendingAsync(intent with { Phase = "LocalStateApplied" }, token);
            await store.SavePendingAsync(intent with { Phase = "Complete" }, token);
            if (cleanupPrimary) await store.CleanupPrimaryAsync(intent, token);
            store.CompletePending(intent.Id);
        }
    }
}
