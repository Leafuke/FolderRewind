using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

internal enum HistoryRestoreTransactionPhase
{
    Prepared = 0,
    Mutating = 1,
    WorkspaceApplying = 2,
    WorkspaceApplied = 3,
    Complete = 4,
    Preparing = 5,
    Cleaning = 6
}

internal sealed record RestoreWorkspaceRegistration(HistoryRestoreWorkspaceLocation Location, string OwnerNonce,
    RestoreObjectIdentity? ContainerIdentity, ImmutableArray<RestorePlanObject> KnownObjects, bool CleanupStarted = false);

internal sealed record HistoryRestoreTransactionJournal(
    HistoryTransactionId TransactionId,
    HistoryRestoreTransactionPhase Phase,
    HistoryWorkspace ExpectedWorkspace,
    HistoryWorkspace DesiredWorkspace,
    ImmutableArray<string> StagingDirectories,
    ImmutableArray<HistoryRestoreRollbackSnapshot> RollbackSnapshots,
    ImmutableArray<SourceId> AppliedSources,
    ImmutableArray<SourceId> StartedSources = default,
    byte[]? IntendedPackBytes = null,
    LocalReplicaCatalog? DesiredCatalog = null,
    long ExpectedCatalogRevision = -1,
    int ProtocolVersion = 1,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<RestoreWorkspaceRegistration> Workspaces = default,
    RestorePlanProof? FileSystemPlan = null,
    bool? CleanupCommitted = null,
    string? ConfigurationSignature = null);

internal sealed partial class HistoryRestoreTransactionJournalStore
{
    private const string FileName = "restore-journal.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HistoryRuntime _runtime;
    private readonly IHistoryRestoreMutationBackend _backend;

    public HistoryRestoreTransactionJournalStore(
        HistoryRuntime runtime,
        IHistoryRestoreMutationBackend backend)
    {
        _runtime = runtime;
        _backend = backend;
    }

