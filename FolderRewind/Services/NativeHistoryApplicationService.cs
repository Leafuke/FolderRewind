using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class NativeHistoryApplicationService
{
    public static async Task<HistoryRestoreResult> RestoreVersionAsync(
        BackupConfig config,
        ManagedFolder folder,
        VersionId versionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        return await CreateRestoreService(config, runtime).RestoreVersionAsync(
            versionId,
            new HistoryRestoreSourceBinding(Source(folder), folder.Path),
            workspace,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> RestoreCheckpointAsync(
        BackupConfig config,
        CheckpointId checkpointId,
        bool completeCheckpoint,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var checkpoint = await runtime.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Checkpoint does not exist.");
        var restorableSources = checkpoint.Sources
            .Where(item => item.VersionId is not null)
            .Select(item => item.SourceId)
            .ToHashSet();
        var bindings = config.SourceFolders
            .Where(folder => completeCheckpoint || restorableSources.Contains(Source(folder)))
            .Select(folder => new HistoryRestoreSourceBinding(Source(folder), folder.Path))
            .ToArray();
        return await CreateRestoreService(config, runtime).RestoreCheckpointAsync(
            checkpointId,
            bindings,
            workspace,
            completeCheckpoint
                ? HistoryCheckpointRestoreScope.CompleteCheckpoint
                : HistoryCheckpointRestoreScope.AvailableMappedSources,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreResult> CheckoutAsync(
        BackupConfig config,
        BranchUpdateId selectedTipId,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var workspace = await RequireWorkspaceAsync(runtime, cancellationToken).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        var bindings = config.SourceFolders
            .Select(folder => new HistoryRestoreSourceBinding(Source(folder), folder.Path))
            .ToArray();
        return await new HistoryCheckoutService(runtime, restore).CheckoutAsync(
            selectedTipId,
            bindings,
            workspace,
            HistoryCheckoutProtectionMode.DiscardCurrentChanges,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task ApplyAutomaticRetentionAsync(
        BackupConfig config,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        var archive = new SevenZipHistoryArchiveBackend(config);
        var representations = new RepresentationRuntime(
        [
            new CoreArchiveRepresentationHandler(archive),
            new SmartDeltaRepresentationHandler(archive)
        ]);
        async Task<IRepresentationEnvironment> Environment(CancellationToken token)
            => await BuildEnvironmentAsync(runtime, token).ConfigureAwait(false);
        var payloads = new FileSystemHistoryLocalPayloadStore();
        var planner = new HistoryRetentionPlanner(runtime, representations, Environment, payloads);
        var plan = await planner.PlanAsync(
            new HistoryRetentionRequest(
                config.Archive.KeepCount,
                MaterializationFidelity.Overlay,
                HistoryRetentionOperationRoots.Empty,
                allowPostMigrationCleanup: true),
            cancellationToken).ConfigureAwait(false);
        if (!plan.CanExecute)
        {
            LogService.LogWarning(
                "[Retention] " + string.Join(" ", plan.Blockers),
                nameof(NativeHistoryApplicationService));
            return;
        }
        var executor = new HistoryRetentionExecutor(
            runtime,
            planner,
            representations,
            Environment,
            archive,
            payloads,
            new ArtifactLedgerGarbageCollector(config));
        var result = await executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            LogService.LogWarning(
                "[Retention] " + result.Diagnostic,
                nameof(NativeHistoryApplicationService));
        }
    }

    public static async Task ReleaseVersionAsync(
        BackupConfig config,
        VersionId versionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.MaterializationPolicies.SetAsync(
            versionId,
            MaterializationPolicyState.Released,
            "Explicit user release",
            cancellationToken).ConfigureAwait(false);
        await ApplyAutomaticRetentionAsync(config, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<HistoryRestoreService> CreateRestoreServiceAsync(
        BackupConfig config,
        CancellationToken cancellationToken = default)
    {
        var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(config.Id);
        await runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        return CreateRestoreService(config, runtime);
    }

    private static HistoryRestoreService CreateRestoreService(BackupConfig config, HistoryRuntime runtime)
    {
        var archive = new SevenZipHistoryArchiveBackend(config);
        var representations = new RepresentationRuntime(
        [
            new CoreArchiveRepresentationHandler(archive),
            new SmartDeltaRepresentationHandler(archive)
        ]);
        return new HistoryRestoreService(
            runtime,
            representations,
            token => BuildEnvironmentAsync(runtime, token),
            new FileSystemHistoryRestoreMutationBackend());
    }

    private static async Task<IRepresentationEnvironment> BuildEnvironmentAsync(
        HistoryRuntime runtime,
        CancellationToken cancellationToken)
    {
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var representations = await runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var replicas = new List<StorageReplica>();
        var active = new List<ReplicaId>();
        foreach (var representation in representations)
        {
            foreach (var replica in await runtime.Query.GetStorageReplicasAsync(
                         representation.RepresentationId,
                         cancellationToken).ConfigureAwait(false))
            {
                replicas.Add(replica);
                var updates = await runtime.Query.GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, cancellationToken)
                    .ConfigureAwait(false);
                var parents = updates.SelectMany(item => item.ParentUpdateIds).ToHashSet();
                if (updates.Where(item => !parents.Contains(item.UpdateId))
                    .Any(item => item.State == ReplicaLifecycleState.Active))
                {
                    active.Add(replica.ReplicaId);
                }
            }
        }
        return new RepresentationEnvironment(catalog?.Entries, replicas, active);
    }

    private static async Task<HistoryWorkspace> RequireWorkspaceAsync(
        HistoryRuntime runtime,
        CancellationToken cancellationToken)
    {
        var load = await runtime.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status != DeviceLocalStateStatus.Valid || load.Value is null)
            throw new DeviceLocalStateConflictException("Workspace requires recovery before restore.");
        return load.Value;
    }

    private static SourceId Source(ManagedFolder folder)
        => Guid.TryParse(folder.Id, out var id) && id != Guid.Empty
            ? new SourceId(id)
            : throw new InvalidDataException("ManagedFolder has no stable SourceId.");

    private sealed class ArtifactLedgerGarbageCollector(BackupConfig config) : IHistoryArtifactGarbageCollector
    {
        public async Task GarbageCollectAsync(
            IReadOnlySet<Guid> protectedArtifactRootIds,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.DestinationPath)) return;
            var store = new FileArtifactLedgerStore(config.DestinationPath);
            await store.GarbageCollectUnreachableAsync(
                protectedArtifactRootIds.Select(id => new ArtifactId(id)),
                dryRun: false,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class SevenZipHistoryArchiveBackend : IArchiveRepresentationBackend, IHistoryCompactionBackend
{
    private readonly BackupConfig _config;

    public SevenZipHistoryArchiveBackend(BackupConfig config)
        => _config = config ?? throw new ArgumentNullException(nameof(config));

    public async ValueTask<PayloadVerificationResult> VerifyAsync(
        VersionRepresentation representation,
        string localPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
            return new(false, string.Empty, "Archive payload is missing.");
        var result = await RunAsync("t", localPath, outputDirectory: null, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? new(true, $"7z-test:{new FileInfo(localPath).Length}", string.Empty)
            : new(false, string.Empty, result.Diagnostic);
    }

    public async ValueTask MaterializeAsync(
        IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        foreach (var input in dependencyFirstInputs)
        {
            var result = await RunAsync(
                "x",
                input.LocalPath,
                stagingDirectory,
                workingDirectory: null,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidDataException(result.Diagnostic);
            ApplyDeletedFiles(input.Representation, stagingDirectory);
        }
        var marker = Path.Combine(stagingDirectory, "__FolderRewind_Internal");
        if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
    }

    public async Task<HistoryCompactionPayload> CreateFullAsync(
        SourceVersion version,
        string materializedDirectory,
        RepresentationId replacementRepresentationId,
        string durableOutputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(durableOutputDirectory);
        var path = Path.Combine(durableOutputDirectory, "payload.7z");
        var result = await RunAsync(
            "a",
            path,
            outputDirectory: null,
            workingDirectory: materializedDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidDataException(result.Diagnostic);
        return new(
            "7z",
            path,
            new FileInfo(path).Length,
            null,
            version.StateFingerprint,
            ImmutableDictionary<string, string>.Empty);
    }

    public ValueTask<PayloadVerificationResult> DeepVerifyAsync(
        VersionRepresentation representation,
        string payloadPath,
        CancellationToken cancellationToken)
        => VerifyAsync(representation, payloadPath, cancellationToken);

    private async Task<(bool Success, string Diagnostic)> RunAsync(
        string operation,
        string archivePath,
        string? outputDirectory,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var executable = SevenZipExecutableLocator.Resolve(
            ConfigService.CurrentConfig?.GlobalSettings?.SevenZipPath);
        if (string.IsNullOrWhiteSpace(executable))
            return (false, I18n.GetString("BackupService_Log_SevenZipNotFound"));
        var password = _config.IsEncrypted ? EncryptionService.RetrievePassword(_config.Id) : null;
        if (_config.IsEncrypted && string.IsNullOrEmpty(password))
            return (false, "Encrypted archive credential is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory
                : workingDirectory
        };
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(archivePath);
        if (operation == "x") start.ArgumentList.Add("-o" + outputDirectory);
        if (operation == "a") start.ArgumentList.Add("*");
        start.ArgumentList.Add("-y");
        if (!string.IsNullOrEmpty(password)) start.ArgumentList.Add("-p" + password);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return (false, "7z process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0
            ? (true, string.Empty)
            : (false, string.IsNullOrWhiteSpace(error) ? output : error);
    }

    private static void ApplyDeletedFiles(VersionRepresentation representation, string stagingDirectory)
    {
        if (!representation.RepresentationSpecificMetadata.TryGetValue("deletedFiles", out var encoded)) return;
        var root = Path.GetFullPath(stagingDirectory);
        foreach (var relative in encoded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = relative.Replace('\\', '/');
            if (!FolderRewind.History.Storage.HistoryRepositoryPaths.IsSafeRepositoryRelativePath(normalized))
                throw new InvalidDataException("Smart deletion path is unsafe.");
            var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var relation = Path.GetRelativePath(root, target);
            if (relation == ".." || relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Smart deletion escapes the materialization root.");
            if (File.Exists(target)) File.Delete(target);
            else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
