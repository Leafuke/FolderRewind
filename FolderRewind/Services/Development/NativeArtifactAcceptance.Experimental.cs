#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    // Headless service integration; never drives UI and never enters the development gate override.
    private static async Task<int> RunExperimentalDeliveryAsync(BackupConfig config, string root)
    {
        var policy = config.ArtifactTransformPolicy!;
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        if ((await BackupService.BackupConfigAsync(config)).CreatedNewArchive)
            throw new InvalidDataException("Legacy settings auto-enabled the experiment.");
        policy.ExperimentalEnabled = true;
        if (ExperimentalArtifactPolicyService.UnavailableReason(config) is not null)
            throw new InvalidDataException("Expected compatible production provider.");
        if (!(await BackupService.BackupConfigAsync(config)).CreatedNewArchive)
            throw new InvalidDataException("Explicit opt-in did not create a baseline.");
        var first = (await history.Query.GetAllRepresentationsAsync()).Single();
        var store = NativeArtifactHostService.Store(config);
        var firstIds = ArtifactLedgerValidator.BuildClosure(await store.Ledger.LoadAsync(),
            [new(Guid.Parse(first.RepresentationSpecificMetadata["artifactRootId"]))]).Reachable.ToHashSet();
        if ((await BackupService.BackupConfigAsync(config)).CreatedNewArchive)
            throw new InvalidDataException("Normal unchanged backup did not skip.");
        var settingsDraft = new ConfigSettingsDraftService(config);
        var checkpoint = await BackupService.CreateSemanticCheckpointAsync(config, default);
        if (!checkpoint.IsSuccessful || !checkpoint.CreatedNewArchive) throw new InvalidDataException("Unchanged manual checkpoint failed.");
        settingsDraft.RefreshBackupTimesAfterOperation();
        if (settingsDraft.IsDirty) throw new InvalidDataException("Checkpoint runtime facts dirtied the settings draft.");
        settingsDraft.Draft.Name += " (delivery)";
        if (!(await settingsDraft.CommitAsync()).Success) throw new InvalidDataException("Checkpoint left the settings draft stale.");
        config = ConfigService.CurrentConfig.BackupConfigs.Single(); policy = config.ArtifactTransformPolicy!;
        var current = await CurrentAsync();
        var checkpointIds = ArtifactLedgerValidator.BuildClosure(await store.Ledger.LoadAsync(),
            [new(Guid.Parse(current.RepresentationSpecificMetadata["artifactRootId"]))]).Reachable;
        if (checkpointIds.Any(firstIds.Contains)) throw new InvalidDataException("Checkpoint still references old Artifacts.");
        await VerifyAsync(current, "checkpoint");
        if ((await BackupService.BackupConfigAsync(config)).CreatedNewArchive)
            throw new InvalidDataException("Manual checkpoint leaked into the next invocation.");

        var versionsBefore = (await history.Query.GetAllVersionsAsync()).Count;
        config.Archive.Mode = BackupMode.Rolling;
        if ((await BackupService.CreateSemanticCheckpointAsync(config, default)).CreatedNewArchive)
            throw new InvalidDataException("Rolling checkpoint was accepted.");
        config.Archive.Mode = BackupMode.Smart;
        try { await BackupService.CreateSemanticCheckpointAsync(config, new CancellationToken(true)); throw new InvalidDataException("Canceled checkpoint ran."); }
        catch (OperationCanceledException) { }
        if ((await history.Query.GetAllVersionsAsync()).Count != versionsBefore) throw new InvalidDataException("Rejected checkpoint advanced History.");

        // Valid container bounds, intentionally over-budget decoded payload: semantic failure must not be hidden by opaque fallback.
        var bad = Path.Combine(config.SourceFolders.Single().Path, "dimensions", "p3test", "fixture", "region", "r.999.999.mca");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        using (var encoded = new MemoryStream())
        {
            using (var compressor = new ZLibStream(encoded, CompressionLevel.Fastest, true))
            {
                var block = new byte[65536];
                for (var i = 0; i < 257; i++) compressor.Write(block);
            }
            var payload = encoded.ToArray(); var sectors = (payload.Length + 5 + 4095) / 4096;
            var bytes = new byte[8192 + sectors * 4096];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(0x200 | sectors));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8192), payload.Length + 1);
            bytes[8196] = 2; payload.CopyTo(bytes, 8197); await File.WriteAllBytesAsync(bad, bytes);
        }
        policy.FailureBehavior = PersistedArtifactTransformFailureBehavior.RequireTransform;
        if ((await BackupService.BackupConfigAsync(config)).CreatedNewArchive) throw new InvalidDataException("RequireTransform failure advanced History.");
        policy.FailureBehavior = PersistedArtifactTransformFailureBehavior.KeepPrimaryWithWarnings;
        var fallback = await BackupService.BackupConfigAsync(config);
        if (!fallback.CreatedNewArchive || fallback.Outcome != OperationOutcome.SuccessWithWarnings)
            throw new InvalidDataException("Safe Core fallback was not reported as a warning.");
        current = await CurrentAsync();
        if (current.Kind != RepresentationKind.CoreFull || current.DependencyRepresentationIds.Length != 0)
            throw new InvalidDataException("Fallback depended on a semantic representation.");
        await VerifyAsync(current, "fallback");
        versionsBefore = (await history.Query.GetAllVersionsAsync()).Count;
        if ((await BackupService.CreateSemanticCheckpointAsync(config, default)).CreatedNewArchive
            || (await history.Query.GetAllVersionsAsync()).Count != versionsBefore)
            throw new InvalidDataException("Failed manual checkpoint silently committed a Core fallback.");
        File.Delete(bad);
        if (!(await BackupService.BackupConfigAsync(config)).CreatedNewArchive || (await CurrentAsync()).Kind != RepresentationKind.PluginArtifact)
            throw new InvalidDataException("Semantic capture did not re-establish a baseline after Core fallback.");
        config.ArtifactTransformPolicy = null;
        if (!(await BackupService.BackupConfigAsync(config)).CreatedNewArchive || (await CurrentAsync()).Kind != RepresentationKind.CoreFull)
            throw new InvalidDataException("Disabling semantic capture did not establish CoreFull.");
        config.ArtifactTransformPolicy = policy;
        versionsBefore = (await history.Query.GetAllVersionsAsync()).Count;
        await PluginV3RuntimeService.DeactivateAsync(new(policy.Transformer.PluginId));
        if ((await BackupService.BackupConfigAsync(config)).CreatedNewArchive || (await history.Query.GetAllVersionsAsync()).Count != versionsBefore)
            throw new InvalidDataException("Missing plugin advanced History.");
        await File.WriteAllTextAsync(Path.Combine(root, "experimental-delivery-result.json"), JsonSerializer.Serialize(new
        {
            Success = true, DevelopmentOverrideUsed = false, ManualCheckpointWithNoChanges = true, SelfContained = true,
            NoStickyCheckpoint = true, ExactCheckpointAndFallbackRestore = true, RequireTransform = true,
            CoreFallbackWithWarnings = true, CheckpointNeverFallsBack = true, DisableCreatesCoreFull = true,
            MissingPluginBlocked = true, CanceledAndRollingBlocked = true, UiAutomated = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;

        async Task<VersionRepresentation> CurrentAsync()
        {
            var version = (await history.WorkspaceStore.LoadAsync()).Value!.SourceBaselines.Single().BaseVersionId;
            return (await history.Query.GetAllRepresentationsAsync()).Single(r => r.VersionId == version);
        }
        async Task VerifyAsync(VersionRepresentation representation, string name)
        {
            var archives = new SevenZipHistoryArchiveBackend(config);
            var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(archives), new SmartDeltaRepresentationHandler(archives),
                NativeArtifactHostService.Handler(config, history)]);
            var restored = Path.Combine(root, "delivery-restore-" + name);
            await engine.MaterializeAsync(representation.RepresentationId, await history.Query.GetAllRepresentationsAsync(),
                await NativeHistoryApplicationService.BuildEnvironmentAsync(history, default), MaterializationFidelity.Exact, restored);
            var expected = await MergeTreeManifest.ReadAsync(config.SourceFolders.Single().Path, p => p != "session.lock", default);
            if ((await MergeTreeManifest.ReadAsync(restored, _ => true, default)).Digest != expected.Digest)
                throw new InvalidDataException("Delivery restore differs from captured input.");
        }
    }
}
#endif
