using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryReplicaRecoveryHint(RepresentationId RepresentationId, string RelativePath,
    long Size, string StorageSha256);
public sealed record HistoryReplicaRecoveryCandidate(RepresentationId RepresentationId, string Path,
    long Size, string StorageSha256, bool IdentityVerified);
public sealed record HistoryReplicaRecoveryPreview(HistoryConfigId ConfigId, long CatalogRevision,
    string BackupRoot, ImmutableArray<HistoryReplicaRecoveryCandidate> Candidates,
    ImmutableArray<RepresentationId> Missing);
public sealed record HistoryReplicaRecoveryFailure(RepresentationId RepresentationId, string Diagnostic);
public sealed record HistoryReplicaRecoveryResult(int RegisteredReplicas, ImmutableArray<VersionAssessment> Versions,
    ImmutableArray<HistoryReplicaRecoveryFailure> Failures);

/// <summary>File names locate candidates; hashes or explicit confirmation establish the binding.</summary>
public sealed class HistoryReplicaRecoveryService(HistoryRuntime runtime)
{
    public async Task<HistoryReplicaRecoveryPreview> InspectAsync(string backupRoot,
        IEnumerable<HistoryReplicaRecoveryHint>? hints = null, CancellationToken token = default)
    {
        var root = Path.GetFullPath(backupRoot);
        var catalog = await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        RequireReadable(catalog);
        var representations = await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var replicas = await runtime.Query.GetAllStorageReplicasAsync(token).ConfigureAwait(false);
        var hintMap = (hints ?? []).GroupBy(h => h.RepresentationId).ToDictionary(g => g.Key, g => g.ToArray());
        var files = Directory.Exists(root) ? EnumerateFiles(root, token).GroupBy(Path.GetFileName,
            StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key!, g => g.ToArray(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var candidates = ImmutableArray.CreateBuilder<HistoryReplicaRecoveryCandidate>();
        var missing = ImmutableArray.CreateBuilder<RepresentationId>();
        foreach (var representation in representations.Where(r => r.Kind != RepresentationKind.PluginArtifact))
        {
            token.ThrowIfCancellationRequested();
            var declared = hintMap.GetValueOrDefault(representation.RepresentationId) ?? [];
            var paths = declared.Select(h => ResolveUnderRoot(root, h.RelativePath)).Where(File.Exists).ToList();
            var name = representation.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
                ?? representation.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName");
            if (name is not null && name == Path.GetFileName(name) && files.TryGetValue(name, out var matching))
                paths.AddRange(matching);
            paths.AddRange((catalog.Value?.Entries ?? []).Where(e => e.RepresentationId == representation.RepresentationId
                && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && File.Exists(e.Locator.AbsolutePath)
                && IsUnderRoot(root, e.Locator.AbsolutePath)).Select(e => e.Locator.AbsolutePath));
            var found = false;
            foreach (var path in paths.Where(p => !PreviewStoragePaths.IsReserved(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                RequireRegularPath(root, path);
                var facts = await ReadFileFactsAsync(path, token).ConfigureAwait(false);
                var expected = declared.Where(h => ResolveUnderRoot(root, h.RelativePath).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
                var shared = replicas.Where(r => r.RepresentationId == representation.RepresentationId
                    && !string.IsNullOrEmpty(r.ExpectedStorageSha256)).ToArray();
                bool hasIdentity = expected.Length > 0 || shared.Length > 0;
                bool matches = expected.Any(h => h.Size == facts.Size && EqualHash(h.StorageSha256, facts.Hash))
                    || shared.Any(r => (r.ExpectedSize is null || r.ExpectedSize == facts.Size) && EqualHash(r.ExpectedStorageSha256!, facts.Hash));
                if (hasIdentity && !matches) continue;
                candidates.Add(new(representation.RepresentationId, path, facts.Size, facts.Hash, matches));
                found = true;
            }
            if (!found) missing.Add(representation.RepresentationId);
        }
        return new(runtime.ConfigId, catalog.Value?.CatalogRevision ?? -1, root, candidates.ToImmutable(), missing.ToImmutable());
    }

    public async Task<HistoryReplicaRecoveryResult> ApplyAsync(HistoryReplicaRecoveryPreview preview,
        IReadOnlyCollection<HistoryReplicaRecoveryCandidate> selected, IArchiveRepresentationBackend archive,
        bool confirmUnverified, bool credentialReady = true, CancellationToken token = default,
        RepresentationRuntime? consumers = null)
    {
        if (preview.ConfigId != runtime.ConfigId || selected.GroupBy(c => c.RepresentationId).Any(g => g.Count() > 1)
            || selected.Any(c => !preview.Candidates.Contains(c) || (!c.IdentityVerified && !confirmUnverified)))
            throw new ArgumentException("Recovery selection requires one confirmed candidate per representation.");
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(runtime.ConfigId, token).ConfigureAwait(false);
        await using var mutation = await runtime.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var catalog = await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        RequireReadable(catalog);
        if ((catalog.Value?.CatalogRevision ?? -1) != preview.CatalogRevision)
            throw new DeviceLocalStateConflictException("Replica catalog changed after recovery preview.");
        var graph = await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var entries = (catalog.Value?.Entries ?? []).ToList();
        var failures = ImmutableArray.CreateBuilder<HistoryReplicaRecoveryFailure>();
        var added = 0;
        foreach (var candidate in selected)
        {
            token.ThrowIfCancellationRequested();
            RequireRegularPath(preview.BackupRoot, candidate.Path);
            var facts = await ReadFileFactsAsync(candidate.Path, token).ConfigureAwait(false);
            if (facts.Size != candidate.Size || !EqualHash(facts.Hash, candidate.StorageSha256))
                throw new IOException("Archive changed after recovery preview.");
            var representation = graph.Single(r => r.RepresentationId == candidate.RepresentationId);
            if (!credentialReady && !candidate.IdentityVerified)
            { failures.Add(new(candidate.RepresentationId, "CredentialRequired")); continue; }
            if (credentialReady)
            {
                var verified = await archive.VerifyAsync(representation, candidate.Path, token).ConfigureAwait(false);
                if (!verified.Success) { failures.Add(new(candidate.RepresentationId, verified.Diagnostic)); continue; }
                // Content verification can run a process; recheck the input before committing its locator.
                facts = await ReadFileFactsAsync(candidate.Path, token).ConfigureAwait(false);
                if (facts.Size != candidate.Size || !EqualHash(facts.Hash, candidate.StorageSha256))
                    throw new IOException("Archive changed during verification.");
            }
            if (entries.Any(e => e.RepresentationId == candidate.RepresentationId
                && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                && e.Locator.AbsolutePath.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase))) continue;
            entries.Add(new(candidate.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(candidate.Path), DateTimeOffset.UtcNow));
            added++;
        }
        if (added > 0)
        {
            var revision = catalog.Value?.CatalogRevision ?? -1;
            var updated = new LocalReplicaCatalog(runtime.ConfigId, revision + 1, entries);
            runtime.MigrationState.MarkImported(false);
            runtime.MigrationState.Prepare([HistoryLocalStateJournalRecovery.CreateCatalogIntent(updated, revision)]);
            await runtime.MigrationState.RecoverAsync(new(runtime.WorkspaceStore, runtime.LocalReplicaCatalogStore), CancellationToken.None).ConfigureAwait(false);
            await runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            runtime.ChangeFeed.Publish(runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
        }
        var engine = consumers ?? new RepresentationRuntime([new CoreArchiveRepresentationHandler(archive), new SmartDeltaRepresentationHandler(archive)]);
        var environment = new RepresentationEnvironment(entries, [], []);
        var versions = ImmutableArray.CreateBuilder<VersionAssessment>();
        foreach (var version in await runtime.Query.GetAllVersionsAsync(CancellationToken.None).ConfigureAwait(false))
            versions.Add(credentialReady ? await engine.AssessVersionAsync(version.VersionId, graph, environment,
                AssessmentDepth.Deep, MaterializationFidelity.Exact, CancellationToken.None).ConfigureAwait(false)
                : new(version.VersionId, AssessmentDepth.Deep, MaterializationFidelity.Exact, HistoryReadiness.Blocked, null, []));
        return new(added, versions.ToImmutable(), failures.ToImmutable());
    }

    internal static async Task<(long Size, string Hash)> ReadFileFactsAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return (file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).ToLowerInvariant());
    }
    internal static string ResolveUnderRoot(string root, string relative)
    {
        if (!History.Storage.HistoryRepositoryPaths.IsSafeRepositoryRelativePath(relative))
            throw new InvalidDataException("Unsafe migration payload path.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, path)) throw new InvalidDataException("Migration payload escapes its backup root.");
        return path;
    }
    internal static bool IsUnderRoot(string root, string path)
        => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void RequireRegularPath(string root, string path)
    {
        if (!IsUnderRoot(root, path) && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Migration payload escapes its backup root.");
        var current = path;
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Migration recovery cannot follow filesystem links.");
            if (Path.TrimEndingDirectorySeparator(current).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
    }
    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Backup root cannot be a link.");
        var pending = new Stack<string>(); pending.Push(root); var count = 0;
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (PreviewStoragePaths.IsReserved(entry)) continue;
                if (++count > 200000) throw new IOException("Backup candidate scan exceeds its entry limit.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else yield return entry;
            }
    }
    private static bool EqualHash(string first, string second) => StringComparer.OrdinalIgnoreCase.Equals(first, second);
    private static void RequireReadable(DeviceLocalStateLoadResult<LocalReplicaCatalog> load)
    {
        if (load.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new DeviceLocalStateConflictException("Replica catalog requires recovery before rebinding files.");
    }
}
