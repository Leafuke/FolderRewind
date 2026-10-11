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

namespace FolderRewind.Services;

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

    internal static IReadOnlyList<string> Check(BackupConfig draft)
    {
        var errors = new List<string>();
        if (draft.SourceFolders.Count == 0) errors.Add(I18n.GetString("Migration_NoSources"));
        foreach (var source in draft.SourceFolders)
        {
            if (!Path.IsPathFullyQualified(source.Path) || !Directory.Exists(source.Path))
                errors.Add(I18n.Format("Migration_SourceMissing", source.DisplayName, source.Path));
            if (!BackupPathOverlapPolicy.Validate(source.Path, draft.DestinationPath).IsSafe)
                errors.Add(I18n.Format("Migration_PathOverlap", source.DisplayName));
        }
        if (!Path.IsPathFullyQualified(draft.DestinationPath)) errors.Add(I18n.GetString("Migration_DestinationInvalid"));
        var paths = HistoryRepositoryPaths.ForConfigDirectory(ConfigService.ConfigDirectory, new(draft.Id));
        if (draft.HistoryRepositoryBinding is not null && !File.Exists(paths.DescriptorPath))
            errors.Add(I18n.GetString("Migration_HistoryMissing"));
        var requiredPlugins = new[]
        {
            draft.RequiredPluginId,
            draft.Kind.OwnerId == ConfigSchema.CoreOwnerId ? "" : draft.Kind.OwnerId,
            draft.BackupScope.IsPluginScopeEnabled ? draft.BackupScope.OwnerId : "",
            draft.ArtifactTransformPolicy?.FailureBehavior == PersistedArtifactTransformFailureBehavior.RequireTransform
                ? draft.ArtifactTransformPolicy.Transformer.PluginId : ""
        };
        foreach (var pluginId in requiredPlugins.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!PluginV3RuntimeService.IsActive(new PluginId(pluginId)))
                errors.Add(I18n.Format("Migration_PluginPending", pluginId));
        if (draft.IsEncrypted && !EncryptionService.HasStoredPassword(draft.Id))
            errors.Add(I18n.GetString("Migration_PasswordPending"));
        return errors;
    }

    internal static async Task CompleteAsync(BackupConfig originalSnapshot, BackupConfig draft)
    {
        if (draft.Id != originalSnapshot.Id || !draft.SourceFolders.Select(f => f.Id).SequenceEqual(originalSnapshot.SourceFolders.Select(f => f.Id)))
            throw new InvalidOperationException("Migration must preserve project and source identities.");
        var errors = await Task.Run(() => Check(draft)).ConfigureAwait(false);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        // Capability probing only; this never examines backup archives.
        await Task.Run(() =>
        {
            Directory.CreateDirectory(draft.DestinationPath);
            var probe = Path.Combine(draft.DestinationPath, $".FolderRewind-migration-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }).ConfigureAwait(false);
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(draft.Id).ConfigureAwait(false);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(runtime.ConfigId).ConfigureAwait(false);
        runtime.MigrationState.MarkImported();
        var configImportPending = new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(draft.Id) is not null
            && MigrationSetupStateStore.IsPending(ConfigService.ConfigDirectory, draft.Id);
        var resets = draft.SourceFolders.Where(f => configImportPending || f.Path != originalSnapshot.SourceFolders.Single(s => s.Id == f.Id).Path)
            .Select(f => new SourceId(Guid.Parse(f.Id))).ToArray();
        await new HistoryMigrationInitializationService(runtime).InitializeInsideOperationAsync(
            draft.SourceFolders.Select(f => new SourceId(Guid.Parse(f.Id))), operation, resets).ConfigureAwait(false);
        var appliedRevision = Guid.NewGuid().ToString("N");
        var changed = false;
        var result = await ConfigService.UpdateAndSaveAsync(current =>
        {
            var live = current.BackupConfigs.Single(c => c.Id == draft.Id);
            if (live.ConfigRevision != originalSnapshot.ConfigRevision || live.DestinationPath != originalSnapshot.DestinationPath || live.SourceFolders.Count != originalSnapshot.SourceFolders.Count
                || live.SourceFolders.Any(f => f.Path != originalSnapshot.SourceFolders.Single(s => s.Id == f.Id).Path))
                throw new InvalidOperationException("Configuration changed while migration paths were being edited. Reopen setup.");
            live.DestinationPath = draft.DestinationPath;
            foreach (var source in live.SourceFolders) source.Path = draft.SourceFolders.Single(f => f.Id == source.Id).Path;
            live.ConfigRevision = appliedRevision;
            changed = true;
            live.Automation.AutoBackupEnabled = false;
            live.Cloud.Enabled = false;
        }).ConfigureAwait(false);
        if (!result.Success)
        {
            runtime.MigrationState.RequireConfirmation();
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
                var restored = await ConfigService.SaveAsync(publishSavedEvent: false).ConfigureAwait(false);
                if (!restored.Success) throw new IOException("Migration paths could not be persisted or rolled back; setup remains pending.", restored.Exception);
            }
            throw new IOException(result.ErrorMessage, result.Exception);
        }
        MigrationSetupStateStore.Confirm(ConfigService.ConfigDirectory, draft.Id);
    }
}
