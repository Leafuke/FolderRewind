using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum DataMigrationScope { ConfigurationAndHistory, Configuration, History, MetadataOnly }
internal sealed record DataMigrationPreview(string Path, string FileSha256, ImmutableArray<HistoryTransferProject> Projects,
    byte[]? ConfigurationUtf8, HistoryMigrationBundlePreview? Bundle);
internal sealed record DataMigrationProjectIssue(HistoryConfigId ConfigId, string Diagnostic);
internal sealed record DataMigrationImportResult(ConfigImportResult? Configuration, HistoryTransferBatchImportResult? History,
    ImmutableArray<DataMigrationProjectIssue> Issues);
internal sealed record DataMigrationRecoveryContext(BackupConfig Snapshot, HistoryRuntime Runtime,
    HistoryReplicaRecoveryPreview Replicas, HistoryContinuationPreview Continuation, ImmutableArray<HistoryRestoreSourceBinding> Bindings);
internal sealed record DataMigrationRecoveryResult(HistoryReplicaRecoveryResult Replicas, HistoryWorkspace Workspace, bool CredentialReady);

/// <summary>Coordinates existing configuration, history, payload and permission transactions.</summary>
internal static class DataMigrationService
{
    internal static async Task ExportAsync(string path, IReadOnlyCollection<HistoryConfigId> ids, DataMigrationScope scope,
        bool includePayloads, CancellationToken token)
    {
        var runtimes = new Dictionary<HistoryConfigId, HistoryRuntime>();
        if (scope != DataMigrationScope.Configuration)
            foreach (var id in ids) runtimes[id] = await NativeHistoryCoreGateway.EnsureReadyAsync(id.Value, token).ConfigureAwait(false);
        var snapshot = await ConfigService.CaptureMigrationExportAsync(ids).ConfigureAwait(false);
        if (scope == DataMigrationScope.Configuration)
        {
            await ConfigService.ValidateMigrationExportAsync(snapshot.Fingerprint, token).ConfigureAwait(false);
            await AtomicFileService.WriteAsync(path, (stream, cancellation) => stream.WriteAsync(snapshot.PortableUtf8, cancellation).AsTask(), token).ConfigureAwait(false);
            return;
        }
        var projects = snapshot.Original.BackupConfigs.Where(c => ids.Contains(new HistoryConfigId(c.Id))).Select(c =>
            new HistoryMigrationExportProject(new(new(c.Id), c.Name, [.. c.SourceFolders.Select(f => new HistoryTransferSource(new(Guid.Parse(f.Id)), f.DisplayName, f.Path))], runtimes[new(c.Id)]),
                c.DestinationPath, BackupPasswordPreparationService.CreateBackend(c))).ToArray();
        if (scope == DataMigrationScope.MetadataOnly)
            await new HistoryRepositoryBatchTransferService().ExportAsync(projects.Select(p => p.History).ToArray(), path, AppRuntimeInfo.GetApplicationVersion()?.ToString() ?? "unknown", token).ConfigureAwait(false);
        else
            await new HistoryMigrationBundleService().ExportAsync(projects, path, AppRuntimeInfo.GetApplicationVersion()?.ToString() ?? "unknown",
                scope == DataMigrationScope.ConfigurationAndHistory ? snapshot.PortableUtf8 : null, includePayloads,
                cancellation => ConfigService.ValidateMigrationExportAsync(snapshot.Fingerprint, cancellation), token).ConfigureAwait(false);
    }

