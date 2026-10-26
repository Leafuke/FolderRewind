using FolderRewind.History.Capture;
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

public sealed record PreparedMergeSource(SourceVersion Version, string StagingDirectory, string TreeDigest)
{
    public ImmutableArray<Plugin.Abstractions.PluginDiagnostic> SemanticDiagnostics { get; init; } = [];
    public ImmutableArray<string> RequiredAbsentPaths { get; init; } = [];
}
public sealed record PreparedMerge(MergeSession Session, ImmutableArray<PreparedMergeSource> Sources,
    ImmutableArray<object> Facts, SourceCheckpoint Checkpoint, BranchUpdate Update,
    ImmutableArray<LocalReplicaCatalogEntry> NewReplicas, PackId PackId, HistoryTransactionId TransactionId);

public sealed class HistoryMergeCommitBuilder(HistoryRuntime history, HistoryRestoreService restore,
    IHistoryCompactionBackend archives, IArchiveRepresentationBackend materializer,
    Func<SourceVersion, string, CancellationToken, Task<IReadOnlyList<VersionMetadataSnapshot>>>? metadata = null,
    SemanticMergeEngine? semantic = null)
{
    private MergeProviderDescriptor Descriptor => semantic?.Descriptor ?? MergeProviderDescriptor.Generic;
    internal async Task<IDisposable> ValidateAndLockAsync(PreparedMerge prepared, CancellationToken token)
    {
        history.MergeSessions.RequireCurrentContract(prepared.Session, Descriptor);
        var held = new PayloadLocks();
        try
        {
            if (semantic is not null) held.Items.Add(await semantic.AcquireAsync(token).ConfigureAwait(false));
            foreach (var source in prepared.Sources)
            {
                RequireCoveragePreconditions(prepared.Session.Plan.Bindings.Single(b => b.SourceId == source.Version.SourceId).TargetDirectory,
                    source.RequiredAbsentPaths, token);
                var representation = prepared.Facts.OfType<VersionRepresentation>().SingleOrDefault(r => r.VersionId == source.Version.VersionId);
                if (representation is null)
                {
                    held.Items.Add(await restore.VerifyExactTreeAndLockAsync(source.Version, source.TreeDigest, token).ConfigureAwait(false));
                    continue;
                }
                var path = prepared.NewReplicas.Single(r => r.RepresentationId == representation.RepresentationId).Locator.AbsolutePath;
                var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                held.Items.Add(input);
                if (representation.RepresentationSpecificMetadata.TryGetValue("storageLength", out var length)
                    && input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) != length)
                    throw new InvalidDataException("Prepared payload length changed.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
                if (!StringComparer.OrdinalIgnoreCase.Equals(hash, representation.RepresentationSpecificMetadata["storageSha256"]))
                    throw new InvalidDataException("Prepared payload storage hash changed.");
                var root = Path.Combine(history.Repository.Paths.TransactionsRoot, "merge-verification", Guid.NewGuid().ToString("N"));
                try
                {
                    await materializer.MaterializeAsync([new(representation, path)], root, token).ConfigureAwait(false);
                    if ((await MergeTreeManifest.ReadAsync(root, _ => true, token).ConfigureAwait(false)).Digest != source.TreeDigest)
                        throw new InvalidDataException("Prepared payload logical state changed.");
                }
                finally { HistoryRestoreTransactionJournalStore.CleanupStaging([root]); }
            }
            return held;
        }
        catch { held.Dispose(); throw; }
    }

    internal static void RequireCoveragePreconditions(string root, IEnumerable<string> absentPaths, CancellationToken token)
    {
        foreach (var relative in absentPaths)
        {
            token.ThrowIfCancellationRequested(); SemanticMergeSourceView.Canonical(relative);
            var path = Path.GetFullPath(root); var absent = false;
            foreach (var segment in new[] { "" }.Concat(relative.Split('/')))
            {
                if (segment.Length != 0) path = Path.Combine(path, segment);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { absent = true; break; }
                catch (DirectoryNotFoundException) { absent = true; break; }
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) break;
            }
            if (!absent) throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.CoordinationScopeChanged,
                Detail: "Semantic Merge coverage requires an absent target path: " + relative));
        }
    }

    private sealed class PayloadLocks : IDisposable
    {
        internal List<IDisposable> Items { get; } = [];
        public void Dispose() { foreach (var item in Items) item.Dispose(); }
    }

    public async Task<PreparedMerge> BuildAsync(MergeSession session, CancellationToken token = default)
    {
        var stage = "load-prepared";
        try
        {
            history.MergeSessions.RequireCurrentContract(session, Descriptor);
            using var providerLease = semantic is null ? null : await semantic.AcquireAsync(token).ConfigureAwait(false);
            if (session.State != MergeSessionState.Ready) throw new InvalidOperationException("Merge Session has unresolved conflicts.");
            if (history.MergeSessions.LoadPrepared(session) is { } stored)
            {
                var cached = stored.Restore(session);
                if (HasMismatchedMetadataCaptureTime(cached))
                {
                    // Older Hosts timestamped metadata after archive creation. Rebuild only unpublished
                    // preparation; never rewrite history, decisions or an already reviewed result in place.
                    if (File.Exists(history.Repository.Paths.GetPackPath(cached.PackId)))
                        throw new InvalidDataException("Published Merge metadata cannot be regenerated.");
                    token.ThrowIfCancellationRequested();
                    history.MergeSessions.DiscardPrepared(session, stored);
                }
                else
                {
                    stage = "validate-facts";
                    await ValidateFactsAsync(cached, token).ConfigureAwait(false);
                    return cached;
                }
            }
            var service = new HistoryMergeService(history, restore, semantic: semantic);
            stage = "assemble-result";
            var sources = history.MergeSessions.Sources(session);
            if (history.MergeSessions.SourceCount(session) != session.Plan.Sources.Length) throw new InvalidDataException("Merge preparation is incomplete.");
            var root = Path.Combine(history.MergeSessions.SessionDirectory(session.Id), "results", Guid.NewGuid().ToString("N"));
            history.MergeSessions.RegisterArtifact(session, root);
            Directory.CreateDirectory(root);
            var facts = new List<object>(); var replicas = new List<LocalReplicaCatalogEntry>(); var prepared = new List<PreparedMergeSource>();
            var roster = new List<CheckpointSource>();
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                stage = "assemble-result";
                var plan = source.Plan; var tree = source.Automatic; CheckpointSource? selected = null;
                if (plan.Action == HistoryMergeSourceAction.Remove) continue;
                if (plan.Action == HistoryMergeSourceAction.Reuse)
                    selected = plan.Ours?.VersionId == plan.ReuseVersionId ? plan.Ours : plan.Theirs;
                else if (plan.Action != HistoryMergeSourceAction.MergeFiles)
                {
                    var resolution = service.AllConflicts(session, plan.SourceId).Single().Resolution ?? throw new InvalidOperationException("Source conflict is unresolved.");
                    selected = resolution.Choice == MergeResolutionChoice.Ours ? plan.Ours
                        : resolution.Choice == MergeResolutionChoice.Theirs ? plan.Theirs : throw new InvalidOperationException("Manual Source creation is not supported.");
                    if (selected is null) continue;
                    tree = resolution.Choice == MergeResolutionChoice.Ours ? source.Ours : source.Theirs;
                }
                else
                {
                    var files = tree.Files.ToBuilder();
                    var directories = tree.Directories.ToHashSet(StringComparer.Ordinal);
                    foreach (var (conflict, resolution) in service.AllConflicts(session, plan.SourceId))
                    {
                        if (conflict.SemanticSubject is not null || conflict.AutomaticDisplayOnly) continue;
                        if (resolution is null || resolution.PlanRevision != session.Plan.Revision || resolution.InputSignature != conflict.InputSignature)
                            throw new InvalidOperationException("Conflict resolution is missing or stale.");
                        foreach (var path in conflict.Subject.Paths) { files.Remove(path); directories.Remove(path); }
                        if (resolution.Choice == MergeResolutionChoice.Manual)
                        {
                            if (!conflict.CanImportFile || resolution.Manual is null)
                                throw new InvalidOperationException("Invalid manual file resolution.");
                            files.Add(conflict.Subject.Paths[0], resolution.Manual);
                        }
                        else if (resolution.Choice != MergeResolutionChoice.Delete)
                        {
                            var selectedFiles = resolution.Choice switch
                            {
                                MergeResolutionChoice.Ours => conflict.Ours, MergeResolutionChoice.Theirs => conflict.Theirs,
                                MergeResolutionChoice.Base => conflict.Base, _ => throw new InvalidDataException("Invalid file Merge choice.")
                            };
                            foreach (var pair in selectedFiles) files.Add(pair.Key, pair.Value);
                            directories.UnionWith(resolution.Choice == MergeResolutionChoice.Ours ? conflict.OursDirectories
                                : resolution.Choice == MergeResolutionChoice.Base ? conflict.BaseDirectories : conflict.TheirsDirectories);
                        }
                    }
                    tree = MergeTreeManifest.Create(files, directories) with
                    { HasCompleteDirectoryInventory = source.HasCompleteDirectoryInventory };
                    if (semantic is not null) tree = await semantic.BuildAsync(session, source, tree, root, token).ConfigureAwait(false);
                    else if (tree.Digest == source.Ours.Digest) selected = plan.Ours;
                    else if (tree.Digest == source.Theirs.Digest) selected = plan.Theirs;
                }
                SourceDirectorySnapshot.Validate(tree.Directories, tree.Files.Keys);
                if (GenericFileMergeProvider.StructuralGroups(tree).Count != 0) throw new InvalidOperationException("Resolved tree contains path conflicts.");
                var descriptor = selected ?? plan.Ours ?? plan.Theirs ?? throw new InvalidOperationException("Merge cannot create a Source without parents.");
                var staging = Path.Combine(root, plan.SourceId.ToString()); Directory.CreateDirectory(staging);
                var binding = new HistoryRestoreSourceBinding(plan.SourceId, staging, descriptor.EffectiveSourceBoundary)
                    { LogicalDirectory = descriptor.SourceDescriptorSnapshot.PathHint };
                var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding);
                var includeDirectory = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding, isDirectory: true);
                foreach (var path in tree.Files.Keys)
                    if (!include(path)) throw new InvalidOperationException("Merge proposal escapes its boundary.");
                var allowedDirectories = tree.Directories.Where(includeDirectory).ToHashSet(StringComparer.Ordinal);
                foreach (var path in tree.Files.Keys.Concat(allowedDirectories.ToArray()))
                    for (int slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
                    {
                        allowedDirectories.Add(path[..slash]);
                        if (slash == 0) break;
                    }
                foreach (var directory in tree.Directories)
                {
                    token.ThrowIfCancellationRequested();
                    if (!allowedDirectories.Contains(directory)) throw new InvalidOperationException("Merge directory escapes its boundary.");
                    Directory.CreateDirectory(Plugin.Runtime.Artifacts.ArtifactPathRules.ResolveUnderRoot(staging, directory));
                }
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
                var actual = await MergeTreeManifest.ReadAsync(staging, include, token, includeDirectory).ConfigureAwait(false);
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
                    var output = Path.Combine(history.Repository.Paths.RepositoryRoot, "payloads", representationId.ToString());
                    history.MergeSessions.RegisterArtifact(session, output);
                    stage = "archive-create";
                    var payload = await archives.CreateFullAsync(version, staging, representationId, output, token).ConfigureAwait(false);
                    var fullPayload = Path.GetFullPath(payload.PayloadPath);
                    if (!fullPayload.StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || (File.GetAttributes(fullPayload) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Merge archive escapes its owned payload directory.");
                    using (var seal = new FileStream(fullPayload, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) seal.Flush(true);
                    await using var payloadStream = File.OpenRead(payload.PayloadPath);
                    var storageHash = Convert.ToHexString(await SHA256.HashDataAsync(payloadStream, token).ConfigureAwait(false));
                    var payloadMetadata = payload.Metadata.SetItem("fileName", Path.GetFileName(payload.PayloadPath)).SetItem("storageSha256", storageHash)
                        .SetItem("storageLength", payloadStream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (tree.HasCompleteDirectoryInventory) payloadMetadata = payloadMetadata.SetItem("mergeDirectoryEvidence", "1");
                    var representation = new VersionRepresentation(representationId, version.VersionId, RepresentationKind.CoreFull,
                        payload.Format, [], MaterializationFidelity.Exact, tree.Digest, tree.Digest, payloadMetadata);
                    stage = "archive-verify";
                    var verification = await archives.DeepVerifyAsync(representation, payload.PayloadPath, token).ConfigureAwait(false);
                    if (!verification.Success) throw new InvalidDataException("Merge archive verification failed: " + verification.Diagnostic);
                    var verifyRoot = Path.Combine(root, "verify-" + representationId);
                    try
                    {
                        stage = "archive-roundtrip";
                        await materializer.MaterializeAsync([new(representation, payload.PayloadPath)], verifyRoot, token).ConfigureAwait(false);
                        if ((await MergeTreeManifest.ReadAsync(verifyRoot, _ => true, token).ConfigureAwait(false)).Digest != tree.Digest)
                            throw new InvalidDataException("Merge archive round-trip changed its logical state.");
                    }
                    finally { HistoryRestoreTransactionJournalStore.CleanupStaging([verifyRoot]); }
                    facts.Add(version); facts.Add(representation);
                    stage = "capture-metadata";
                    if (metadata is not null) facts.AddRange(await metadata(version, staging, token).ConfigureAwait(false));
                    replicas.Add(new(representationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(payload.PayloadPath), DateTimeOffset.UtcNow));
                }
                roster.Add(new(plan.SourceId, descriptor.SourceDescriptorSnapshot, version.VersionId, CheckpointSourceDisposition.CarriedForward, version.EffectiveSourceBoundary));
                prepared.Add(new(version, staging, tree.Digest) { SemanticDiagnostics = tree.SemanticDiagnostics, RequiredAbsentPaths = tree.RequiredAbsentPaths });
            }
            SourceCheckpoint checkpoint;
            if (session.Plan.Mode == HistoryMergeMode.FastForwardLike)
                checkpoint = await history.Query.GetCheckpointAsync(session.Plan.Theirs.TargetCheckpointId!.Value, token).ConfigureAwait(false) ?? throw new InvalidDataException("Fast-forward checkpoint is missing.");
            else
            {
                checkpoint = new(CheckpointId.New(), history.ConfigId, DateTimeOffset.UtcNow, null, HistoryProvenance.Native("branch-merge"), roster,
                    [session.Plan.Ours.TargetCheckpointId!.Value, session.Plan.Theirs.TargetCheckpointId!.Value], CheckpointCreationKind.Merge);
                facts.Add(checkpoint);
            }
            using var resolutionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            bool firstResolution = true;
            foreach (var c in service.AllConflicts(session))
            {
                if (!firstResolution) resolutionHash.AppendData([(byte)'\n']);
                firstResolution = false;
                resolutionHash.AppendData(Encoding.UTF8.GetBytes($"{c.Conflict.InputSignature}:{c.Resolution?.Choice}:{c.Resolution?.Manual?.Digest}"));
            }
            var resolutionDigest = Convert.ToHexString(resolutionHash.GetHashAndReset());
            var provenance = new BranchMergeProvenance(session.Plan.Mode == HistoryMergeMode.FastForwardLike ? BranchMergeMode.FastForwardLike : BranchMergeMode.ThreeWay,
                session.Plan.Ours.BranchId, session.Plan.Theirs.BranchId, session.Plan.Ours.UpdateId, session.Plan.Theirs.UpdateId,
                session.Plan.Ours.TargetCheckpointId!.Value, session.Plan.Theirs.TargetCheckpointId!.Value, session.Plan.BaseCheckpointId,
                session.Plan.ProviderVersion, session.Plan.PolicyVersion, resolutionDigest);
            var update = new BranchUpdate(BranchUpdateId.New(), session.Plan.Ours.BranchId,
                new[] { session.Plan.Ours.UpdateId, session.Plan.Theirs.UpdateId }, session.Plan.Ours.Name, checkpoint.CheckpointId,
                false, DateTimeOffset.UtcNow, BranchUpdateReason.Merged, session.Plan.Ours.SourceId, provenance);
            facts.Add(update);
            var result = new PreparedMerge(session, prepared.ToImmutableArray(), facts.ToImmutableArray(), checkpoint, update, replicas.ToImmutableArray(), PackId.New(), HistoryTransactionId.New());
            stage = "validate-facts";
            await ValidateFactsAsync(result, token).ConfigureAwait(false);
            stage = "persist-prepared";
            return history.MergeSessions.SavePrepared(session, result).Restore(session);
        }
        catch (Exception ex)
        {
            ex.Data["MergeStage"] = stage;
            throw;
        }
    }

    internal static bool HasMismatchedMetadataCaptureTime(PreparedMerge prepared)
    {
        var versions = prepared.Facts.OfType<SourceVersion>().ToDictionary(v => v.VersionId);
        return prepared.Facts.OfType<VersionMetadataSnapshot>().Any(m =>
            versions.TryGetValue(m.VersionId, out var owner) && m.CapturedAtUtc != owner.CreatedAtUtc);
    }

    private async Task ValidateFactsAsync(PreparedMerge prepared, CancellationToken token)
    {
        var codec = new HistoryPackCodec();
        var pack = new HistoryCommitPack(prepared.PackId, prepared.TransactionId, prepared.Update.CreatedAtUtc,
            prepared.Facts.Select(f => codec.CreateObject(f)));
        var packs = await history.Repository.ReadAllPacksAsync(token).ConfigureAwait(false);
        new HistoryRepositoryValidator(codec).Validate(history.ConfigId, packs.Append(codec.Decode(codec.Encode(pack))));
    }
}
