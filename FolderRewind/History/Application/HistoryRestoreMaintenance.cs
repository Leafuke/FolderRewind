using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace FolderRewind.History.Application;

internal sealed record HistoryRestorePreparationRecord(int FormatVersion, HistoryConfigId ConfigId,
    HistoryTransactionId TransactionId, ImmutableArray<RestoreWorkspaceRegistration> Workspaces, string? ConfigurationSignature);
internal enum RestoreCleanupKind { AbandonedPreparation, Committed, RolledBack }
internal sealed record HistoryRestoreCleanupRecord(int FormatVersion, RestoreCleanupKind Kind, HistoryConfigId ConfigId,
    HistoryTransactionId TransactionId, ImmutableArray<RestoreWorkspaceRegistration> Workspaces,
    RestorePlanProof? Plan = null, string? DurableOutcomeProof = null);

internal sealed partial class HistoryRestoreTransactionJournalStore
{
    internal string PreparationPath(HistoryTransactionId transaction) => Path.Combine(TransactionDirectory(transaction), "restore-preparation.json");
    private string CleanupPath(HistoryTransactionId transaction) => Path.Combine(TransactionDirectory(transaction), "restore-cleanup.json");
    internal void SavePreparation(HistoryRestorePreparationRecord record)
    {
        ValidatePreparationRecord(record, PreparationPath(record.TransactionId));
        AtomicFileService.Write(PreparationPath(record.TransactionId), stream => JsonSerializer.Serialize(stream, record, JsonOptions));
        HistoryOperationDiagnostics.Current?.Count("preparation-flushes");
    }
    private void ValidatePreparationRecord(HistoryRestorePreparationRecord record, string path)
    {
        if (record.FormatVersion != 1 || record.ConfigId != _runtime.ConfigId || record.Workspaces.IsDefaultOrEmpty
            || record.Workspaces.Any(w => w is null || w.Location is null)
            || record.Workspaces.Select(w => w.Location.SourceId).Distinct().Count() != record.Workspaces.Length
            || !Path.GetFullPath(path).Equals(PreparationPath(record.TransactionId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore preparation record is incompatible; preserve data.");
        foreach (var workspace in record.Workspaces) ValidatePreparationLocation(record.TransactionId, workspace);
    }
    internal static void RequireSafePreparationRecords(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "restore-preparation.json", SearchOption.AllDirectories))
        {
            var record = JsonSerializer.Deserialize<HistoryRestorePreparationRecord>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Restore preparation record is empty.");
            if (record.FormatVersion != 1 || record.Workspaces.IsDefaultOrEmpty
                || record.Workspaces.Any(w => w is null || w.Location is null
                    || !Path.IsPathFullyQualified(w.Location.Container) || !Path.IsPathFullyQualified(w.Location.Target))
                || !Path.GetFileName(Path.GetDirectoryName(path)!).Equals(record.TransactionId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unknown restore preparation record; preserve data.");
            if (HistoryRestorePreparationSession.OwnsPreparation(record.TransactionId, root)) continue;
            var directory = Path.GetDirectoryName(path)!;
            if (!File.Exists(Path.Combine(directory, FileName))
                && (File.Exists(Path.Combine(directory, "restore-progress.bin")) || File.Exists(Path.Combine(directory, "restore-plan.json"))))
                throw new InvalidDataException("Orphan restore execution evidence requires recovery: " + path);
            if (!File.Exists(Path.Combine(directory, FileName))) RequireNoPreparationRollbackMaterial(record.Workspaces);
        }
    }
    private void RecoverPreparationRecords()
    {
        var root = _runtime.Repository.Paths.TransactionsRoot;
        foreach (var path in Directory.GetFiles(root, "restore-preparation.json", SearchOption.AllDirectories))
        {
            HistoryRestorePreparationRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize<HistoryRestorePreparationRecord>(File.ReadAllBytes(path), JsonOptions)
                    ?? throw new InvalidDataException("Restore preparation record is empty.");
                ValidatePreparationRecord(record, path);
                if (HistoryRestorePreparationSession.OwnsPreparation(record.TransactionId, root)) continue;
                if (File.Exists(GetPath(record.TransactionId))) continue;
                AbandonPreparationRecord(record);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                HistoryRestoreRecoveryDiagnostics.Report(_runtime.ConfigId, record?.TransactionId, "Preparing", false, null, error, "RecoveryBlocked");
                throw;
            }
        }
    }
    internal void AbandonPreparationRecord(HistoryRestorePreparationRecord record)
    {
        ValidatePreparationRecord(record, PreparationPath(record.TransactionId));
        if (File.Exists(GetPath(record.TransactionId)))
            throw HistoryRestoreRecoveryDiagnostics.Mark(new InvalidDataException("Published restore journal must be recovered before abandoning preparation."),
                RestoreRecoveryFailureKind.ConflictingExecutionEvidence);
        ValidateAbandonedPreparation(record.TransactionId, record.Workspaces);
        WriteCleanup(new(2, RestoreCleanupKind.AbandonedPreparation, record.ConfigId, record.TransactionId, record.Workspaces));
        HistoryRestoreFileSystemPlanStore.Reach("after-preparation-cleanup-record");
        File.Delete(PreparationPath(record.TransactionId));
        HistoryRestoreFileSystemPlanStore.Reach("after-preparation-handoff");
        _runtime.ScheduleRestoreCleanup();
    }

    private void ValidateAbandonedPreparation(HistoryTransactionId transaction, ImmutableArray<RestoreWorkspaceRegistration> workspaces)
    {
        if (workspaces.IsDefaultOrEmpty || workspaces.Any(w => w is null || w.Location is null)
            || workspaces.Select(w => w.Location.SourceId).Distinct().Count() != workspaces.Length)
            throw new InvalidDataException("Preparing Source registrations are ambiguous.");
        foreach (var workspace in workspaces) ValidatePreparationLocation(transaction, workspace);
        var directory = TransactionDirectory(transaction);
        if (File.Exists(Path.Combine(directory, "restore-progress.bin")))
            throw HistoryRestoreRecoveryDiagnostics.Mark(new InvalidDataException("Preparation contains execution progress; preserve data."),
                RestoreRecoveryFailureKind.ConflictingExecutionEvidence);
        RequireNoPreparationRollbackMaterial(workspaces);
        var orphan = Path.Combine(directory, "restore-plan.json");
        if (File.Exists(orphan))
        {
            var bytes = File.ReadAllBytes(orphan);
            var plan = HistoryRestoreFileSystemPlanStore.Load(new(orphan, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))), transaction);
            ValidatePlanLocations(plan, workspaces);
            HistoryRestoreFileSystemPlanStore.ValidateFinal(plan, false);
        }
    }
    private static void RequireNoPreparationRollbackMaterial(ImmutableArray<RestoreWorkspaceRegistration> workspaces)
    {
        foreach (var registration in workspaces)
        {
            if (registration.ContainerIdentity is null) continue;
            RestoreObjectIdentity? actual;
            try { actual = WindowsRestoreFileSystem.TryIdentity(registration.Location.Container); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (actual != registration.ContainerIdentity) continue;
            try
            {
                using var held = new WindowsDirectoryReadLease(Path.Combine(registration.Location.Container, "unused-child"));
                if ((File.GetAttributes(registration.Location.Container) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var name in new[] { "rollback", "rollback-items" })
                {
                    var rollback = Path.Combine(registration.Location.Container, name);
                    if (Path.Exists(rollback) && (name == "rollback" || (File.GetAttributes(rollback) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory
                        || Directory.Exists(rollback) && Directory.EnumerateFileSystemEntries(rollback).Any()))
                        throw HistoryRestoreRecoveryDiagnostics.Mark(new InvalidDataException("Preparation contains original rollback material; preserve data."),
                            RestoreRecoveryFailureKind.ConflictingExecutionEvidence, registration.Location.Container, name);
                }
            }
            // 没有执行日志/进度的有效准备记录可交接维护；访问失败仍会阻止清理，不会采用陌生材料。
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Preparing containers are disposable namespaces, not a per-file ownership ledger.
    // Check the namespace proof again at maintenance time; a marker alone is not sufficient.
    private static bool RequirePreparationOwnership(RestoreWorkspaceRegistration registration)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore ownership requires Windows.");
        var container = registration.Location.Container;
        try
        {
            using var held = new WindowsDirectoryReadLease(Path.Combine(container, "unused-child"));
            if (registration.ContainerIdentity is null || WindowsRestoreFileSystem.Identity(container) != registration.ContainerIdentity
                || (File.GetAttributes(container) & FileAttributes.ReparsePoint) != 0)
                throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Preparation container ownership is unknown; preserve container."),
                    RestoreRecoveryFailureKind.OwnershipUnproven, container, ".");
            using var parent = new WindowsDirectoryReadLease(container);
            if (WindowsRestoreFileSystem.Identity(parent.PhysicalDirectory) != registration.Location.ParentIdentity)
                throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Preparation physical parent changed; preserve container."),
                    RestoreRecoveryFailureKind.IdentityMismatch, container, "..");

            var security = new DirectoryInfo(container).GetAccessControl(AccessControlSections.Access);
            using var current = WindowsIdentity.GetCurrent();
            var user = current.User ?? throw new IOException("Cannot identify preparation owner.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            bool userFullControl = false, systemFullControl = false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var principal = (SecurityIdentifier)rule.IdentityReference;
                if (!principal.Equals(user) && !principal.Equals(system))
                    throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Preparation permissions are no longer private; preserve container."),
                        RestoreRecoveryFailureKind.OwnershipUnproven, container, ".");
                if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                {
                    userFullControl |= principal.Equals(user);
                    systemFullControl |= principal.Equals(system);
                }
            }
            if (!security.AreAccessRulesProtected || !userFullControl || !systemFullControl)
                throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Preparation permissions are no longer private; preserve container."),
                    RestoreRecoveryFailureKind.OwnershipUnproven, container, ".");
            var markerPath = Path.Combine(container, "owner.json");
            if (!File.Exists(markerPath) && !Directory.EnumerateFileSystemEntries(container).Any()) return false;
            using var marker = WindowsDirectoryReadLease.OpenPayload(markerPath);
            using var reader = new StreamReader(marker, System.Text.Encoding.UTF8, true, 128, leaveOpen: true);
            if (string.IsNullOrWhiteSpace(registration.OwnerNonce) || marker.Length > 128 || reader.ReadToEnd() != registration.OwnerNonce)
                throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Preparation owner marker changed; preserve container."),
                    RestoreRecoveryFailureKind.OwnershipUnproven, container, "owner.json");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw HistoryRestoreRecoveryDiagnostics.Mark(error,
                error is FileNotFoundException or DirectoryNotFoundException ? RestoreRecoveryFailureKind.OwnershipUnproven
                    : HistoryRestoreRecoveryDiagnostics.Reason(error), container);
        }
    }
    private static void ValidatePlanLocations(HistoryRestoreFileSystemPlan plan, ImmutableArray<RestoreWorkspaceRegistration> workspaces)
    {
        if (!plan.Containers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(workspaces.Select(w => w.Location.Container))
            || plan.Actions.Any(a => !workspaces.Any(w => w.Location.SourceId == a.Source && IsWithin(a.From, w.Location) && IsWithin(a.To, w.Location))))
            throw new InvalidDataException("Restore plan escapes registered containers or Sources.");
    }
    private static string OutcomeProof(HistoryRestoreTransactionJournal journal, bool committed)
        => Convert.ToHexString(SHA256.HashData(committed && journal.IntendedPackBytes is not null ? journal.IntendedPackBytes
            : JsonSerializer.SerializeToUtf8Bytes(committed ? journal.DesiredWorkspace : journal.ExpectedWorkspace, JsonOptions)));
    private void WriteCleanup(HistoryRestoreCleanupRecord record)
        => AtomicFileService.Write(CleanupPath(record.TransactionId), stream => JsonSerializer.Serialize(stream, record, JsonOptions));

    // 调用方必须已经验证持久状态与目标位置；清理记录不是自行决定提交方向的捷径。
    internal void CompleteWithDeferredCleanup(HistoryRestoreTransactionJournal journal, bool committed)
    {
        if (journal.FileSystemPlan is null) throw new InvalidDataException("Execution cleanup requires an immutable plan.");
        WriteCleanup(new(2, committed ? RestoreCleanupKind.Committed : RestoreCleanupKind.RolledBack, _runtime.ConfigId,
            journal.TransactionId, journal.Workspaces, journal.FileSystemPlan, OutcomeProof(journal, committed)));
        HistoryRestoreFileSystemPlanStore.Reach("after-cleanup-record-before-complete");
        Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete, CleanupCommitted = committed });
        HistoryRestoreFileSystemPlanStore.Reach("after-cleanup-handoff");
        _runtime.ScheduleRestoreCleanup();
    }
    internal void RetryDeferredCleanup()
    {
        var failures = new List<string>();
        var root = _runtime.Repository.Paths.TransactionsRoot;
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.GetFiles(root, "restore-cleanup.json", SearchOption.AllDirectories))
        {
            HistoryRestoreCleanupRecord? record = null;
            bool? committedOutcome = null;
            try
            {
                using var diagnostic = HistoryOperationDiagnostics.Begin("restore-cleanup");
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                int version = json.RootElement.GetProperty("formatVersion").GetInt32();
                if (version == 1)
                {
                    var legacy = json.RootElement.Deserialize<PreparationCleanup>(JsonOptions) ?? throw new InvalidDataException("Empty legacy cleanup.");
                    record = new(2, RestoreCleanupKind.AbandonedPreparation, _runtime.ConfigId, legacy.TransactionId, legacy.Workspaces);
                }
                else if (version == 2 && json.RootElement.TryGetProperty("kind", out _))
                    record = json.RootElement.Deserialize<HistoryRestoreCleanupRecord>(JsonOptions) ?? throw new InvalidDataException("Empty cleanup.");
                else throw new InvalidDataException("Unknown cleanup format; preserve data.");
                if (record.ConfigId != _runtime.ConfigId || !Enum.IsDefined(record.Kind) || record.Workspaces.IsDefaultOrEmpty
                    || record.Workspaces.Any(w => w is null || w.Location is null)
                    || record.Workspaces.Select(w => w.Location.SourceId).Distinct().Count() != record.Workspaces.Length
                    || !Path.GetFullPath(path).Equals(CleanupPath(record.TransactionId), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Cleanup identity is invalid.");
                diagnostic.Observe("transaction", record.TransactionId.ToString());
                var journalPath = GetPath(record.TransactionId);
                var journal = File.Exists(journalPath)
                    ? JsonSerializer.Deserialize<HistoryRestoreTransactionJournal>(File.ReadAllBytes(journalPath), JsonOptions)
                        ?? throw new InvalidDataException("Cleanup journal is empty; preserve transaction data.")
                    : null;
                if (journal is not null)
                {
                    ValidateJournalEnvelope(journal, journalPath);
                    if (journal.ExpectedWorkspace.ConfigId != _runtime.ConfigId)
                        throw new InvalidDataException("Cleanup journal belongs to another configuration.");
                }
                if (journal is not null && journal.Phase != HistoryRestoreTransactionPhase.Complete)
                    throw new InvalidDataException("Cleanup has not completed its journal handoff.");
                foreach (var registration in record.Workspaces)
                {
                    ValidatePreparationLocation(record.TransactionId, registration);
                    using var parents = new WindowsDirectoryReadLease(registration.Location.Container);
                    if (WindowsRestoreFileSystem.Identity(parents.PhysicalDirectory) != registration.Location.ParentIdentity)
                        throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Cleanup physical parent changed; preserve container."),
                            RestoreRecoveryFailureKind.IdentityMismatch, registration.Location.Container, "..");
                    var actual = WindowsRestoreFileSystem.TryIdentity(registration.Location.Container);
                    if (actual is not null && (registration.ContainerIdentity is null || actual != registration.ContainerIdentity))
                        throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Cleanup container ownership is unknown; preserve container."),
                            registration.ContainerIdentity is null ? RestoreRecoveryFailureKind.OwnershipUnproven : RestoreRecoveryFailureKind.IdentityMismatch,
                            registration.Location.Container, ".");
                }
                if (record.Kind == RestoreCleanupKind.AbandonedPreparation)
                {
                    if (record.Plan is not null || record.DurableOutcomeProof is not null || journal?.FileSystemPlan is not null
                        || journal is not null && (journal.ProtocolVersion != 2 || journal.IntendedPackBytes is not null
                            || !journal.AppliedSources.IsDefaultOrEmpty || !journal.StartedSources.IsDefaultOrEmpty || !journal.RollbackSnapshots.IsDefaultOrEmpty)
                        || File.Exists(Path.Combine(TransactionDirectory(record.TransactionId), "restore-progress.bin")))
                        throw new InvalidDataException("Preparation cleanup conflicts with execution evidence.");
                    RequireNoPreparationRollbackMaterial(record.Workspaces);
                    committedOutcome = false;
                    foreach (var registration in record.Workspaces)
                    {
                        if (!Path.Exists(registration.Location.Container)) continue;
                        bool hasMarker = RequirePreparationOwnership(registration);
                        var objects = HistoryRestoreFileSystemPlanStore.CaptureTree(registration.Location.Container);
                        if (objects[0].Identity != registration.ContainerIdentity || !hasMarker && objects.Count != 1)
                            throw HistoryRestoreRecoveryDiagnostics.Mark(new IOException("Preparation ownership changed during cleanup."),
                                RestoreRecoveryFailureKind.OwnershipUnproven, registration.Location.Container, ".");
                        HistoryRestoreFileSystemPlanStore.Cleanup(new(record.TransactionId, [registration.Location.Container], objects, []), false, true,
                            Path.Combine(registration.Location.Container, "owner.json"));
                    }
                }
                else
                {
                    bool committed = record.Kind == RestoreCleanupKind.Committed;
                    if (journal is null || journal.ProtocolVersion != 2 || journal.TransactionId != record.TransactionId
                        || journal.ExpectedWorkspace.ConfigId != _runtime.ConfigId || journal.DesiredWorkspace.ConfigId != _runtime.ConfigId
                        || record.Plan is null || journal.FileSystemPlan != record.Plan || journal.CleanupCommitted != committed
                        || record.DurableOutcomeProof != OutcomeProof(journal, committed)
                        || !record.Plan.Path.Equals(Path.Combine(TransactionDirectory(record.TransactionId), "restore-plan.json"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Execution cleanup lacks a matching durable outcome proof.");
                    var plan = HistoryRestoreFileSystemPlanStore.Load(record.Plan, record.TransactionId);
                    ValidatePlanLocations(plan, record.Workspaces);
                    committedOutcome = committed;
                    HistoryRestoreFileSystemPlanStore.Reach("before-cleanup");
                    HistoryRestoreFileSystemPlanStore.Cleanup(plan, committed, true);
                }
                File.Delete(PreparationPath(record.TransactionId));
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                failures.Add(path + ": " + HistoryRestoreRecoveryDiagnostics.Describe(ex));
                HistoryRestoreRecoveryDiagnostics.Report(_runtime.ConfigId, record?.TransactionId, "Cleaning", record?.Plan is not null,
                    committedOutcome, ex, "MaintenancePreserved");
            }
        }
        if (failures.Count > 0) _runtime.ReportMaintenance("Restore cleanup: " + string.Join("\n", failures));
        else if (_runtime.MaintenanceDiagnostic?.StartsWith("Restore cleanup:", StringComparison.Ordinal) == true) _runtime.ReportMaintenance(null);
    }
}
