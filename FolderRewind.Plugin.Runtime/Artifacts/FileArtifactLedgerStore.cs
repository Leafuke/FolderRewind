using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Artifacts;

public sealed class FileArtifactLedgerStore
{
    private const string MetadataDirectoryName = ".folderrewind";
    private readonly string _repositoryRoot;
    private readonly string _metadataRoot;
    private readonly string _ledgerPath;
    private readonly string _transactionsRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<ArtifactTransactionStage>? _observeStage;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileArtifactLedgerStore(
        string repositoryRoot,
        Action<ArtifactTransactionStage>? observeStage = null)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
        _metadataRoot = Path.Combine(_repositoryRoot, MetadataDirectoryName);
        _ledgerPath = Path.Combine(_metadataRoot, "artifact-ledger.v1.json");
        _transactionsRoot = Path.Combine(_metadataRoot, "transactions");
        _observeStage = observeStage;
        Directory.CreateDirectory(_repositoryRoot);
        Directory.CreateDirectory(_metadataRoot);
        Directory.CreateDirectory(_transactionsRoot);
        Directory.CreateDirectory(Path.Combine(_repositoryRoot, "artifacts"));
    }

    public ArtifactTransformStagingArea CreateStagingArea(
        string transactionId,
        int maximumFiles = 10_000,
        long maximumBytes = 8L * 1024 * 1024 * 1024)
    {
        var safeId = RequireTransactionId(transactionId);
        var root = Path.Combine(_transactionsRoot, safeId, "staging");
        if (Directory.Exists(root)) throw new InvalidOperationException("Artifact transaction staging already exists.");
        return new ArtifactTransformStagingArea(root, maximumFiles, maximumBytes);
    }

    public ArtifactReadSession CreateReadSession(
        ArtifactLedgerDocument ledger,
        IEnumerable<ArtifactId> artifactIds)
    {
        ArtifactLedgerValidator.Validate(ledger);
        var selectedIds = artifactIds.ToHashSet();
        var selected = ledger.Artifacts.Where(artifact => selectedIds.Contains(artifact.ArtifactId)).ToArray();
        if (selected.Length != selectedIds.Count) throw new KeyNotFoundException("Artifact read session contains an unknown ArtifactId.");
        var roots = new Dictionary<ArtifactContentHandle, string>();
        var snapshots = new Dictionary<ArtifactId, BackupArtifactSnapshot>();
        foreach (var artifact in selected)
        {
            var handle = new ArtifactContentHandle(Guid.NewGuid().ToString("N"));
            var path = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, artifact.ContentRelativePath);
            roots.Add(handle, path);
            snapshots.Add(artifact.ArtifactId, new BackupArtifactSnapshot(
                artifact.ArtifactId,
                artifact.Format,
                artifact.FormatVersion,
                artifact.RestoreStrategyId,
                handle,
                artifact.LogicalSha256,
                artifact.LogicalSize,
                artifact.Completeness,
                artifact.CoreCaptureMode,
                artifact.ConfigId,
                artifact.FolderId,
                artifact.HistoryItemId,
                artifact.Dependencies));
        }
        return new ArtifactReadSession(new HostArtifactReadService(roots), snapshots);
    }

    public async ValueTask VerifyArtifactsAsync(
        ArtifactLedgerDocument ledger,
        IEnumerable<ArtifactId> artifactIds,
        CancellationToken cancellationToken = default)
    {
        ArtifactLedgerValidator.Validate(ledger);
        var selectedIds = artifactIds.ToHashSet();
        var selected = ledger.Artifacts.Where(artifact => selectedIds.Contains(artifact.ArtifactId)).ToArray();
        if (selected.Length != selectedIds.Count) throw new KeyNotFoundException("Artifact verification contains an unknown ArtifactId.");
        foreach (var artifact in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, artifact.ContentRelativePath);
            var (sha256, size) = await HostArtifactReadService.ComputeLogicalFactsAsync(path, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(sha256, artifact.LogicalSha256)
                || size != artifact.LogicalSize)
            {
                throw new InvalidDataException($"Artifact '{artifact.ArtifactId}' failed logical integrity verification.");
            }
        }
    }

    public async ValueTask<ArtifactLedgerDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ArtifactLedgerDocument> RegisterCoreArtifactAsync(
        string configId,
        Guid folderId,
        string historyItemId,
        string contentRelativePath,
        ArtifactCompleteness completeness,
        CoreCaptureMode captureMode,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configId) || folderId == Guid.Empty || string.IsNullOrWhiteSpace(historyItemId))
        {
            throw new ArgumentException("Core Artifact ownership is incomplete.");
        }
        var safeId = RequireTransactionId(transactionId);
        var canonicalPath = ArtifactPathRules.NormalizeRelativePath(contentRelativePath);
        var contentPath = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, canonicalPath);
        var (sha256, size) = await HostArtifactReadService.ComputeLogicalFactsAsync(contentPath, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            if (current.HistoryRoots.Any(root => StringComparer.Ordinal.Equals(root.HistoryItemId, historyItemId)))
            {
                throw new InvalidOperationException("History Item already has an Artifact root.");
            }
            var artifactId = new ArtifactId(Guid.NewGuid());
            var entry = new ArtifactLedgerEntry(
                artifactId,
                new ArtifactFormatRef(new OwnerId("folderrewind.core"), "archive-set"),
                1,
                new RestoreStrategyId(new PluginId("folderrewind.core"), "archive-materializer"),
                configId,
                folderId,
                historyItemId,
                canonicalPath,
                sha256,
                size,
                sha256,
                size,
                completeness,
                captureMode,
                Array.Empty<ArtifactId>(),
                safeId,
                ArtifactAvailability.Available,
                ArtifactAvailability.Pending);
            var candidate = current with
            {
                Revision = NewRevision(),
                Artifacts = current.Artifacts.Append(entry).ToArray(),
                HistoryRoots = current.HistoryRoots.Append(
                    new ArtifactHistoryRoot(historyItemId, configId, folderId, artifactId)).ToArray()
            };
            ArtifactLedgerValidator.Validate(candidate);
            await WriteJsonAtomicallyAsync(_ledgerPath, candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CommitAsync(
        ArtifactLedgerDocument expected,
        ArtifactLedgerDocument candidate,
        string transactionId,
        ArtifactTransformStagingArea staging,
        IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> stagedFacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(stagedFacts);
        ArtifactLedgerValidator.Validate(expected);
        ArtifactLedgerValidator.Validate(candidate);
        var safeId = RequireTransactionId(transactionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision != expected.Revision)
            {
                throw new InvalidOperationException("Artifact graph changed before transaction commit.");
            }
            var oldIds = expected.Artifacts.Select(artifact => artifact.ArtifactId).ToHashSet();
            var added = candidate.Artifacts.Where(artifact => !oldIds.Contains(artifact.ArtifactId)).ToArray();
            ValidateStagedFacts(added, stagedFacts);
            var transactionRoot = Path.Combine(_transactionsRoot, safeId);
            Directory.CreateDirectory(transactionRoot);
            var journalPath = Path.Combine(transactionRoot, "journal.json");
            var journal = new ArtifactTransactionJournal(
                safeId,
                ArtifactTransactionStatus.Prepared,
                expected.Revision,
                candidate.Revision,
                added.Select(artifact => artifact.ArtifactId).ToArray());
            await WriteJsonDurablyAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            Observe(ArtifactTransactionStage.JournalPrepared);

            var installed = new List<string>();
            try
            {
                foreach (var artifact in added)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fact = stagedFacts.Values.Single(value => value.ArtifactId == artifact.ArtifactId);
                    var source = staging.GetAllocationPath(fact.Staging);
                    var destination = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, artifact.ContentRelativePath);
                    if (Directory.Exists(destination) || File.Exists(destination))
                    {
                        throw new IOException($"Artifact payload '{artifact.ArtifactId}' already exists.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    Directory.Move(source, destination);
                    installed.Add(destination);
                }
                Observe(ArtifactTransactionStage.PayloadsInstalled);

                Observe(ArtifactTransactionStage.BeforeMetadataSwitch);
                await WriteJsonAtomicallyAsync(_ledgerPath, candidate, cancellationToken).ConfigureAwait(false);
                Observe(ArtifactTransactionStage.MetadataSwitched);
                journal = journal with { Status = ArtifactTransactionStatus.Committed };
                await WriteJsonDurablyAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                Observe(ArtifactTransactionStage.Committed);
            }
            catch
            {
                var observed = await LoadWithoutLockAsync(CancellationToken.None).ConfigureAwait(false);
                if (observed.Revision != candidate.Revision)
                {
                    foreach (var path in installed)
                    {
                        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    }
                    journal = journal with { Status = ArtifactTransactionStatus.RolledBack };
                    await WriteJsonDurablyAsync(journalPath, journal, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledger = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            foreach (var journalPath in Directory.EnumerateFiles(_transactionsRoot, "journal.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var journal = await ReadJsonAsync<ArtifactTransactionJournal>(journalPath, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Artifact transaction journal is empty.");
                if (journal.Status != ArtifactTransactionStatus.Prepared) continue;
                if (ledger.Revision == journal.TargetRevision)
                {
                    await WriteJsonDurablyAsync(
                        journalPath,
                        journal with { Status = ArtifactTransactionStatus.Committed },
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var referenced = ledger.Artifacts.Select(artifact => artifact.ArtifactId).ToHashSet();
                foreach (var artifactId in journal.AddedArtifactIds.Where(id => !referenced.Contains(id)))
                {
                    var relative = $"artifacts/{artifactId.Value:N}";
                    var path = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, relative);
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
                var stagingRoot = Path.Combine(Path.GetDirectoryName(journalPath)!, "staging");
                if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
                await WriteJsonDurablyAsync(
                    journalPath,
                    journal with { Status = ArtifactTransactionStatus.RolledBack },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ArtifactId>> GarbageCollectUnreachableAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            var reachable = ArtifactLedgerValidator.ComputeReachable(current);
            var garbage = current.Artifacts.Where(artifact => !reachable.Contains(artifact.ArtifactId)).ToArray();
            if (garbage.Length == 0) return Array.Empty<ArtifactId>();
            var retained = current.Artifacts.Where(artifact => reachable.Contains(artifact.ArtifactId)).ToArray();
            var candidate = current with
            {
                Revision = NewRevision(),
                Artifacts = retained
            };
            ArtifactLedgerValidator.Validate(candidate);
            await WriteJsonAtomicallyAsync(_ledgerPath, candidate, cancellationToken).ConfigureAwait(false);
            foreach (var artifact in garbage)
            {
                var path = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, artifact.ContentRelativePath);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            return garbage.Select(artifact => artifact.ArtifactId).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ImportCloudClosureAsync(
        ArtifactLedgerDocument closure,
        IReadOnlyDictionary<ArtifactId, CloudArtifactPayload> payloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(payloads);
        ArtifactLedgerValidator.Validate(closure);
        var closureIds = closure.Artifacts.Select(value => value.ArtifactId).ToHashSet();
        if (!closureIds.SetEquals(payloads.Keys))
            throw new InvalidDataException("Cloud Artifact closure payloads do not match its ledger slice.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            var installed = new List<string>();
            try
            {
                foreach (var artifact in closure.Artifacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var existing = current.Artifacts.SingleOrDefault(value => value.ArtifactId == artifact.ArtifactId);
                    if (existing is not null && !ArtifactIdentityMatches(existing, artifact))
                        throw new InvalidDataException($"Cloud Artifact '{artifact.ArtifactId}' conflicts with local metadata.");
                    var destination = ArtifactPathRules.ResolveUnderRoot(_repositoryRoot, artifact.ContentRelativePath);
                    var payload = payloads[artifact.ArtifactId];
                    if (!File.Exists(destination) && !Directory.Exists(destination))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (payload.IsFile)
                            File.Copy(payload.Path, destination, overwrite: false);
                        else
                            CopyDirectory(payload.Path, destination, cancellationToken);
                        installed.Add(destination);
                    }
                    var facts = await HostArtifactReadService.ComputeLogicalFactsAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(facts.Sha256, artifact.LogicalSha256)
                        || facts.Size != artifact.LogicalSize)
                        throw new InvalidDataException($"Cloud Artifact '{artifact.ArtifactId}' failed logical integrity verification.");
                }

                var mergedArtifacts = current.Artifacts
                    .Concat(closure.Artifacts.Where(incoming => current.Artifacts.All(value => value.ArtifactId != incoming.ArtifactId)))
                    .ToArray();
                var mergedRoots = current.HistoryRoots.ToList();
                foreach (var root in closure.HistoryRoots)
                {
                    var existing = mergedRoots.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.HistoryItemId, root.HistoryItemId));
                    if (existing is not null && existing != root)
                        throw new InvalidDataException("Cloud Artifact History root conflicts with local metadata.");
                    if (existing is null) mergedRoots.Add(root);
                }
                var candidate = new ArtifactLedgerDocument(
                    1,
                    closure.Revision,
                    mergedArtifacts,
                    mergedRoots);
                ArtifactLedgerValidator.Validate(candidate);
                await WriteJsonAtomicallyAsync(_ledgerPath, candidate, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var path in installed)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    }
                    catch { }
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RemoveHistoryRootAsync(
        string historyItemId,
        ArtifactGraphRevision committedRevision,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadWithoutLockAsync(cancellationToken).ConfigureAwait(false);
            var roots = current.HistoryRoots.Where(root => !StringComparer.Ordinal.Equals(root.HistoryItemId, historyItemId)).ToArray();
            if (roots.Length == current.HistoryRoots.Count) throw new KeyNotFoundException("History root was not found.");
            var candidate = current with { Revision = committedRevision, HistoryRoots = roots };
            ArtifactLedgerValidator.Validate(candidate);
            await WriteJsonAtomicallyAsync(_ledgerPath, candidate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static void EnsureArtifactCanBePhysicallyDeleted(
        ArtifactLedgerDocument document,
        ArtifactId artifactId)
    {
        var dependentHistory = document.HistoryRoots
            .Where(root => ArtifactLedgerValidator.ComputeReachable(document, [root.HistoryItemId]).Contains(artifactId))
            .Select(root => root.HistoryItemId)
            .ToArray();
        if (dependentHistory.Length != 0)
        {
            throw new InvalidOperationException(
                $"Artifact '{artifactId}' is reachable from History: {string.Join(", ", dependentHistory)}.");
        }
    }

    private static bool ArtifactIdentityMatches(ArtifactLedgerEntry left, ArtifactLedgerEntry right)
        => left.ArtifactId == right.ArtifactId
           && left.Format == right.Format
           && left.FormatVersion == right.FormatVersion
           && left.RestoreStrategyId == right.RestoreStrategyId
           && StringComparer.Ordinal.Equals(left.ConfigId, right.ConfigId)
           && left.FolderId == right.FolderId
           && StringComparer.Ordinal.Equals(left.HistoryItemId, right.HistoryItemId)
           && StringComparer.Ordinal.Equals(left.ContentRelativePath, right.ContentRelativePath)
           && StringComparer.OrdinalIgnoreCase.Equals(left.LogicalSha256, right.LogicalSha256)
           && left.LogicalSize == right.LogicalSize
           && left.Completeness == right.Completeness
           && left.CoreCaptureMode == right.CoreCaptureMode
           && left.Dependencies.SequenceEqual(right.Dependencies);

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private async ValueTask<ArtifactLedgerDocument> LoadWithoutLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_ledgerPath))
        {
            return new ArtifactLedgerDocument(
                ArtifactLedgerValidator.CurrentSchemaVersion,
                new ArtifactGraphRevision("0"),
                Array.Empty<ArtifactLedgerEntry>(),
                Array.Empty<ArtifactHistoryRoot>());
        }
        var document = await ReadJsonAsync<ArtifactLedgerDocument>(_ledgerPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Artifact Ledger is empty.");
        ArtifactLedgerValidator.Validate(document);
        return document;
    }

    private void ValidateStagedFacts(
        IReadOnlyList<ArtifactLedgerEntry> added,
        IReadOnlyDictionary<ArtifactStagingHandle, StagedArtifactFacts> stagedFacts)
    {
        if (added.Count != stagedFacts.Count) throw new InvalidOperationException("Staged Artifact count does not match graph additions.");
        foreach (var artifact in added)
        {
            var fact = stagedFacts.Values.SingleOrDefault(value => value.ArtifactId == artifact.ArtifactId)
                ?? throw new InvalidOperationException("Graph addition has no staged payload.");
            if (!StringComparer.Ordinal.Equals(fact.ContentRelativePath, artifact.ContentRelativePath)
                || !StringComparer.OrdinalIgnoreCase.Equals(fact.LogicalSha256, artifact.LogicalSha256)
                || fact.LogicalSize != artifact.LogicalSize
                || !StringComparer.OrdinalIgnoreCase.Equals(fact.StorageSha256, artifact.StorageSha256)
                || fact.StorageSize != artifact.StorageSize)
            {
                throw new InvalidOperationException("Plugin-declared Artifact facts do not match Host staging facts.");
            }
        }
    }

    private async ValueTask<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await WriteJsonDurablyAsync(temporary, value, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path)) File.Move(temporary, path, overwrite: true);
        else File.Move(temporary, path);
        await using var verify = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var readBack = await JsonSerializer.DeserializeAsync<T>(verify, _json, cancellationToken).ConfigureAwait(false);
        if (readBack is null) throw new InvalidDataException("Atomic Artifact metadata read-back failed.");
    }

    private async ValueTask WriteJsonDurablyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string RequireTransactionId(string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId)
            || transactionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Transaction identity must use ASCII letters, digits, dash, or underscore.", nameof(transactionId));
        }
        return transactionId;
    }

    private static ArtifactGraphRevision NewRevision()
        => new(Guid.NewGuid().ToString("N"));

    private void Observe(ArtifactTransactionStage stage) => _observeStage?.Invoke(stage);
}

public sealed record ArtifactTransactionJournal(
    string TransactionId,
    ArtifactTransactionStatus Status,
    ArtifactGraphRevision ExpectedRevision,
    ArtifactGraphRevision TargetRevision,
    IReadOnlyList<ArtifactId> AddedArtifactIds);

public enum ArtifactTransactionStatus
{
    Prepared = 0,
    Committed = 1,
    RolledBack = 2
}

public enum ArtifactTransactionStage
{
    JournalPrepared = 0,
    PayloadsInstalled = 1,
    BeforeMetadataSwitch = 2,
    MetadataSwitched = 3,
    Committed = 4
}

public sealed record ArtifactReadSession(
    IArtifactReadService ReadService,
    IReadOnlyDictionary<ArtifactId, BackupArtifactSnapshot> Snapshots);