    internal static void RequireRecovered(string root)
    {
        if (!Directory.Exists(root)) return;
        RequireSafePreparationRecords(root);
        foreach (var path in Directory.EnumerateFiles(root, FileName, SearchOption.AllDirectories))
        {
            var journal = JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(path), JsonOptions);
            if (journal is not null && journal.ProtocolVersion is not (1 or 2)) throw new InvalidDataException("Unsupported restore journal protocol; preserve transaction data.");
            if (journal is not null && !Enum.IsDefined(journal.Phase)) throw new InvalidDataException("Unknown restore journal phase; preserve transaction data.");
            if (journal is not null && journal.ProtocolVersion == 1 && journal.Phase > HistoryRestoreTransactionPhase.Complete)
                throw new InvalidDataException("Invalid legacy restore journal phase.");
            if (journal is not null && HistoryRestorePreparationSession.OwnsPreparing(journal, root)) continue;
            if (journal is null || journal.Phase != HistoryRestoreTransactionPhase.Complete)
                throw new InvalidOperationException($"Transaction {journal?.TransactionId} phase {journal?.Phase} ({path}) requires recovery before further mutations or GC.");
            ValidateJournalEnvelope(journal, path);
        }
    }

    private static void ValidateJournalEnvelope(HistoryRestoreTransactionJournal? journal, string path)
    {
        if (journal is null || journal.ExpectedWorkspace is null || journal.DesiredWorkspace is null
            || string.IsNullOrWhiteSpace(journal.ExpectedWorkspace.ConfigId.Value)
            || journal.ExpectedWorkspace.ConfigId != journal.DesiredWorkspace.ConfigId
            || !Path.GetFileName(Path.GetDirectoryName(path)!).Equals(journal.TransactionId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore journal identity or state evidence is invalid; preserve transaction data.");
    }

    public void Save(HistoryRestoreTransactionJournal journal)
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("journal-save-flush");
        var path = GetPath(journal.TransactionId);
        AtomicFileService.Write(
            path,
            stream =>
            {
                JsonSerializer.Serialize(stream, journal, JsonOptions);
                HistoryOperationDiagnostics.Current?.Count("journal-bytes", stream.Position);
            });
        HistoryOperationDiagnostics.Current?.Count("journal-flushes");
    }

    public async Task<bool> RecoverIncompleteAsync(CancellationToken cancellationToken)
    {
        var root = _runtime.Repository.Paths.TransactionsRoot;
        if (!Directory.Exists(root)) return false;
        RecoverPreparationRecords();
        bool recovered = false;
        foreach (var path in Directory.GetFiles(root, FileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journal = JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                JsonOptions) ?? throw new InvalidDataException($"Restore journal '{path}' is empty.");
            ValidateJournalEnvelope(journal, path);
            if (journal.ExpectedWorkspace.ConfigId != _runtime.ConfigId) throw new InvalidDataException("Restore journal belongs to another configuration.");
            if (journal.ProtocolVersion is not (1 or 2)) throw new InvalidDataException("Unsupported restore journal protocol; preserve transaction data.");
            if (!Enum.IsDefined(journal.Phase)) throw new InvalidDataException("Unknown restore journal phase; preserve transaction data.");
            if (journal.ProtocolVersion == 1 && journal.Phase > HistoryRestoreTransactionPhase.Complete) throw new InvalidDataException("Invalid legacy restore journal phase.");
            if (HistoryRestorePreparationSession.OwnsPreparing(journal, root)) continue;
            if (journal.Phase == HistoryRestoreTransactionPhase.Complete) continue;
            if (journal.ProtocolVersion == 2)
            {
                await RecoverV2Async(journal, cancellationToken).ConfigureAwait(false);
                recovered = true;
                continue;
            }
            if (journal.StartedSources.IsDefault && journal.Phase == HistoryRestoreTransactionPhase.Mutating && !journal.RollbackSnapshots.IsEmpty)
                throw new InvalidDataException("Restore journal lacks source progress; explicit recovery is required.");

            var workspaceLoad = await _runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            bool workspaceCommitted = journal.Phase >= HistoryRestoreTransactionPhase.WorkspaceApplying
                && workspaceLoad.Value is not null
                && WorkspaceEquals(workspaceLoad.Value, journal.DesiredWorkspace);
            if (journal.IntendedPackBytes is not null)
            {
                workspaceCommitted = IsPackCommitted(journal);
                if (workspaceCommitted) await CompleteLocalStateAsync(journal, cancellationToken).ConfigureAwait(false);
            }
            if (workspaceCommitted)
            {
                foreach (var snapshot in journal.RollbackSnapshots)
                    await _backend.CommitAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (workspaceLoad.Value is null || !WorkspaceEquals(workspaceLoad.Value, journal.ExpectedWorkspace))
                    throw new InvalidDataException("Uncommitted restore Workspace changed; preserving legacy transaction data.");
                foreach (var snapshot in journal.RollbackSnapshots.Where(s => !journal.StartedSources.IsDefault
                    && journal.StartedSources.Contains(s.SourceId)).Reverse())
                    await _backend.RollbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            CleanupStaging(journal.StagingDirectories);
            Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
            recovered = true;
        }
        if (Directory.Exists(_runtime.MergeSessions.Root))
            foreach (var session in _runtime.MergeSessions.List().Where(s => s.State == MergeSessionState.Applying))
            {
                var journalPath = GetPath(session.ApplyTransactionId!.Value);
                if (File.Exists(journalPath))
                {
                    var intent = JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(journalPath), JsonOptions)
                        ?? throw new InvalidDataException("Merge journal is empty.");
                    if (intent.Phase != HistoryRestoreTransactionPhase.Complete) throw new InvalidDataException("Merge recovery is incomplete.");
                    _runtime.MergeSessions.Update(session, IsPackCommitted(intent) ? MergeSessionState.Committed : MergeSessionState.Ready);
                }
                else
                {
                    if (File.Exists(_runtime.Repository.Paths.GetPackPath(session.IntendedPackId!.Value)))
                        throw new InvalidDataException("Committed Merge is missing its recovery journal.");
                    _runtime.MergeSessions.Update(session, MergeSessionState.Ready);
                }
            }
        if (Directory.EnumerateFiles(root, "restore-cleanup.json", SearchOption.AllDirectories).Any()) _runtime.ScheduleRestoreCleanup();
        return recovered;
    }

    public bool IsPackCommitted(HistoryRestoreTransactionJournal journal)
    {
        if (journal.IntendedPackBytes is null) return false;
        var codec = new HistoryPackCodec(); var expected = codec.Decode(journal.IntendedPackBytes).Pack;
        var path = _runtime.Repository.Paths.GetPackPath(expected.PackId);
        if (!File.Exists(path)) return false;
        var bytes = File.ReadAllBytes(path); _ = codec.Decode(bytes);
        if (!bytes.AsSpan().SequenceEqual(journal.IntendedPackBytes)) throw new InvalidDataException("Durable Merge pack differs from transaction intent.");
        return true;
    }

    internal string TransactionDirectory(HistoryTransactionId id) => Path.GetDirectoryName(GetPath(id))!;

    private async Task RecoverV2Async(HistoryRestoreTransactionJournal journal, CancellationToken token)
    {
        if (journal.FileSystemPlan is null)
        {
            AbandonPreparation(journal);
            return;
        }
        var actual = (await _runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value
            ?? throw new InvalidDataException("Restore v2 requires a readable Workspace.");
        if (journal.Workspaces.IsDefaultOrEmpty) throw new InvalidDataException("Restore v2 workspace ownership is absent.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(journal.FileSystemPlan.Path),
                Path.Combine(TransactionDirectory(journal.TransactionId), "restore-plan.json")))
            throw new InvalidDataException("Restore immutable plan location is invalid.");
        var plan = HistoryRestoreFileSystemPlanStore.Load(journal.FileSystemPlan, journal.TransactionId);
        if (plan.Sources is not null && (plan.Sources.Count != journal.Workspaces.Length
            || plan.Sources.Select(source => source.Source).Distinct().Count() != plan.Sources.Count
            || plan.Sources.Any(source => !journal.Workspaces.Any(workspace => source.Source == workspace.Location.SourceId
                && Path.GetFullPath(source.Target).Equals(workspace.Location.Target, StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidDataException("Restore source evidence disagrees with workspace bindings.");
        if (plan.Actions.Any(action => !journal.Workspaces.Any(workspace => workspace.Location.SourceId == action.Source
            && IsWithin(action.From, workspace.Location) && IsWithin(action.To, workspace.Location))))
            throw new InvalidDataException("Restore action escapes its registered Source locations.");
        RestoreActionProgress.Validate(Path.Combine(TransactionDirectory(journal.TransactionId), "restore-progress.bin"), plan);
        var committed = journal.IntendedPackBytes is not null ? IsPackCommitted(journal) : WorkspaceEquals(actual, journal.DesiredWorkspace);
        if (!committed && !WorkspaceEquals(actual, journal.ExpectedWorkspace))
            throw new InvalidDataException("Workspace is neither expected nor desired; preserving restore v2 data.");
        if (journal.Phase == HistoryRestoreTransactionPhase.Cleaning && journal.CleanupCommitted != committed)
            throw new InvalidDataException("Restore cleanup decision conflicts with durable state.");
        if (committed)
        {
            HistoryRestoreFileSystemPlanStore.ValidatePublished(plan);
            if (journal.IntendedPackBytes is not null) await CompleteLocalStateAsync(journal, CancellationToken.None).ConfigureAwait(false);
        }
        else if (journal.Phase != HistoryRestoreTransactionPhase.Cleaning)
        {
            HistoryRestoreFileSystemPlanStore.Rollback(plan);
            HistoryRestoreFileSystemPlanStore.ValidateFinal(plan, false);
        }
        else HistoryRestoreFileSystemPlanStore.ValidateOutsideContainers(plan, false);
        CompleteWithDeferredCleanup(journal, committed);
    }

    // This record covers only abandoned preparation containers, never original rollback objects.
    private sealed record PreparationCleanup(int FormatVersion, HistoryTransactionId TransactionId,
        ImmutableArray<RestoreWorkspaceRegistration> Workspaces);

    internal void AbandonPreparation(HistoryRestoreTransactionJournal journal)
    {
        if (journal.ProtocolVersion != 2 || journal.Phase != HistoryRestoreTransactionPhase.Preparing
            || journal.FileSystemPlan is not null || journal.Workspaces.IsDefaultOrEmpty
            || journal.ExpectedWorkspace.ConfigId != _runtime.ConfigId
            || journal.DesiredWorkspace.ConfigId != _runtime.ConfigId
            || !journal.RollbackSnapshots.IsDefaultOrEmpty || !journal.AppliedSources.IsDefaultOrEmpty
            || !journal.StartedSources.IsDefaultOrEmpty || journal.IntendedPackBytes is not null || journal.CleanupCommitted is not null)
            throw new InvalidDataException("Preparation has conflicting execution evidence; preserve transaction data.");
        if (journal.Workspaces.Select(w => w.Location.SourceId).Distinct().Count() != journal.Workspaces.Length)
            throw new InvalidDataException("Preparing Source registrations are ambiguous.");
        var directory = TransactionDirectory(journal.TransactionId);
        if (File.Exists(Path.Combine(directory, "restore-progress.bin")))
            throw new InvalidDataException("Preparing transaction contains execution progress; preserve transaction data.");
        foreach (var registration in journal.Workspaces)
        {
            ValidatePreparationLocation(journal.TransactionId, registration);
            if (WindowsRestoreFileSystem.TryIdentity(registration.Location.Target) != registration.Location.TargetIdentity)
                throw new InvalidDataException("Preparing target identity changed; preserve transaction data.");
            // Unknown containers are preserved as maintenance; do not traverse them to inspect evidence.
            bool owned;
            try { owned = registration.ContainerIdentity is not null && WindowsRestoreFileSystem.TryIdentity(registration.Location.Container) == registration.ContainerIdentity; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { owned = false; }
            if (owned)
            {
                using var ancestors = new WindowsDirectoryReadLease(Path.Combine(registration.Location.Container, "unused-child"));
                foreach (var name in new[] { "rollback", "rollback-items" })
                {
                    var rollback = Path.Combine(registration.Location.Container, name);
                    if (Path.Exists(rollback) && (name == "rollback" || (File.GetAttributes(rollback) & FileAttributes.ReparsePoint) != 0
                        || Directory.Exists(rollback) && Directory.EnumerateFileSystemEntries(rollback).Any()))
                        throw new InvalidDataException("Preparing container contains rollback evidence; preserve transaction data.");
                }
            }
        }
        var orphanPlan = Path.Combine(directory, "restore-plan.json");
        if (File.Exists(orphanPlan))
        {
            var bytes = File.ReadAllBytes(orphanPlan);
            var plan = HistoryRestoreFileSystemPlanStore.Load(new(orphanPlan, bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), journal.TransactionId);
            if (plan.Actions.Any(a => !journal.Workspaces.Any(w => w.Location.SourceId == a.Source && IsWithin(a.From, w.Location) && IsWithin(a.To, w.Location))))
                throw new InvalidDataException("Unpublished preparation plan escapes registered locations.");
            HistoryRestoreFileSystemPlanStore.ValidateFinal(plan, false);
        }
        var record = new PreparationCleanup(1, journal.TransactionId, journal.Workspaces);
        // Persist the handoff before completing the blocking transaction. A crash retries either side.
        AtomicFileService.Write(Path.Combine(directory, "restore-cleanup.json"), stream => JsonSerializer.Serialize(stream, record, JsonOptions));
        Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
        _runtime.ScheduleRestoreCleanup();
    }

    private void ValidatePreparationLocation(HistoryTransactionId transaction, RestoreWorkspaceRegistration registration)
    {
        var location = registration.Location;
        var target = Path.GetFullPath(location.Target);
        var container = Path.GetFullPath(location.Container);
        var repository = HistoryRestoreWorkspacePlanner.ResolvePath(_runtime.Repository.Paths.RepositoryRoot);
        var expected = Path.Combine(Path.GetDirectoryName(target)!, $".folderrewind-restore-{transaction}-{location.SourceId}");
        if (!Path.IsPathFullyQualified(location.Target) || !Path.IsPathFullyQualified(location.Container)
            || !StringComparer.OrdinalIgnoreCase.Equals(container, expected)
            || HistoryRestoreWorkspacePlanner.Overlaps(container, repository)
            || HistoryRestoreWorkspacePlanner.Overlaps(target, repository))
            throw new InvalidDataException("Preparation container binding is invalid.");
    }

    private static bool IsWithin(string path, HistoryRestoreWorkspaceLocation location)
        => new[] { location.Target, location.Container }.Any(root => Path.GetFullPath(path).Equals(root, StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    public async Task CompleteLocalStateAsync(HistoryRestoreTransactionJournal journal, CancellationToken token)
    {
        if (journal.DesiredCatalog is { } desired)
        {
            var catalog = (await _runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
            if (catalog is null || JsonSerializer.Serialize(catalog, JsonOptions) != JsonSerializer.Serialize(desired, JsonOptions))
                await _runtime.LocalReplicaCatalogStore.SaveAsync(desired, journal.ExpectedCatalogRevision, token).ConfigureAwait(false);
        }
        var workspace = (await _runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (workspace is null || !WorkspaceEquals(workspace, journal.DesiredWorkspace))
            await _runtime.WorkspaceStore.SaveAsync(journal.DesiredWorkspace, journal.ExpectedWorkspace.StateRevision, token).ConfigureAwait(false);
    }

    public static bool WorkspaceEquals(HistoryWorkspace left, HistoryWorkspace right) => HistoryWorkspace.StateEquals(left, right);

    public static void CleanupStaging(IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            if (HistoryRestorePreparationSession.IsActiveStaging(directory)) continue;
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch { }
        }
    }

    private string GetPath(HistoryTransactionId transactionId)
    {
        var directory = _runtime.Repository.Paths.GetTransactionDirectory(transactionId);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, FileName);
    }
}
