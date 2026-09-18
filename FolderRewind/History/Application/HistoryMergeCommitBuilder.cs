using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record PreparedMergeSource(SourceVersion Version, string StagingDirectory, string TreeDigest);
public sealed record PreparedMerge(MergeSession Session, ImmutableArray<PreparedMergeSource> Sources,
    ImmutableArray<object> Facts, ConfigurationCheckpoint Checkpoint, BranchUpdate Update,
    ImmutableArray<LocalReplicaCatalogEntry> NewReplicas, PackId PackId, HistoryTransactionId TransactionId);

public sealed class HistoryMergeCommitBuilder(HistoryRuntime history, HistoryRestoreService restore,
    IHistoryCompactionBackend archives, IArchiveRepresentationBackend materializer,
    Func<SourceVersion, string, CancellationToken, Task<IReadOnlyList<VersionMetadataSnapshot>>>? metadata = null)
{
    public async Task<PreparedMerge> BuildAsync(MergeSession session, CancellationToken token = default)
    {
        if (session.State != MergeSessionState.Ready) throw new InvalidOperationException("Merge Session has unresolved conflicts.");
        var service = new HistoryMergeService(history, restore);
        var conflicts = service.AllConflicts(session).ToLookup(c => c.Conflict.Subject.SourceId);
        var sources = history.MergeSessions.Sources(session);
        if (sources.Count != session.Plan.Sources.Length) throw new InvalidDataException("Merge preparation is incomplete.");
        var root = Path.Combine(history.MergeSessions.SessionDirectory(session.Id), "results", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var facts = new List<object>(); var replicas = new List<LocalReplicaCatalogEntry>(); var prepared = new List<PreparedMergeSource>();
        var roster = new List<CheckpointSource>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var plan = source.Plan; var tree = source.Automatic; CheckpointSource? selected = null;
            if (plan.Action == HistoryMergeSourceAction.Remove) continue;
            if (plan.Action == HistoryMergeSourceAction.Reuse)
                selected = plan.Ours?.VersionId == plan.ReuseVersionId ? plan.Ours : plan.Theirs;
            else if (plan.Action != HistoryMergeSourceAction.MergeFiles)
            {
                var resolution = conflicts[plan.SourceId].Single().Resolution ?? throw new InvalidOperationException("Source conflict is unresolved.");
                selected = resolution.Choice == MergeResolutionChoice.Ours ? plan.Ours
                    : resolution.Choice == MergeResolutionChoice.Theirs ? plan.Theirs : throw new InvalidOperationException("Manual Source creation is not supported.");
                if (selected is null) continue;
                tree = resolution.Choice == MergeResolutionChoice.Ours ? source.Ours : source.Theirs;
            }
            else
            {
                var files = tree.Files.ToBuilder();
                foreach (var (conflict, resolution) in conflicts[plan.SourceId])
                {
                    if (resolution is null || resolution.PlanRevision != session.Plan.Revision || resolution.InputSignature != conflict.InputSignature)
                        throw new InvalidOperationException("Conflict resolution is missing or stale.");
                    foreach (var path in conflict.Subject.Paths) files.Remove(path);
                    if (resolution.Choice == MergeResolutionChoice.Manual)
                    {
                        if (conflict.Subject.Paths.Length != 1 || resolution.Manual is null || conflict.Kind == MergeConflictKind.PathStructure)
                            throw new InvalidOperationException("Invalid manual file resolution.");
                        files.Add(conflict.Subject.Paths[0], resolution.Manual);
                    }
                    else foreach (var pair in resolution.Choice == MergeResolutionChoice.Ours ? conflict.Ours : conflict.Theirs) files.Add(pair.Key, pair.Value);
                }
                tree = MergeTreeManifest.Create(files);
                if (tree.Digest == source.Ours.Digest) selected = plan.Ours;
                else if (tree.Digest == source.Theirs.Digest) selected = plan.Theirs;
            }
            if (GenericFileMergeProvider.StructuralGroups(tree.Files.Keys).Count != 0) throw new InvalidOperationException("Resolved tree contains path conflicts.");
            var descriptor = selected ?? plan.Ours ?? plan.Theirs ?? throw new InvalidOperationException("Merge cannot create a Source without parents.");
            var staging = Path.Combine(root, plan.SourceId.ToString()); Directory.CreateDirectory(staging);
            var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(new(plan.SourceId, staging, descriptor.EffectiveSourceBoundary));
            foreach (var pair in tree.Files)
            {
                if (!include(pair.Key)) throw new InvalidOperationException("Merge proposal escapes its boundary.");
                var handle = Path.GetFullPath(pair.Value.Handle);
                var ownedRoot = Path.GetFullPath(history.MergeSessions.SessionDirectory(session.Id)) + Path.DirectorySeparatorChar;
                if (!handle.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Merge content handle is not Session-owned.");
                for (var entry = handle; entry.Length >= ownedRoot.TrimEnd(Path.DirectorySeparatorChar).Length; entry = Path.GetDirectoryName(entry)!)
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Merge handle traverses a link.");
                    if (entry.Equals(ownedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
                }
                var output = Plugin.Runtime.Artifacts.ArtifactPathRules.ResolveUnderRoot(staging, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(handle, output, overwrite: false);
            }
            var actual = await MergeTreeManifest.ReadAsync(staging, include, token).ConfigureAwait(false);
            if (actual.Digest != tree.Digest) throw new InvalidDataException("Merge content changed after analysis.");
            SourceVersion version;
            if (selected?.VersionId is { } existingId)
                version = await history.Query.GetVersionAsync(existingId, token).ConfigureAwait(false) ?? throw new InvalidDataException("Selected Version disappeared.");
            else
            {
                version = new(VersionId.New(), history.ConfigId, plan.SourceId,
                    new[] { plan.Ours?.VersionId, plan.Theirs?.VersionId }.OfType<VersionId>().Distinct(), DateTimeOffset.UtcNow, null,
                    CaptureScope.FullSource, CaptureOutcome.Captured, [], descriptor.SourceDescriptorSnapshot, tree.Digest,
                    HistoryProvenance.Native("branch-merge"), descriptor.EffectiveSourceBoundary, SourceVersionCreationKind.Merge);
                var representationId = RepresentationId.New();
                var output = Path.Combine(history.MergeSessions.SessionDirectory(session.Id), "payloads", representationId.ToString());
                var payload = await archives.CreateFullAsync(version, staging, representationId, output, token).ConfigureAwait(false);
                await using var payloadStream = File.OpenRead(payload.PayloadPath);
                var storageHash = Convert.ToHexString(await SHA256.HashDataAsync(payloadStream, token).ConfigureAwait(false));
                var representation = new VersionRepresentation(representationId, version.VersionId, RepresentationKind.CoreFull,
                    payload.Format, [], MaterializationFidelity.Exact, tree.Digest, tree.Digest,
                    payload.Metadata.SetItem("fileName", Path.GetFileName(payload.PayloadPath)).SetItem("storageSha256", storageHash));
                var verification = await archives.DeepVerifyAsync(representation, payload.PayloadPath, token).ConfigureAwait(false);
                if (!verification.Success) throw new InvalidDataException("Merge archive verification failed.");
                var verifyRoot = Path.Combine(root, "verify-" + representationId);
                try
                {
                    await materializer.MaterializeAsync([new(representation, payload.PayloadPath)], verifyRoot, token).ConfigureAwait(false);
                    if ((await MergeTreeManifest.ReadAsync(verifyRoot, _ => true, token).ConfigureAwait(false)).Digest != tree.Digest)
                        throw new InvalidDataException("Merge archive round-trip changed its logical state.");
                }
                finally { HistoryRestoreTransactionJournalStore.CleanupStaging([verifyRoot]); }
                facts.Add(version); facts.Add(representation);
                if (metadata is not null) facts.AddRange(await metadata(version, staging, token).ConfigureAwait(false));
                replicas.Add(new(representationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload.PayloadPath), DateTimeOffset.UtcNow));
            }
            roster.Add(new(plan.SourceId, descriptor.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.CarriedForward, version.EffectiveSourceBoundary));
            prepared.Add(new(version, staging, tree.Digest));
        }
        ConfigurationCheckpoint checkpoint;
        if (session.Plan.Mode == HistoryMergeMode.FastForwardLike)
            checkpoint = await history.Query.GetCheckpointAsync(session.Plan.Theirs.TargetCheckpointId!.Value, token).ConfigureAwait(false) ?? throw new InvalidDataException("Fast-forward checkpoint is missing.");
        else
        {
            checkpoint = new(CheckpointId.New(), history.ConfigId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("branch-merge"), roster,
                [session.Plan.Ours.TargetCheckpointId!.Value, session.Plan.Theirs.TargetCheckpointId!.Value], CheckpointCreationKind.Merge);
            facts.Add(checkpoint);
        }
        var resolutionDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            service.AllConflicts(session).Select(c => $"{c.Conflict.InputSignature}:{c.Resolution?.Choice}:{c.Resolution?.Manual?.Digest}")))));
        var provenance = new BranchMergeProvenance(session.Plan.Mode == HistoryMergeMode.FastForwardLike ? BranchMergeMode.FastForwardLike : BranchMergeMode.ThreeWay,
            session.Plan.Ours.BranchId, session.Plan.Theirs.BranchId, session.Plan.Ours.UpdateId, session.Plan.Theirs.UpdateId,
            session.Plan.Ours.TargetCheckpointId!.Value, session.Plan.Theirs.TargetCheckpointId!.Value, session.Plan.BaseCheckpointId,
            session.Plan.ProviderVersion, session.Plan.PolicyVersion, resolutionDigest);
        var update = new BranchUpdate(BranchUpdateId.New(), session.Plan.Ours.BranchId,
            new[] { session.Plan.Ours.UpdateId, session.Plan.Theirs.UpdateId }, session.Plan.Ours.Name, checkpoint.CheckpointId,
            false, DateTimeOffset.UtcNow, BranchUpdateReason.Merged, provenance);
        facts.Add(update);
        return new(session, prepared.ToImmutableArray(), facts.ToImmutableArray(), checkpoint, update, replicas.ToImmutableArray(), PackId.New(), HistoryTransactionId.New());
    }
}
