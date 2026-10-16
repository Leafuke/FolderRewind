using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

public sealed class NativeArtifactIncrementalCaptureService(PluginRuntimeManager plugins, NativeArtifactStore store,
    IArchiveRepresentationBackend archives, bool encrypted)
{
    public async Task<SourceCaptureResult> TransformAsync(SourceCaptureResult? capture, FileChangeCapturePlan plan,
        ConfigSnapshot config, FolderSnapshot folder, ArtifactTransformPolicy policy, CancellationToken token,
        string? stableSourceDirectory = null, EffectiveSourceBoundarySnapshot? directBoundary = null,
        SourceCaptureBaselineCandidate? directStatistics = null)
    {
        var version = VersionId.New(); var representation = RepresentationId.New();
        var work = System.IO.Path.Combine(store.Root, ".capture", Guid.NewGuid().ToString("N"));
        var primary = stableSourceDirectory ?? System.IO.Path.Combine(work, "primary"); Directory.CreateDirectory(work);
        var boundary = capture?.EffectiveSourceBoundary ?? directBoundary ?? throw new InvalidOperationException("Source boundary required.");
        var statistics = capture?.BaselineCandidate ?? directStatistics ?? throw new InvalidOperationException("Capture witness required.");
        SourceDirectorySnapshot.Validate(plan.Directories, plan.FileStates.Keys);
        if (plan.FileStates.Count > ArtifactTransformLimits.Default.MaximumFiles
            || plan.FileStates.Any(f => f.Value.Size < 0)
            || plan.FileStates.Sum(f => f.Value.Size) > ArtifactTransformLimits.Default.MaximumBytes)
            throw new InvalidDataException("Capture source exceeds the Artifact input budget.");
        try
        {
            if (stableSourceDirectory is null)
            {
                if (archives is not IBoundedPrimaryArchiveBackend bounded) throw new InvalidOperationException("Bounded primary extraction required.");
                using (ArtifactDiagnostics.Source.StartActivity("primary.extract"))
                    await bounded.MaterializePrimaryAsync(capture!.PayloadCandidate!.AbsolutePath, primary, token);
            }
            var readPrimary = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [new("primary")] = primary },
                stableSourceDirectory is null ? null : plan.Input.AddedFiles.Concat(plan.Input.ModifiedFiles).ToHashSet(StringComparer.Ordinal));
            var physical = await readPrimary.ListFilesAsync(new("primary"), token);
            if (!physical.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal)
                .SetEquals(plan.Input.AddedFiles.Concat(plan.Input.ModifiedFiles)))
                throw new InvalidDataException("Primary inventory differs from the Host change set.");
            var expected = new Dictionary<string, ArtifactFileEntry>(StringComparer.Ordinal);
            if (plan.Input.BaseRootArtifactId is { } baseRoot)
            {
                await using var baseline = await store.OpenLazyAsync(baseRoot, token);
                var evidence = await IncrementalCaptureEvidence.ReadAsync(baseline, baseRoot, token);
                if (evidence.VersionId != plan.Input.BaseVersionId || evidence.RepresentationId != plan.Input.BaseRepresentationId
                    || evidence.BoundaryFingerprint != plan.Input.BoundaryFingerprint)
                    throw new InvalidDataException("Baseline evidence binding mismatch.");
                foreach (var file in evidence.Files) expected.Add(file.RelativePath, file);
            }
            foreach (var path in plan.Input.DeletedFiles) if (!expected.Remove(path)) throw new InvalidDataException("Deletion has no baseline.");
            foreach (var file in physical)
            {
                if (!plan.FileStates.TryGetValue(file.RelativePath, out var stat) || stat.Size != file.Length)
                    throw new InvalidDataException("Captured bytes differ from the file-state witness.");
                expected[file.RelativePath] = file;
            }
            if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(plan.FileStates.Keys)) throw new InvalidDataException("Incomplete logical Source state.");
            var document = new IncrementalCaptureEvidence(IncrementalCaptureEvidence.CurrentSchema, config.ConfigId, folder.FolderId, version.ToString(), representation.ToString(),
                plan.Input.BoundaryFingerprint, plan.Input.DetectionPolicy, expected.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(),
                plan.FileStates, plan.Input.Checkpoint ? 0 : checked((plan.Baseline?.ConsecutiveSmartCaptures ?? 0) + 1))
                { Directories = plan.Directories };
            var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(document);
            await using var prepared = await store.PrepareAsync(plugins, config, folder, version.ToString(), representation.ToString(), primary,
                policy, encrypted, token, capture?.PayloadCandidate?.AbsolutePath, capture?.RepresentationCandidate?.RepresentationId.ToString(), plan.Input,
                repacked => JsonSerializer.SerializeToUtf8Bytes(repacked ? document with { CapturesSinceCheckpoint = 0 } : document), new(new("primary"), readPrimary));
            try
            {
                if (prepared.RequiresFullVerification)
                { document = document with { CapturesSinceCheckpoint = 0 }; evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(document); }
                await prepared.SealAndCommitGraphAsync(token);
                await using var read = prepared.OpenLazy();
                var node = read.Artifacts.Single(a => a.ArtifactId == prepared.Intent.Root);
                using var materializer = plugins.TryAcquire<IRestoreMaterializerCapability>(node.RestoreStrategyId.PluginId,
                    c => c.RestoreStrategyId == node.RestoreStrategyId, token) ?? throw new InvalidOperationException("Materializer unavailable.");
                if (materializer.Capability is not IArtifactFileViewProvider provider) throw new InvalidOperationException("Incremental validation requires a file-view provider.");
                await using var view = await provider.OpenFileViewAsync(new(config, folder, version.ToString(), node.ArtifactId, read.Artifacts, read.Read), materializer.Context);
                IArtifactFileView? oldView = null;
                try
                {
                    if (prepared.BaselineRead is { } oldRead && plan.Input.BaseRootArtifactId is { } oldRoot)
                        oldView = await provider.OpenFileViewAsync(new(config, folder, plan.Input.BaseVersionId!, oldRoot, oldRead.Artifacts, oldRead.Read), materializer.Context);
                    using (ArtifactDiagnostics.Source.StartActivity("candidate.validate"))
                        await document.ValidateViewAsync(view, oldView, plan.Input with { Checkpoint = plan.Input.Checkpoint || prepared.RequiresFullVerification }, token);
                }
                finally { if (oldView is not null) await oldView.DisposeAsync(); }
                var stored = prepared.Ledger.Artifacts.Single(n => n.ArtifactId == node.ArtifactId);
                var path = store.PayloadPath(stored); var fingerprint = document.Fingerprint();
                var metadata = new Dictionary<string, string>
                {
                    ["artifactRootId"] = node.ArtifactId.Value.ToString("D"), ["pluginId"] = node.Format.OwnerId.Value,
                    ["storageSha256"] = stored.StorageSha256, ["nativeArtifactEnvelope"] = "1", ["incrementalEvidence"] = "2", ["nativeArtifactContractVersion"] = "2",
                    ["captureEvidenceSha256"] = Convert.ToHexString(SHA256.HashData(evidenceBytes)),
                    ["fileName"] = System.IO.Path.GetFileName(path), ["stateFingerprint"] = fingerprint
                };
                var candidate = new RepresentationCandidate(representation, RepresentationKind.PluginArtifact, node.Format.FormatId,
                    [], MaterializationFidelity.Exact, fingerprint, fingerprint, metadata);
                var baselineCandidate = new SourceCaptureBaselineCandidate(statistics.ExpectedRevision, path,
                    document.CapturesSinceCheckpoint, plan.FileStates, document.BoundaryFingerprint)
                    { ArtifactRootId = node.ArtifactId.Value, EvidenceIdentity = metadata["captureEvidenceSha256"],
                        SourcePathFingerprint = statistics.SourcePathFingerprint, Directories = plan.Directories };
                return new SourceCaptureResult(new(folder.FolderId), SourceCaptureOutcome.Captured, CaptureScope.FullSource, fingerprint,
                    null, candidate, new(LocalReplicaId.New(), representation, LocalReplicaLocator.ControlledAbsolute(path), CapturePayloadState.VerifiedFinal, DateTimeOffset.UtcNow),
                    new(path, CapturePayloadState.VerifiedFinal, stored.StorageSize, stored.StorageSha256), capture?.ExpectedWorkspaceRevision ?? -1,
                    capture?.ExpectedBaseVersionId ?? plan.Baseline?.BaseVersionId, capture?.CleanupHandle, (capture?.Diagnostics ?? []).Concat(prepared.Diagnostics.Select(d =>
                        new HistoryDiagnostic(d.Code, d.Severity == DiagnosticSeverity.Error ? HistoryDiagnosticSeverity.Error : HistoryDiagnosticSeverity.Warning,
                            d.Code + ": " + JsonSerializer.Serialize(d.Arguments)))), capture?.ExpectedMaterializationPolicyTipIds ?? [],
                    baselineCandidate, boundary, capture?.VersionMetadataCandidates ?? [])
                { ReservedVersionId = version, CommitParticipant = new NativeArtifactCaptureService.Participant(store, prepared.Intent, cleanupPrimary: true) };
            }
            catch { store.CompletePending(prepared.Intent.Id); throw; }
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }
}
