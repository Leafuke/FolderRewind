using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Configuration;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;

namespace FolderRewind.Services;

internal sealed record MigrationPathPreview(BackupConfig Original, BackupConfig Draft);

internal static class MigrationProjectSetupService
{
    internal static void Relocate(BackupConfig draft, string oldRoot, string newRoot)
    {
        // Compute the complete preview before changing any draft field.
        var paths = draft.SourceFolders.Select(f => MigrationPathRebindPolicy.ReplaceRoot(f.Path, oldRoot, newRoot)).ToArray();
        var destination = MigrationPathRebindPolicy.ReplaceRoot(draft.DestinationPath, oldRoot, newRoot);
        for (var i = 0; i < paths.Length; i++) draft.SourceFolders[i].Path = paths[i];
        draft.DestinationPath = destination;
    }

    internal static IReadOnlyList<MigrationPathPreview> PreviewMappings(IEnumerable<BackupConfig> snapshots, string oldRoot, string newRoot)
    {
        var results = new List<MigrationPathPreview>();
        foreach (var original in snapshots)
        {
            var draft = ConfigSnapshotMapper.Copy(original);
            Relocate(draft, oldRoot, newRoot);
            if (draft.DestinationPath != original.DestinationPath
                || draft.SourceFolders.Any(f => f.Path != original.SourceFolders.Single(old => old.Id == f.Id).Path))
                results.Add(new(original, draft));
        }
        return results;
    }
    internal static Task<IReadOnlyList<MigrationResumeResult>> ApplyMappingsAsync(IEnumerable<MigrationPathPreview> selected, CancellationToken token)
        => MigrationBatchConfirmation.RunAsync(selected, item => item.Original.Id, async (item, cancellation) =>
        {
            await CompleteAsync(item.Original, item.Draft, releaseBackup: false, cancellationToken: cancellation).ConfigureAwait(false);
            return new MigrationResumeResult(item.Original.Id, false, false, "");
        }, error => CloudCommandSecurity.Redact(error.Message), token);

