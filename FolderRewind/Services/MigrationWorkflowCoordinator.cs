using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum MigrationWizardMode { Import, Export, Continue, OriginalSettings }
internal sealed record MigrationWizardRequest(MigrationWizardMode Mode, DataMigrationScope Scope = DataMigrationScope.ConfigurationAndHistory,
    string? WorkflowId = null, string? ConfigId = null);

/// <summary>One Host-owned queue for import, activation, local recovery and permission decisions.</summary>
internal static class MigrationWorkflowCoordinator
{
    private static readonly SemaphoreSlim Operations = new(1, 1);
    internal static event Action? Changed;
    internal static MigrationWorkflowStore Store => new(ConfigService.ConfigDirectory);
    internal static MigrationWorkflowDocument? Load(string id) => Store.Load(id);
    private static void Save(MigrationWorkflowDocument document) { Store.Save(document); Changed?.Invoke(); }
    private static ImmutableArray<MigrationWorkflowDocument> ReadWorkflows() => Store.LoadAll((_, error) => LogService.LogError(
        "A migration progress record needs recovery; core history was not changed.", nameof(MigrationWorkflowCoordinator), error));

    internal static Task<MigrationHistoryStatus> HistoryStatusAsync(BackupConfig config, CancellationToken token = default)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (DataMigrationService.IsAwaitingHistory(config)) return MigrationHistoryStatus.AwaitingHistory;
            var id = new HistoryConfigId(config.Id); var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, id);
            var configToken = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(config.Id);
            if (state is not null && !DataMigrationStateStore.IsActive(ConfigService.ConfigDirectory, state)) return MigrationHistoryStatus.None;
            var signature = NativeHistoryConfigLease.Signature(config);
            if (state is { RecoveryCompleted: false } || state is not null && state.CompletedConfigSignature != signature
                || state is null && (configToken is not null || MigrationSetupStateStore.HasRequirement(ConfigService.ConfigDirectory, config.Id)
                    && MigrationSetupStateStore.IsBackupPending(ConfigService.ConfigDirectory, config.Id))) return MigrationHistoryStatus.AwaitingRecovery;
            if (MigrationSetupStateStore.IsBackupPending(ConfigService.ConfigDirectory, config.Id)) return MigrationHistoryStatus.AwaitingPermissions;
            if (MigrationBatchRecoveryService.NeedsCloudPermission(config)) return MigrationHistoryStatus.AwaitingCloudPermissions;
            if (state is null) return MigrationHistoryStatus.None;
            var finished = ReadWorkflows().LastOrDefault(d => d.IsFinished && d.Projects.Any(p => p.ConfigId == id.Value
                && p.ConfigImportId == (configToken ?? "") && p.ConfigurationSignature == signature));
            if (finished is not null) return finished.Projects.Any(p => p.ConfigId == id.Value && p.ReadyVersions < p.TotalVersions)
                ? MigrationHistoryStatus.MissingArchives : MigrationHistoryStatus.None;
            return MigrationHistoryStatus.None;
        }, token);

    internal static async Task<DataMigrationPreview> CachePreviewAsync(DataMigrationPreview preview, CancellationToken token)
        => preview with { Path = await CacheAsync(preview.Path, preview.FileSha256, token).ConfigureAwait(false) };

    internal static async Task<MigrationWorkflowDocument> SaveDraftAsync(DataMigrationPreview preview, IReadOnlyDictionary<string, MigrationProjectPaths> selected,
        bool includeConfiguration, bool metadataOnly, string? id)
    {
        await Operations.WaitAsync().ConfigureAwait(false);
        try
        {
            var existing = id is null ? null : Store.Load(id);
            if (existing is { ImportCommitted: true } || existing is { Stage: not MigrationWorkflowStage.AwaitingConfirmation }) return existing;
            var cached = await CacheAsync(preview.Path, preview.FileSha256, CancellationToken.None).ConfigureAwait(false);
            var document = new MigrationWorkflowDocument(existing?.WorkflowId ?? Guid.NewGuid().ToString("N"),
                [.. preview.Projects.Where(p => selected.ContainsKey(p.ConfigId.Value)).Select(p => new MigrationWorkflowProject(p.ConfigId.Value, p.Name) { Paths = selected[p.ConfigId.Value] })])
            { PackagePath = cached, PackageSha256 = preview.FileSha256, IncludeConfiguration = includeConfiguration, MetadataOnly = metadataOnly, ImportProjectIds = [.. selected.Keys] };
            Save(document); return document;
        }
        finally { Operations.Release(); }
    }

    internal static async Task<MigrationArchiveCandidate> ReadCandidateAsync(MigrationWorkflowProject project, string representationId, string path, CancellationToken token)
    {
        var config = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => new HistoryConfigId(project.ConfigId).Matches(c.Id));
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config.Id, token).ConfigureAwait(false);
        var representation = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).Single(r => r.RepresentationId.ToString() == representationId);
        HistoryReplicaRecoveryService.RequireRegularPath(config.DestinationPath, path);
        var facts = await HistoryReplicaRecoveryService.ReadFileFactsAsync(path, token).ConfigureAwait(false);
        var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, new(config.Id));
        var hints = state?.Project.Migration?.Replicas.Where(h => h.RepresentationId == representation.RepresentationId).ToArray() ?? [];
        var shared = (await runtime.Query.GetAllStorageReplicasAsync(token).ConfigureAwait(false)).Where(r => r.RepresentationId == representation.RepresentationId && !string.IsNullOrEmpty(r.ExpectedStorageSha256)).ToArray();
        var matches = hints.Any(h => h.Size == facts.Size && h.StorageSha256.Equals(facts.Hash, StringComparison.OrdinalIgnoreCase))
            || shared.Any(r => (r.ExpectedSize is null || r.ExpectedSize == facts.Size) && r.ExpectedStorageSha256!.Equals(facts.Hash, StringComparison.OrdinalIgnoreCase));
        if (!matches && (hints.Length > 0 || shared.Length > 0)) throw new InvalidDataException(I18n.GetString("MigrationWizard_ArchiveHashMismatch"));
        var verified = !config.IsEncrypted && (await BackupPasswordPreparationService.CreateBackend(config).VerifyAsync(representation, path, token).ConfigureAwait(false)).Success;
        return new(path, facts.Size, facts.Hash, matches, verified, File.GetLastWriteTimeUtc(path));
    }

    internal static async Task<ImmutableArray<MigrationWorkflowDocument>> PendingAsync(CancellationToken token = default)
    {
        await Operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var store = Store; var documents = (await Task.Run(ReadWorkflows, token).ConfigureAwait(false)).ToList();
            for (int index = 0; index < documents.Count; index++)
                if (documents[index].Stage == MigrationWorkflowStage.Processing)
                { documents[index] = documents[index] with { Stage = MigrationWorkflowStage.Paused }; store.Save(documents[index]); }
            foreach (var config in await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false))
            {
                var id = new HistoryConfigId(config.Id); var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, id);
                var pending = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(config.Id) ?? "";
                var backupPending = MigrationSetupStateStore.IsBackupPending(ConfigService.ConfigDirectory, config.Id);
                var needsPermission = backupPending || MigrationBatchRecoveryService.NeedsCloudPermission(config);
                if (state is null && pending.Length == 0 && !needsPermission) continue;
                if (state is not null && !DataMigrationStateStore.IsActive(ConfigService.ConfigDirectory, state)) continue;
                var signature = NativeHistoryConfigLease.Signature(config);
                var verified = state is { RecoveryCompleted: true } && state.CompletedConfigSignature == signature;
                if (verified && !needsPermission) continue;
                var existing = documents.LastOrDefault(d => d.Projects.Any(p => p.ConfigId == id.Value && p.ConfigImportId == pending)
                    && !d.IsFinished);
                if (existing is not null) continue;
                if (state is null && !needsPermission && documents.Any(d => d.IsFinished && d.Projects.Any(p =>
                    p.ConfigId == id.Value && p.ConfigImportId == pending && p.ConfigurationSignature == signature))) continue;
                // A completed local migration may still require a separate cloud permission decision.
                // Retain its recovery evidence and choices, then revalidate them when the wizard opens.
                var completed = verified ? documents.Where(d => d.IsFinished).SelectMany(d => d.Projects).LastOrDefault(p =>
                    p.ConfigId == id.Value && p.ConfigImportId == pending && p.ConfigurationSignature == signature) : null;
                var project = completed is not null ? completed with { Name = config.Name, Paths = PathsFor(config),
                    Phase = MigrationProjectPhase.Verified, BackupSelected = backupPending, CloudSelected = false }
                    : new MigrationWorkflowProject(id.Value, config.Name)
                {
                    Paths = PathsFor(config), ConfigImportId = pending, ConfigurationSignature = signature, RecoveryVerified = false,
                    Phase = DataMigrationService.IsAwaitingHistory(config) ? MigrationProjectPhase.AwaitingHistory : MigrationProjectPhase.Pending,
                    CloudEnabled = config.Cloud.Enabled
                };
                var adopted = new MigrationWorkflowDocument(Guid.NewGuid().ToString("N"), [project])
                { PackagePath = state?.BundlePath ?? "", PackageSha256 = state?.SourceFileSha256 ?? "", ImportCommitted = true,
                    Stage = MigrationWorkflowStage.NeedsAttention };
                store.Save(adopted); documents.Add(adopted);
            }
            return [.. documents.Where(d => !d.IsFinished)];
        }
        finally { Operations.Release(); }
    }

    internal static MigrationProjectPaths PathsFor(BackupConfig config) => new(config.DestinationPath,
        [.. config.SourceFolders.Select(f => new MigrationSourcePath(Guid.Parse(f.Id).ToString("N"), f.DisplayName, f.Path))]);

    internal static Task<MigrationWorkflowDocument?> FindRepairTaskAsync(string configId) => Task.Run(() => ReadWorkflows().LastOrDefault(d =>
        d.Stage == MigrationWorkflowStage.PartiallyCompleted && d.Projects.Any(p => new HistoryConfigId(p.ConfigId).Matches(configId) && p.ReadyVersions < p.TotalVersions)));

    internal static async Task<IReadOnlyDictionary<string, MigrationProjectPaths>> PreviewPathsAsync(DataMigrationPreview preview)
    {
        var imported = preview.ConfigurationUtf8 is null ? null : ConfigService.PrepareImportedConfig(preview.ConfigurationUtf8);
        var active = await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false);
        return preview.Projects.ToDictionary(p => p.ConfigId.Value, p =>
        {
            var config = imported?.BackupConfigs.SingleOrDefault(c => p.ConfigId.Matches(c.Id)) ?? active.SingleOrDefault(c => p.ConfigId.Matches(c.Id));
            return config is null ? new MigrationProjectPaths(p.Migration?.BackupRootHint ?? "", [.. p.Sources.Select(s => new MigrationSourcePath(s.SourceId.ToString(), s.Name, s.PathHint))]) : PathsFor(config);
        });
    }

    internal static async Task<MigrationWorkflowDocument> ImportAsync(DataMigrationPreview preview, IReadOnlyDictionary<string, MigrationProjectPaths> selected,
        bool includeConfiguration, bool metadataOnly, string? workflowId, CancellationToken token)
    {
        await Operations.WaitAsync(token).ConfigureAwait(false);
        MigrationWorkflowDocument? document = null;
        try
        {
            if (selected.Count == 0) throw new InvalidOperationException(I18n.GetString("Migration_NoProjects"));
            var configurationStore = new ConfigImportTransactionStore(ConfigService.ConfigFilePath);
            if (workflowId is null)
            {
                var reusable = ReadWorkflows().LastOrDefault(d => d.PackageSha256 == preview.FileSha256 && d.IncludeConfiguration == includeConfiguration
                    && d.MetadataOnly == metadataOnly && d.ImportCommitted && d.Projects.Length == selected.Count
                    && d.Projects.All(p => selected.TryGetValue(p.ConfigId, out var paths) && p.Paths is not null
                        && p.Paths.BackupRoot == paths.BackupRoot && p.Paths.Sources.SequenceEqual(paths.Sources)));
                if (reusable is not null)
                {
                    if (reusable.Stage == MigrationWorkflowStage.AwaitingRestart && reusable.ActivationToken == configurationStore.StagedImportId) return reusable;
                    var active = await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false);
                    if (!reusable.IsFinished && reusable.Projects.All(p => p.ConfigImportId == (configurationStore.PendingToken(p.ConfigId) ?? "")
                        && active.Any(c => new HistoryConfigId(p.ConfigId).Matches(c.Id) && PathsFor(c).BackupRoot == p.Paths!.BackupRoot
                            && PathsFor(c).Sources.SequenceEqual(p.Paths.Sources)))) return reusable;
                    var backend = new MigrationWorkflowBackend(); bool current = true;
                    foreach (var project in reusable.Projects)
                        current &= project.RecoveryVerified && await backend.IsRecoveryCurrentAsync(project, token).ConfigureAwait(false);
                    if (current) return reusable;
                }
            }
            if (configurationStore.StagedImportId is not null && (workflowId is null || configurationStore.ImportForWorkflow(workflowId, out _) is null))
                throw new InvalidOperationException(I18n.GetString("MigrationWizard_AnotherActivation"));
            var cached = await CacheAsync(preview.Path, preview.FileSha256, token).ConfigureAwait(false);
            document = workflowId is null ? new(Guid.NewGuid().ToString("N"), []) : Store.Load(workflowId) ?? throw new InvalidOperationException(I18n.GetString("MigrationWizard_TaskMissing"));
            var projects = document.Stage == MigrationWorkflowStage.AwaitingConfirmation ? new Dictionary<string, MigrationWorkflowProject>(StringComparer.Ordinal)
                : document.Projects.ToDictionary(p => p.ConfigId, StringComparer.Ordinal);
            foreach (var project in preview.Projects.Where(p => selected.ContainsKey(p.ConfigId.Value)))
                projects[project.ConfigId.Value] = new(project.ConfigId.Value, project.Name) { Paths = selected[project.ConfigId.Value], ImportPending = true };
            document = document with { Projects = [.. projects.Values], PackagePath = cached, PackageSha256 = preview.FileSha256,
                IncludeConfiguration = includeConfiguration, MetadataOnly = metadataOnly, Stage = MigrationWorkflowStage.Processing, ImportCommitted = false,
                ImportProjectIds = [.. selected.Keys] };
            Save(document);
            return await CommitImportAsync(document, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (document is null) throw; document = document with { Stage = MigrationWorkflowStage.Paused }; Save(document); return document; }
        finally { Operations.Release(); }
    }

    private static async Task<MigrationWorkflowDocument> CommitImportAsync(MigrationWorkflowDocument document, CancellationToken token)
    {
        var preview = await DataMigrationService.PreviewAsync(document.PackagePath, token).ConfigureAwait(false);
        if (preview.FileSha256 != document.PackageSha256) throw new IOException(I18n.GetString("Migration_CheckExpired"));
        var projects = document.Projects.Where(p => (document.ImportProjectIds.IsDefaultOrEmpty || document.ImportProjectIds.Contains(p.ConfigId))
            && preview.Projects.Any(i => i.ConfigId.Value == p.ConfigId)).ToArray();
        var paths = projects.Where(p => p.Paths is not null).ToDictionary(p => new HistoryConfigId(p.ConfigId), p => p.Paths!);
        if (!document.MetadataOnly && (!document.IncludeConfiguration || preview.ConfigurationUtf8 is null))
        {
            var active = await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false);
            foreach (var project in projects.Where(p => p.Paths is not null))
            {
                var current = active.SingleOrDefault(c => new HistoryConfigId(project.ConfigId).Matches(c.Id));
                if (current is null || PathsFor(current).BackupRoot == project.Paths!.BackupRoot && PathsFor(current).Sources.SequenceEqual(project.Paths.Sources)) continue;
                var draft = ConfigSnapshotMapper.Copy(current); draft.DestinationPath = project.Paths!.BackupRoot;
                foreach (var source in draft.SourceFolders) source.Path = project.Paths.Sources.Single(s => Guid.Parse(s.SourceId) == Guid.Parse(source.Id)).Path;
                await MigrationProjectSetupService.CompleteAsync(current, draft, releaseBackup: false, releaseCloud: false, cancellationToken: token).ConfigureAwait(false);
            }
        }
        var result = await DataMigrationService.ImportAsync(preview, [.. projects.Select(p => new HistoryConfigId(p.ConfigId))], ConfigImportMode.LocalRestore,
            document.MetadataOnly, token, includeConfiguration: document.IncludeConfiguration, paths: paths, migrationWorkflowId: document.WorkflowId).ConfigureAwait(false);
        var prepared = result.Configuration?.Status == ConfigImportStatus.StagedRequiresRestart;
        var configurationFailed = result.Configuration is { Success: false };
        var activation = prepared ? new ConfigImportTransactionStore(ConfigService.ConfigFilePath).StagedImportId ?? "" : "";
        var updated = document.Projects.Select(p =>
        {
            if (projects.All(i => i.ConfigId != p.ConfigId)) return p;
            var imported = result.History?.Projects.SingleOrDefault(i => i.ConfigId.Value == p.ConfigId);
            var failures = result.Issues.Where(i => i.ConfigId.Value == p.ConfigId).Select(i => i.Diagnostic).ToList();
            if (configurationFailed) failures.Add(result.Configuration!.Diagnostic);
            if (imported is not null && imported.CommitStatus is not (HistoryTransferCommitStatus.Committed or HistoryTransferCommitStatus.CommittedRecoveryRequired))
                failures.Add(I18n.GetString("MigrationWizard_HistoryNotCommitted"));
            var pending = configurationFailed || preview.Bundle is not null && imported?.CommitStatus is not (HistoryTransferCommitStatus.Committed or HistoryTransferCommitStatus.CommittedRecoveryRequired);
            return p with { ConfigImportId = prepared && !pending ? activation : new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(p.ConfigId) ?? "",
                ImportPending = pending, Phase = pending ? MigrationProjectPhase.Blocked : document.MetadataOnly ? MigrationProjectPhase.Completed : MigrationProjectPhase.Pending,
                Diagnostics = [.. failures] };
        });
        document = document with { Projects = [.. updated], ActivationToken = activation,
            ImportCommitted = true, Stage = document.MetadataOnly && !updated.Any(p => p.ImportPending) ? MigrationWorkflowStage.Completed
                : prepared ? MigrationWorkflowStage.AwaitingRestart : MigrationWorkflowStage.NeedsAttention };
        Save(document); return document;
    }

    internal static async Task<MigrationWorkflowDocument> ContinueAsync(string id, CancellationToken token)
    {
        await Operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var document = Store.Load(id) ?? throw new InvalidOperationException(I18n.GetString("MigrationWizard_TaskMissing"));
            if (document.Stage == MigrationWorkflowStage.AwaitingConfirmation) return document;
            if (document.Stage == MigrationWorkflowStage.PartiallyCompleted)
            {
                document = document with { Stage = MigrationWorkflowStage.NeedsAttention, Projects = [.. document.Projects.Select(p => p.ReadyVersions < p.TotalVersions
                    ? p with { RecoveryVerified = false, RetainMissingArchives = false, Phase = MigrationProjectPhase.Pending } : p)] }; Save(document);
            }
            var correlated = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).ImportForWorkflow(document.WorkflowId, out var correlatedId);
            if ((!document.ImportCommitted || document.ActivationToken != correlatedId && document.Projects.Any(p => p.ImportPending))
                && correlated?.Status is ConfigImportStatus.StagedRequiresRestart or ConfigImportStatus.Applied)
            {
                var committed = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).ProjectsForWorkflow(document.WorkflowId)
                    .Select(id => new HistoryConfigId(id).Value).ToHashSet(StringComparer.Ordinal);
                document = document with { ImportCommitted = true, Stage = MigrationWorkflowStage.AwaitingRestart, ActivationToken = correlatedId,
                    Projects = [.. document.Projects.Select(p => document.ImportProjectIds.IsDefaultOrEmpty || document.ImportProjectIds.Contains(p.ConfigId)
                        ? p with { ConfigImportId = committed.Contains(p.ConfigId) ? correlatedId : "", ImportPending = !committed.Contains(p.ConfigId),
                            Phase = committed.Contains(p.ConfigId) ? MigrationProjectPhase.Pending : MigrationProjectPhase.Blocked } : p)] }; Save(document);
            }
            if (document.Stage == MigrationWorkflowStage.AwaitingRestart && correlated?.Status == ConfigImportStatus.RolledBack)
            { document = document with { Stage = MigrationWorkflowStage.NeedsAttention, ImportCommitted = false, ActivationToken = "" }; Save(document); }
            if (document.Stage == MigrationWorkflowStage.AwaitingRestart)
            {
                var configStore = new ConfigImportTransactionStore(ConfigService.ConfigFilePath);
                if (configStore.StagedImportId == document.ActivationToken) return document;
                if (!document.Projects.Any(p => p.ConfigImportId == document.ActivationToken)
                    || document.Projects.Where(p => p.ConfigImportId == document.ActivationToken).Any(p => configStore.PendingToken(p.ConfigId) != document.ActivationToken))
                    throw new InvalidOperationException(I18n.GetString("MigrationWizard_ActivationChanged"));
                document = document with { Stage = MigrationWorkflowStage.NeedsAttention, ResumeAfterActivation = false }; Save(document);
            }
            if (!document.ImportCommitted || document.Projects.Any(p => p.ImportPending))
            {
                if (document.ImportCommitted)
                { document = document with { ImportProjectIds = [.. document.Projects.Where(p => p.ImportPending).Select(p => p.ConfigId)] }; Save(document); }
                document = await CommitImportAsync(document, token).ConfigureAwait(false);
                if (document.Stage == MigrationWorkflowStage.AwaitingRestart || document.MetadataOnly) return document;
            }
            var result = await new MigrationWorkflowRunner(Store, new MigrationWorkflowBackend()).RunAsync(id, token).ConfigureAwait(false);
            Changed?.Invoke(); return result;
        }
        finally { Operations.Release(); }
    }

    internal static async Task<MigrationProjectInspection> InspectAsync(MigrationWorkflowProject project, CancellationToken token)
        => await new MigrationWorkflowBackend().InspectAsync(project, token).ConfigureAwait(false);

    internal static async Task<MigrationWorkflowDocument> UpdateProjectAsync(string id, MigrationWorkflowProject project, bool pathsChanged, CancellationToken token)
    {
        await Operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var document = Store.Load(id) ?? throw new InvalidOperationException(I18n.GetString("MigrationWizard_TaskMissing"));
            if (pathsChanged && project.Paths is { } paths)
            {
                var config = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => new HistoryConfigId(project.ConfigId).Matches(c.Id));
                var draft = ConfigSnapshotMapper.Copy(config); draft.DestinationPath = paths.BackupRoot;
                foreach (var folder in draft.SourceFolders) folder.Path = paths.Sources.Single(s => Guid.Parse(s.SourceId) == Guid.Parse(folder.Id)).Path;
                await MigrationProjectSetupService.CompleteAsync(config, draft, releaseBackup: false, releaseCloud: false, cancellationToken: token).ConfigureAwait(false);
                project = project with { RecoveryVerified = false, RetainMissingArchives = false, Phase = MigrationProjectPhase.Pending,
                    ArchiveSelections = [], BranchSelections = [], ConfigurationSignature = "" };
            }
            var index = document.Projects.FindIndex(p => p.ConfigId == project.ConfigId);
            document = document with { Projects = document.Projects.SetItem(index, project), Stage = MigrationWorkflowStage.NeedsAttention };
            Save(document); return document;
        }
        finally { Operations.Release(); }
    }

    internal static async Task<MigrationWorkflowDocument> ConfirmAsync(string id, CancellationToken token)
    {
        await Operations.WaitAsync(token).ConfigureAwait(false);
        try { var result = await new MigrationWorkflowRunner(Store, new MigrationWorkflowBackend()).ConfirmAsync(id, token).ConfigureAwait(false); Changed?.Invoke(); return result; }
        finally { Operations.Release(); }
    }

    internal static void RequestRestart(string id)
    {
        var document = Store.Load(id) ?? throw new InvalidOperationException(I18n.GetString("MigrationWizard_TaskMissing"));
        if (document.Stage != MigrationWorkflowStage.AwaitingRestart || document.ActivationToken != new ConfigImportTransactionStore(ConfigService.ConfigFilePath).StagedImportId)
            throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        Save(document with { ResumeAfterActivation = true });
    }

    internal static async Task<string?> ConsumeRestartAsync()
    {
        var document = (await Task.Run(ReadWorkflows).ConfigureAwait(false)).LastOrDefault(d => d.ResumeAfterActivation && !d.IsFinished);
        if (document is null) return null;
        Save(document with { ResumeAfterActivation = false }); return document.WorkflowId;
    }

    private static async Task<string> CacheAsync(string source, string expectedHash, CancellationToken token)
    {
        var destination = Path.Combine(ConfigService.ConfigDirectory, "migration", "workflow-files", expectedHash, "source" + Path.GetExtension(source));
        if (!File.Exists(destination))
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await AtomicFileService.WriteAsync(destination, (output, cancellation) => input.CopyToAsync(output, 65536, cancellation), token).ConfigureAwait(false);
        }
        await using var cached = File.OpenRead(destination);
        if (Convert.ToHexString(await SHA256.HashDataAsync(cached, token).ConfigureAwait(false)) != expectedHash)
            throw new IOException(I18n.GetString("Migration_CheckExpired"));
        return destination;
    }

    private static int FindIndex<T>(this ImmutableArray<T> source, Func<T, bool> match)
    { for (int i = 0; i < source.Length; i++) if (match(source[i])) return i; throw new InvalidOperationException("Migration project was not found."); }
}