    internal static async Task<DataMigrationPreview> PreviewAsync(string path, CancellationToken token)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var config = ConfigService.PrepareImportedConfig(bytes);
            return new(path, Convert.ToHexString(SHA256.HashData(bytes)), [.. config.BackupConfigs.Select(c => new HistoryTransferProject(new(c.Id), c.Name,
                [.. c.SourceFolders.Select(f => new HistoryTransferSource(new(Guid.Parse(f.Id)), f.DisplayName, f.Path))], 0, 0, ImmutableSortedDictionary<string, string>.Empty))], bytes, null);
        }
        var bundle = await new HistoryMigrationBundleService().PreviewAsync(path, token).ConfigureAwait(false);
        return new(path, bundle.History.FileSha256, bundle.History.Projects, bundle.ConfigurationUtf8, bundle);
    }

    internal static async Task<DataMigrationImportResult> ImportAsync(DataMigrationPreview preview, IReadOnlyCollection<HistoryConfigId> ids,
        ConfigImportMode mode, bool metadataOnly, CancellationToken token, string oldRoot = "", string newRoot = "", bool includeConfiguration = true,
        IReadOnlyDictionary<HistoryConfigId, MigrationProjectPaths>? paths = null, string migrationWorkflowId = "")
    {
        var fresh = await PreviewAsync(preview.Path, token).ConfigureAwait(false);
        if (fresh.FileSha256 != preview.FileSha256 || ids.Count == 0 || ids.Any(id => fresh.Projects.All(p => p.ConfigId != id)))
            throw new IOException("Migration file or selection changed after preview.");
        var configurationBytes = fresh.ConfigurationUtf8;
        if (configurationBytes is not null && includeConfiguration && paths is not null)
        {
            var mapped = ConfigService.PrepareImportedConfig(configurationBytes);
            foreach (var project in mapped.BackupConfigs.Where(c => ids.Contains(new HistoryConfigId(c.Id))))
                if (paths.TryGetValue(new(project.Id), out var selectedPaths))
                {
                    project.DestinationPath = selectedPaths.BackupRoot;
                    foreach (var source in project.SourceFolders)
                        source.Path = selectedPaths.Sources.Single(f => Guid.Parse(f.SourceId) == Guid.Parse(source.Id)).Path;
                    var errors = MigrationProjectSetupService.Check(project, requireExecutionReady: false);
                    if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
                }
            configurationBytes = JsonSerializer.SerializeToUtf8Bytes(mapped, AppJsonContext.Default.AppConfig);
        }
        if (configurationBytes is not null && (!string.IsNullOrWhiteSpace(oldRoot) || !string.IsNullOrWhiteSpace(newRoot)))
        {
            var mapped = ConfigService.PrepareImportedConfig(configurationBytes);
            foreach (var project in mapped.BackupConfigs.Where(c => ids.Contains(new HistoryConfigId(c.Id)))) MigrationProjectSetupService.Relocate(project, oldRoot, newRoot);
            configurationBytes = JsonSerializer.SerializeToUtf8Bytes(mapped, AppJsonContext.Default.AppConfig);
        }
        var bundlePath = fresh.Path;
        if (fresh.Bundle is not null && !metadataOnly)
        {
            bundlePath = Path.Combine(ConfigService.ConfigDirectory, "migration", "bundles", fresh.FileSha256, "source.frhistory");
            Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
            if (!File.Exists(bundlePath))
            {
                await using var input = new FileStream(fresh.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await AtomicFileService.WriteAsync(bundlePath, (output, cancellation) => input.CopyToAsync(output, 65536, cancellation), token).ConfigureAwait(false);
            }
            await using var cached = File.OpenRead(bundlePath);
            if (Convert.ToHexString(await SHA256.HashDataAsync(cached, token).ConfigureAwait(false)) != fresh.FileSha256)
                throw new IOException("Cached migration bundle failed integrity verification.");
        }
        HistoryTransferBatchImportResult? history = null;
        if (fresh.Bundle is not null)
        {
            var configured = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Select(c => new HistoryConfigId(c.Id)).ToHashSet();
            history = await new HistoryRepositoryBatchTransferService(id => NativeHistoryCoreGateway.TryGetRuntime(id, out var runtime) ? runtime : null,
                configured.Contains).ImportAsync(bundlePath, ConfigService.ConfigDirectory, ids, token, fresh.FileSha256).ConfigureAwait(false);
            ids = history.Projects.Where(p => p.CommitStatus is HistoryTransferCommitStatus.Committed or HistoryTransferCommitStatus.CommittedRecoveryRequired).Select(p => p.ConfigId).ToArray();
        }
        ConfigImportResult? configuration = null;
        if (!metadataOnly && includeConfiguration && configurationBytes is not null && ids.Count > 0)
            configuration = await ConfigService.StageMigrationConfigurationAsync(configurationBytes, ids, mode, token, migrationWorkflowId).ConfigureAwait(false);
        var importId = configuration?.Success == true ? new ConfigImportTransactionStore(ConfigService.ConfigFilePath).StagedImportId ?? "" : "";
        var issues = ImmutableArray.CreateBuilder<DataMigrationProjectIssue>();
        foreach (var project in fresh.Projects.Where(p => ids.Contains(p.ConfigId)))
        {
            if (!metadataOnly && fresh.Bundle is not null)
            {
                DataMigrationStateStore.Save(ConfigService.ConfigDirectory, new(1, project, fresh.FileSha256, importId, BundlePath: bundlePath));
                if (configuration is null && (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Any(c => project.ConfigId.Matches(c.Id)))
                    MigrationSetupStateStore.Revoke(ConfigService.ConfigDirectory, project.ConfigId.Value, true, true);
            }
        }
        foreach (var project in fresh.Projects.Where(p => ids.Contains(p.ConfigId)))
        {
            if (!metadataOnly && fresh.Bundle?.IncludesPayloads == true && !token.IsCancellationRequested)
            {
                try
                {
                var source = configurationBytes is null ? null : ConfigService.PrepareImportedConfig(configurationBytes).BackupConfigs.FirstOrDefault(c => project.ConfigId.Matches(c.Id));
                var active = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).FirstOrDefault(c => project.ConfigId.Matches(c.Id));
                var root = paths is not null && paths.TryGetValue(project.ConfigId, out var selectedPaths) ? selectedPaths.BackupRoot
                    : (includeConfiguration ? source?.DestinationPath ?? active?.DestinationPath : active?.DestinationPath ?? source?.DestinationPath) ?? project.Migration?.BackupRootHint;
                if (string.IsNullOrWhiteSpace(root)) throw new IOException("Choose the target backup directory before installing migration payloads.");
                if (!Path.IsPathFullyQualified(root) || HistoryRestoreWorkspacePlanner.Overlaps(Path.GetFullPath(root), Path.GetFullPath(ConfigService.ConfigDirectory))
                    || HistoryRestoreWorkspacePlanner.Overlaps(Path.GetFullPath(root), Path.GetFullPath(AppRuntimeInfo.ApplicationBaseDirectory)))
                    throw new IOException("Migration backup destination overlaps protected application data.");
                foreach (var folder in (source?.SourceFolders ?? []).Concat((await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).SelectMany(c => c.SourceFolders)))
                    if (Path.IsPathFullyQualified(folder.Path) && !BackupPathOverlapPolicy.Validate(folder.Path, root).IsSafe)
                        throw new IOException("Migration backup destination overlaps a source directory.");
                foreach (var folder in paths?.Values.SelectMany(p => p.Sources) ?? [])
                    if (Path.IsPathFullyQualified(folder.Path) && !BackupPathOverlapPolicy.Validate(folder.Path, root).IsSafe)
                        throw new IOException("Migration backup destination overlaps a selected source directory.");
                await new HistoryMigrationBundleService().InstallPayloadsAsync(bundlePath, fresh.Bundle, project.ConfigId, ConfigService.ConfigDirectory, root, token).ConfigureAwait(false);
                }
                catch (Exception error) { issues.Add(new(project.ConfigId, CloudCommandSecurity.Redact(error.Message))); }
            }
            else if (!metadataOnly && fresh.Bundle?.IncludesPayloads == true) issues.Add(new(project.ConfigId, I18n.GetString("Common_Cancel")));
        }
        return new(configuration, history, issues.ToImmutable());
    }

    internal static bool IsAwaitingHistory(BackupConfig config)
        => config.HistoryRepositoryBinding is not null
            && new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(config.Id) is not null
            && !File.Exists(HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, new(config.Id)).DescriptorPath);

    internal static async Task<DataMigrationRecoveryContext> PrepareRecoveryAsync(BackupConfig snapshot, CancellationToken token)
    {
        if (IsAwaitingHistory(snapshot)) throw new InvalidOperationException(I18n.GetString("Migration_AwaitingHistory"));
        var id = new HistoryConfigId(snapshot.Id);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(snapshot.Id, token).ConfigureAwait(false);
        if (runtime.Health == HistoryRuntimeHealth.LocalStateInitializationRequired)
        {
            runtime.MigrationState.MarkImported();
            await new HistoryMigrationInitializationService(runtime).InitializeAsync(snapshot.SourceFolders.Select(f => new SourceId(Guid.Parse(f.Id))), token).ConfigureAwait(false);
        }
        var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, id);
        if (state is not null && !DataMigrationStateStore.IsActive(ConfigService.ConfigDirectory, state)
            && new ConfigImportTransactionStore(ConfigService.ConfigFilePath).StagedImportId == state.ConfigImportId)
            throw new InvalidOperationException(I18n.GetString("Migration_ConfigPrepared"));
        if (state is { BundlePath.Length: > 0 } && File.Exists(state.BundlePath))
        {
            var bundle = await new HistoryMigrationBundleService().PreviewAsync(state.BundlePath, token).ConfigureAwait(false);
            if (bundle.History.FileSha256 != state.SourceFileSha256) throw new IOException("Migration recovery bundle changed.");
            if (bundle.IncludesPayloads) await new HistoryMigrationBundleService().InstallPayloadsAsync(state.BundlePath, bundle, id, ConfigService.ConfigDirectory, snapshot.DestinationPath, token).ConfigureAwait(false);
        }
        await new HistoryMigrationBundleService().RecoverPayloadsAsync(ConfigService.ConfigDirectory, id, snapshot.DestinationPath, token).ConfigureAwait(false);
        var replicas = await new HistoryReplicaRecoveryService(runtime).InspectAsync(snapshot.DestinationPath, state?.Project.Migration?.Replicas, token).ConfigureAwait(false);
        var restore = await NativeHistoryApplicationService.CreateRestoreServiceAsync(snapshot, token).ConfigureAwait(false);
        var continuation = await new HistoryMigrationContinuationService(runtime,
            (binding, version, cancellation) => new HistoryExactWorkingStateProbe(runtime, restore).MatchesVersionAsync(binding, version, cancellation))
            .PreviewAsync(snapshot.SourceFolders.Select(f => new SourceId(Guid.Parse(f.Id))), state?.Project.Migration?.Continuation, token).ConfigureAwait(false);
        var bindings = ImmutableArray.CreateBuilder<HistoryRestoreSourceBinding>();
        foreach (var source in snapshot.SourceFolders)
        {
            var resolution = await HistorySourceBoundaryResolver.ResolveAsync(snapshot, source, token).ConfigureAwait(false);
            if (resolution.IsBlocked) throw new InvalidOperationException(I18n.GetString("Migration_RuntimePending"));
            bindings.Add(new(new(Guid.Parse(source.Id)), resolution.EffectiveFolder.Path, resolution.Boundary));
        }
        return new(ConfigSnapshotMapper.Copy(snapshot), runtime, replicas, continuation, bindings.ToImmutable());
    }

    internal static async Task<DataMigrationRecoveryResult> CompleteRecoveryAsync(DataMigrationRecoveryContext context,
        IReadOnlyCollection<HistoryReplicaRecoveryCandidate> candidates, IReadOnlyList<HistoryContinuationSelection> selections, CancellationToken token)
    {
        var snapshot = context.Snapshot; var id = new HistoryConfigId(snapshot.Id);
        var signature = NativeHistoryConfigLease.Signature(snapshot);
        var live = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => id.Matches(c.Id));
        if (NativeHistoryConfigLease.Signature(live) != signature) throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        var credentialReady = !snapshot.IsEncrypted || EncryptionService.HasStoredPassword(snapshot.Id);
        var replicas = await new HistoryReplicaRecoveryService(context.Runtime).ApplyAsync(context.Replicas, candidates,
            BackupPasswordPreparationService.CreateBackend(snapshot), true, credentialReady, token).ConfigureAwait(false);
        if (snapshot.IsEncrypted && replicas.Versions.Length > 0 && replicas.Versions.All(v => v.Readiness != HistoryReadiness.Ready)) credentialReady = false;
        var restore = await NativeHistoryApplicationService.CreateRestoreServiceAsync(snapshot, token).ConfigureAwait(false);
        var workspace = await new HistoryMigrationContinuationService(context.Runtime,
            (binding, version, cancellation) => new HistoryExactWorkingStateProbe(context.Runtime, restore).MatchesVersionAsync(binding, version, cancellation))
            .ApplyAsync(context.Continuation, context.Bindings, selections, token).ConfigureAwait(false);
        await BackupService.SynchronizeCaptureBaselinesWithWorkspaceAsync(snapshot, selections.Select(s => s.SourceId), token).ConfigureAwait(false);
        live = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => id.Matches(c.Id));
        if (NativeHistoryConfigLease.Signature(live) != signature) throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, id) ?? new(1,
            new(id, snapshot.Name, [.. snapshot.SourceFolders.Select(f => new HistoryTransferSource(new(Guid.Parse(f.Id)), f.DisplayName, f.Path))],
                0, 0, ImmutableSortedDictionary<string, string>.Empty), "", "");
        DataMigrationStateStore.Save(ConfigService.ConfigDirectory, state with { RecoveryCompleted = credentialReady, CompletedConfigSignature = signature,
            ConfigImportId = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(snapshot.Id) ?? "" });
        return new(replicas, workspace, credentialReady);
    }
}