    internal static IReadOnlyList<string> Check(BackupConfig draft, bool requireExecutionReady = true)
    {
        var errors = new List<string>();
        if (draft.SourceFolders.Count == 0) errors.Add(I18n.GetString("Migration_NoSources"));
        foreach (var source in draft.SourceFolders)
        {
            if (!Path.IsPathFullyQualified(source.Path) || requireExecutionReady && !Directory.Exists(source.Path))
                errors.Add(I18n.Format("Migration_SourceMissing", source.DisplayName, source.Path));
            if (!BackupPathOverlapPolicy.Validate(source.Path, draft.DestinationPath).IsSafe)
                errors.Add(I18n.Format("Migration_PathOverlap", source.DisplayName));
        }
        if (!Path.IsPathFullyQualified(draft.DestinationPath)) errors.Add(I18n.GetString("Migration_DestinationInvalid"));
        var paths = HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, new(draft.Id));
        if (requireExecutionReady && draft.HistoryRepositoryBinding is not null && !File.Exists(paths.DescriptorPath))
            errors.Add(I18n.GetString(DataMigrationService.IsAwaitingHistory(draft) ? "Migration_AwaitingHistory" : "Migration_HistoryMissing"));
        var requiredPlugins = new[]
        {
            draft.RequiredPluginId,
            draft.Kind.OwnerId == ConfigSchema.CoreOwnerId ? "" : draft.Kind.OwnerId,
            draft.BackupScope.IsPluginScopeEnabled ? draft.BackupScope.OwnerId : "",
            draft.ArtifactTransformPolicy?.FailureBehavior == PersistedArtifactTransformFailureBehavior.RequireTransform
                ? draft.ArtifactTransformPolicy.Transformer.PluginId : ""
        };
        foreach (var pluginId in requiredPlugins.Where(id => requireExecutionReady && !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!PluginV3RuntimeService.IsActive(new PluginId(pluginId)))
                errors.Add(I18n.Format("Migration_PluginPending", pluginId));
        if (requireExecutionReady && draft.IsEncrypted && !EncryptionService.HasStoredPassword(draft.Id))
            errors.Add(I18n.GetString("Migration_PasswordPending"));
        var recovery = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, new(draft.Id));
        var configToken = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(draft.Id);
        if (requireExecutionReady && (recovery is not null && DataMigrationStateStore.IsActive(ConfigService.ConfigDirectory, recovery) && !recovery.RecoveryCompleted
            || configToken is not null && (recovery is null || recovery.ConfigImportId != configToken || !recovery.RecoveryCompleted)))
            errors.Add(I18n.GetString("Migration_RecoveryPending"));
        return errors;
    }

    internal static async Task CompleteAsync(BackupConfig originalSnapshot, BackupConfig draft,
        bool releaseBackup = true, bool releaseCloud = false, string? expectedEvidence = null,
        CancellationToken cancellationToken = default)
    {
        if (!MigrationRecoveryPolicy.SameId(draft.Id, originalSnapshot.Id)
            || !draft.SourceFolders.Select(f => f.Id).SequenceEqual(originalSnapshot.SourceFolders.Select(f => f.Id)))
            throw new InvalidOperationException("Migration must preserve project and source identities.");
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(new(draft.Id), cancellationToken).ConfigureAwait(false);
        var liveSnapshot = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => c.Id == draft.Id);
        if (JsonSerializer.Serialize(liveSnapshot, AppJsonContext.Default.BackupConfig) != JsonSerializer.Serialize(originalSnapshot, AppJsonContext.Default.BackupConfig))
            throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        if (expectedEvidence is not null && (await MigrationBatchRecoveryService.CheckProjectAsync(liveSnapshot, cancellationToken).ConfigureAwait(false)).Evidence != expectedEvidence)
            throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        var errors = await Task.Run(() => Check(draft, releaseBackup || releaseCloud), cancellationToken).ConfigureAwait(false);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        await Task.Run(() =>
        {
            Directory.CreateDirectory(draft.DestinationPath);
            var probe = Path.Combine(draft.DestinationPath, $".FolderRewind-migration-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }, cancellationToken).ConfigureAwait(false);
        var store = new ConfigImportTransactionStore(ConfigService.ConfigFilePath);
        BackupConfig? previous = null;
        if (store.ModeFor(draft.Id) == ConfigImportMode.LocalRestore && store.ReadPreviousConfig(draft.Id) is { } bytes)
            previous = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.AppConfig)?.BackupConfigs
                .FirstOrDefault(c => MigrationRecoveryPolicy.SameId(c.Id, draft.Id));
        var isConfigImport = store.PendingToken(draft.Id) is not null;
        if (!isConfigImport) previous = originalSnapshot;
        using var currentDocument = JsonDocument.Parse(JsonSerializer.Serialize(draft, AppJsonContext.Default.BackupConfig));
        using var previousDocument = previous is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(previous, AppJsonContext.Default.BackupConfig));
        var resets = MigrationRecoveryPolicy.ResetSources(currentDocument.RootElement, previousDocument?.RootElement,
            !isConfigImport || store.ModeFor(draft.Id) == ConfigImportMode.LocalRestore).Select(id => new SourceId(Guid.Parse(id))).ToArray();
        var recovered = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, new(draft.Id));
        if (recovered is { RecoveryCompleted: true } && recovered.CompletedConfigSignature == NativeHistoryConfigLease.Signature(draft)) resets = [];
        var pathsChanged = draft.DestinationPath != originalSnapshot.DestinationPath
            || draft.SourceFolders.Any(f => f.Path != originalSnapshot.SourceFolders.Single(old => old.Id == f.Id).Path);
        var expectedApplied = ConfigSnapshotMapper.Copy(draft);
        if (pathsChanged)
        {
            // Persist corrections while execution remains withheld. Revisions invalidate old operation snapshots.
            cancellationToken.ThrowIfCancellationRequested();
            var appliedRevision = Guid.NewGuid().ToString("N");
            expectedApplied.ConfigRevision = appliedRevision;
            var changed = false;
            var result = await ConfigService.UpdateAndSaveAsync(current =>
            {
                var live = current.BackupConfigs.Single(c => c.Id == draft.Id);
                if (JsonSerializer.Serialize(live, AppJsonContext.Default.BackupConfig) != JsonSerializer.Serialize(originalSnapshot, AppJsonContext.Default.BackupConfig))
                    throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
                live.DestinationPath = draft.DestinationPath;
                foreach (var source in live.SourceFolders) source.Path = draft.SourceFolders.Single(f => f.Id == source.Id).Path;
                live.ConfigRevision = appliedRevision;
                changed = true;
            }, publishSavedEvent: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                if (changed)
                {
                    await UiDispatcherService.RunOnUiAsync(() =>
                    {
                        var live = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == draft.Id);
                        if (live.ConfigRevision == appliedRevision)
                        {
                            live.DestinationPath = originalSnapshot.DestinationPath;
                            foreach (var source in live.SourceFolders) source.Path = originalSnapshot.SourceFolders.Single(f => f.Id == source.Id).Path;
                            live.ConfigRevision = originalSnapshot.ConfigRevision;
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                    var rollback = await ConfigService.SaveAsync(publishSavedEvent: false).ConfigureAwait(false);
                    if (!rollback.Success) throw new IOException("Migration path rollback failed; execution remains paused.", rollback.Exception);
                }
                throw new IOException(result.ErrorMessage, result.Exception);
            }
        }
        if (!releaseBackup && !releaseCloud && DataMigrationService.IsAwaitingHistory(expectedApplied)) return;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(draft.Id, cancellationToken).ConfigureAwait(false);
        runtime.MigrationState.MarkImported();
        if (pathsChanged)
        {
            if (recovered is not null) DataMigrationStateStore.Save(ConfigService.ConfigDirectory, recovered with { RecoveryCompleted = false });
            MigrationSetupStateStore.Revoke(ConfigService.ConfigDirectory, draft.Id, true, false);
        }
        await new HistoryMigrationInitializationService(runtime).InitializeInsideOperationAsync(
            draft.SourceFolders.Select(f => new SourceId(Guid.Parse(f.Id))), operation, resets, cancellationToken).ConfigureAwait(false);
        if (expectedApplied.HistoryRepositoryBinding is null)
            expectedApplied.HistoryRepositoryBinding = new() { FormatVersion = HistoryRepositoryDescriptor.CurrentFormatVersion };
        cancellationToken.ThrowIfCancellationRequested();
        await MigrationBatchRecoveryService.GrantIfUnchangedAsync(expectedApplied, releaseBackup, releaseCloud).ConfigureAwait(false);
    }
}
