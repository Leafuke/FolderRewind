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
    bool encrypted)
{
    public async Task<SourceCaptureResult> CaptureAsync(FileChangeCapturePlan plan,
        ConfigSnapshot config, FolderSnapshot folder, ArtifactTransformPolicy policy, string stableSourceDirectory,
        EffectiveSourceBoundarySnapshot boundary, SourceCaptureBaselineCandidate statistics, CancellationToken token, PluginOperationLease? operation = null)
    {
        token.ThrowIfCancellationRequested();
        using var ownedOperation = operation is null ? plugins.TryAcquireOperation(policy.TransformerId.PluginId, token) : null;
        var captureOperation = operation ?? ownedOperation ?? throw new InvalidOperationException("Transformer session unavailable.");
        var version = VersionId.New(); var representation = RepresentationId.New();
        var primary = stableSourceDirectory;
        SourceDirectorySnapshot.Validate(plan.Directories, plan.FileStates.Keys);
        if (plan.FileStates.Count > ArtifactTransformLimits.Default.MaximumFiles
            || plan.FileStates.Any(f => f.Value.Size < 0)
            || plan.FileStates.Sum(f => f.Value.Size) > ArtifactTransformLimits.Default.MaximumBytes)
            throw new InvalidDataException("Capture source exceeds the Artifact input budget.");
        await using var readPrimary = new HostArtifactReadService(new Dictionary<ArtifactContentHandle, string> { [new("primary")] = primary },
            plan.Input.AddedFiles.Concat(plan.Input.ModifiedFiles).ToHashSet(StringComparer.Ordinal));
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
        PreparedNativeArtifact prepared;
        using (Services.HistoryOperationDiagnostics.Current?.Measure("native-conversion"))
            prepared = await store.PrepareAsync(plugins, config, folder, version.ToString(), representation.ToString(), primary,
                policy, encrypted, token, plan.Input,
                repacked => JsonSerializer.SerializeToUtf8Bytes(repacked ? document with { CapturesSinceCheckpoint = 0 } : document), new(new("primary"), readPrimary), captureOperation);
        await using var preparedLifetime = prepared;
        try
        {
            if (prepared.RequiresFullVerification)
            { document = document with { CapturesSinceCheckpoint = 0 }; evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(document); }
            using (Services.HistoryOperationDiagnostics.Current?.Measure("native-envelope-seal"))
                await prepared.SealAndCommitGraphAsync(token);
            using var candidateTiming = Services.HistoryOperationDiagnostics.Current?.Measure("native-candidate-verification");
            await using var read = prepared.OpenLazy();
            var node = read.Artifacts.Single(a => a.ArtifactId == prepared.Intent.Root);
            var materializer = prepared.Operation.Find<IRestoreMaterializerCapability>(c => c.RestoreStrategyId == node.RestoreStrategyId)
                ?? throw new InvalidOperationException("Materializer unavailable.");
            if (materializer is not IArtifactFileViewProvider provider) throw new InvalidOperationException("Incremental validation requires a file-view provider.");
            IArtifactFileView candidateView;
            try { candidateView = await provider.OpenFileViewAsync(new(config, folder, version.ToString(), node.ArtifactId, read.Artifacts, read.Read), prepared.Operation.Context); }
            catch (Exception error) when (ArtifactCaptureTransformException.IsDomainFailure(error))
            { throw new ArtifactCaptureTransformException("Native candidate could not be decoded.", error); }
            await using var view = candidateView;
            IArtifactFileView? oldView = null;
            try
            {
                if (prepared.BaselineRead is { } oldRead && plan.Input.BaseRootArtifactId is { } oldRoot)
                    oldView = await provider.OpenFileViewAsync(new(config, folder, plan.Input.BaseVersionId!, oldRoot, oldRead.Artifacts, oldRead.Read), prepared.Operation.Context);
                try
                {
                    using (ArtifactDiagnostics.Source.StartActivity("candidate.validate"))
                        await document.ValidateViewAsync(view, oldView, plan.Input with { Checkpoint = plan.Input.Checkpoint || prepared.RequiresFullVerification }, token);
                }
                catch (InvalidDataException error) when (ArtifactCaptureTransformException.IsDomainFailure(error))
                { throw new ArtifactCaptureTransformException("Native candidate does not match captured source evidence.", error); }
            }
            finally { if (oldView is not null) await oldView.DisposeAsync(); }
            var stored = prepared.Ledger.Artifacts.Single(n => n.ArtifactId == node.ArtifactId);
            var path = store.PayloadPath(stored); var fingerprint = document.Fingerprint();
            var reachable = NativeArtifactClosureLimits.Validate(prepared.Ledger, node.ArtifactId).Reachable;
            var closureStorageBytes = prepared.Ledger.Artifacts.Where(a => reachable.Contains(a.ArtifactId))
                .Sum(a => a.StorageSize + NativeArtifactClosureLimits.MaximumEnvelopeBytes);
            var metadata = new Dictionary<string, string>
            {
                ["artifactRootId"] = node.ArtifactId.Value.ToString("D"), ["pluginId"] = node.Format.OwnerId.Value,
                ["nativeConfigKindOwnerId"] = config.Kind.OwnerId.Value, ["nativeConfigKindId"] = config.Kind.KindId,
                ["storageSha256"] = stored.StorageSha256, ["nativeArtifactEnvelope"] = "1", ["incrementalEvidence"] = "2", ["nativeArtifactContractVersion"] = "2",
                ["captureEvidenceSha256"] = Convert.ToHexString(SHA256.HashData(evidenceBytes)),
                ["nativeClosureStorageBytes"] = closureStorageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
                new(path, CapturePayloadState.VerifiedFinal, stored.StorageSize, stored.StorageSha256), -1,
                plan.Baseline?.BaseVersionId, null, prepared.Diagnostics.Select(d =>
                    new HistoryDiagnostic(d.Code, d.Severity == DiagnosticSeverity.Error ? HistoryDiagnosticSeverity.Error : HistoryDiagnosticSeverity.Warning,
                        d.Code + ": " + JsonSerializer.Serialize(d.Arguments))), [],
                baselineCandidate, boundary, [])
            { ReservedVersionId = version, CommitParticipant = new NativeArtifactCaptureCommitParticipant(store, prepared.TransferPendingOwnership()) };
        }
        catch { await prepared.DiscardUncommittedAsync(CancellationToken.None); throw; }
    }
}
