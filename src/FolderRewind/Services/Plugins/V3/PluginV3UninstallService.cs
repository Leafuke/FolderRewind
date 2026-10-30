using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3UninstallService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static string TransactionsRoot => Path.Combine(
        AppRuntimeInfo.WritableAppDataBaseDirectory,
        "FolderRewind",
        "plugin-uninstall-transactions");

    private static PluginUninstallTransactionCoordinator CreateCoordinator()
        => new(
            TransactionsRoot,
            new PluginUninstallTransactionCallbacks(
                CommitConfigurationAsync,
                RestoreConfigurationAsync));

    public static async ValueTask<PluginUninstallTransactionResult> ExecuteAsync(
        PluginUninstallPreview preview,
        CancellationToken cancellationToken)
    {
        var snapshot = await CaptureSnapshotAsync(preview.PluginId, cancellationToken).ConfigureAwait(false);
        return await CreateCoordinator().ExecuteAsync(
            new PluginUninstallTransactionRequest(
                preview.PluginId,
                preview.CodePath,
                preview.DataPath,
                JsonSerializer.SerializeToElement(snapshot, Json)),
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    {
        var results = await CreateCoordinator().RecoverAsync(cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.Outcome == OperationOutcome.Failed)
            {
                LogService.LogError(
                    $"Plugin uninstall recovery requires manual intervention at '{result.RecoveryPath}': {result.Diagnostic}",
                    "PluginV3Uninstall");
            }
            else if (!string.IsNullOrWhiteSpace(result.Diagnostic))
            {
                LogService.LogWarning(
                    $"Plugin uninstall recovery completed: {result.Diagnostic}",
                    "PluginV3Uninstall");
            }
        }

        if (results.Any(result => result.Outcome == OperationOutcome.Failed))
            throw new IOException("Plugin uninstall recovery is incomplete; plugin loading is blocked until recovery succeeds.");
    }

    private static async ValueTask<PluginV3UninstallRollbackSnapshot> CaptureSnapshotAsync(
        PluginId pluginId,
        CancellationToken cancellationToken)
    {
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins.TypedSettings;
        var hadTypedSettings = settings.TryGetValue(pluginId.Value, out var typedSettings);
        var configs = ConfigService.CurrentConfig.BackupConfigs
            .Select(config => CaptureConfig(config, pluginId))
            .Where(snapshot => snapshot is not null)
            .Cast<PluginV3UninstallConfigSnapshot>()
            .ToArray();
        var migration = await PluginV3OfflineUpgradeService.CaptureMigrationStateAsync(
            pluginId,
            cancellationToken).ConfigureAwait(false);
        return new PluginV3UninstallRollbackSnapshot(
            hadTypedSettings,
            (typedSettings ?? new Dictionary<string, JsonElement>()).ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            configs,
            migration);
    }

    private static PluginV3UninstallConfigSnapshot? CaptureConfig(
        BackupConfig config,
        PluginId pluginId)
    {
        var hadConfigState = config.ProviderStates.TryGetValue(pluginId.Value, out var configState);
        var folders = config.SourceFolders
            .Where(folder => folder.ProviderStates.ContainsKey(pluginId.Value))
            .Select(folder => new PluginV3UninstallFolderStateSnapshot(
                folder.Id,
                ClonePayload(folder.ProviderStates[pluginId.Value])))
            .ToArray();
        return !hadConfigState && folders.Length == 0
            ? null
            : new PluginV3UninstallConfigSnapshot(
                config.Id,
                config.ConfigRevision,
                hadConfigState,
                hadConfigState ? ClonePayload(configState!) : null,
                folders);
    }

    private static async ValueTask CommitConfigurationAsync(
        PluginUninstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadSnapshot(journal);
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
        settings.TypedSettings.Remove(journal.PluginId.Value);
        foreach (var configSnapshot in snapshot.Configs)
        {
            var config = FindConfig(configSnapshot.ConfigId);
            config.ProviderStates.Remove(journal.PluginId.Value);
            foreach (var folderSnapshot in configSnapshot.Folders)
            {
                var folder = FindFolder(config, folderSnapshot.FolderId);
                folder.ProviderStates.Remove(journal.PluginId.Value);
            }
            config.ConfigRevision = Guid.NewGuid().ToString("N");
        }

        await PluginV3OfflineUpgradeService.SuppressAutomaticMigrationAsync(
            journal.PluginId,
            cancellationToken).ConfigureAwait(false);
        var save = ConfigService.SaveWithResult();
        if (!save.Success)
            throw new IOException(save.ErrorMessage ?? "Plugin data removal could not be persisted.", save.Exception);
    }

    private static async ValueTask RestoreConfigurationAsync(
        PluginUninstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadSnapshot(journal);
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins.TypedSettings;
        if (snapshot.HadTypedSettings)
        {
            settings[journal.PluginId.Value] = snapshot.TypedSettings.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal);
        }
        else
        {
            settings.Remove(journal.PluginId.Value);
        }

        foreach (var configSnapshot in snapshot.Configs)
        {
            var config = FindConfig(configSnapshot.ConfigId);
            if (configSnapshot.HadConfigState)
                config.ProviderStates[journal.PluginId.Value] = ClonePayload(configSnapshot.ConfigState!);
            else
                config.ProviderStates.Remove(journal.PluginId.Value);
            foreach (var folderSnapshot in configSnapshot.Folders)
            {
                var folder = FindFolder(config, folderSnapshot.FolderId);
                folder.ProviderStates[journal.PluginId.Value] = ClonePayload(folderSnapshot.State);
            }
            config.ConfigRevision = configSnapshot.ConfigRevision;
        }

        var save = ConfigService.SaveWithResult();
        if (!save.Success)
            throw new IOException(save.ErrorMessage ?? "Plugin uninstall rollback could not restore configuration.", save.Exception);
        await PluginV3OfflineUpgradeService.RestoreMigrationStateAsync(
            journal.PluginId,
            snapshot.MigrationState,
            cancellationToken).ConfigureAwait(false);
    }

    private static PluginV3UninstallRollbackSnapshot ReadSnapshot(PluginUninstallTransactionJournal journal)
        => journal.RollbackSnapshot.Deserialize<PluginV3UninstallRollbackSnapshot>(Json)
           ?? throw new InvalidDataException("Plugin uninstall rollback snapshot is empty.");

    private static BackupConfig FindConfig(string configId)
        => ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(config =>
               StringComparer.Ordinal.Equals(config.Id, configId))
           ?? throw new InvalidDataException($"Plugin uninstall recovery cannot find config '{configId}'.");

    private static ManagedFolder FindFolder(BackupConfig config, string folderId)
        => config.SourceFolders.FirstOrDefault(folder => StringComparer.Ordinal.Equals(folder.Id, folderId))
           ?? throw new InvalidDataException(
               $"Plugin uninstall recovery cannot find folder '{folderId}' in config '{config.Id}'.");

    private static ProviderStatePayload ClonePayload(ProviderStatePayload payload)
        => new() { SchemaVersion = payload.SchemaVersion, Data = payload.Data.Clone() };

    public sealed record PluginV3UninstallRollbackSnapshot(
        bool HadTypedSettings,
        IReadOnlyDictionary<string, JsonElement> TypedSettings,
        IReadOnlyList<PluginV3UninstallConfigSnapshot> Configs,
        PluginMigrationState? MigrationState);

    public sealed record PluginV3UninstallConfigSnapshot(
        string ConfigId,
        string ConfigRevision,
        bool HadConfigState,
        ProviderStatePayload? ConfigState,
        IReadOnlyList<PluginV3UninstallFolderStateSnapshot> Folders);

    public sealed record PluginV3UninstallFolderStateSnapshot(
        string FolderId,
        ProviderStatePayload State);
}
