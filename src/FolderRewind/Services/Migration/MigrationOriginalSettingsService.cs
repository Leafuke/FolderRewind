using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record MigrationOriginalSettings(BackupConfig Current, bool AutoBackup, bool Cloud);
internal static class MigrationOriginalSettingsService
{
    internal static async Task<IReadOnlyList<MigrationOriginalSettings>> PreviewAsync(string path)
    {
        var original = await ConfigService.PreviewConfigImportAsync(path).ConfigureAwait(false);
        var current = await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false);
        var store = new ConfigImportTransactionStore(ConfigService.ConfigFilePath);
        var result = new List<MigrationOriginalSettings>();
        foreach (var live in current)
        {
            if (store.PendingToken(live.Id) is null) continue;
            var source = original.Config.BackupConfigs.SingleOrDefault(c => MigrationRecoveryPolicy.SameId(c.Id, live.Id));
            if (source is null || source.SourceFolders.Count != live.SourceFolders.Count
                || !source.SourceFolders.All(f => live.SourceFolders.Any(c => MigrationRecoveryPolicy.SameId(c.Id, f.Id)))) continue;
            result.Add(new(live, source.Automation.AutoBackupEnabled, source.Cloud.Enabled));
        }
        return result;
    }
    internal static async Task<IReadOnlyList<string>> ApplyAsync(IEnumerable<MigrationOriginalSettings> selected, CancellationToken token = default)
    {
        var errors = new List<string>();
        foreach (var item in selected)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                await using var lease = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(new(item.Current.Id), token).ConfigureAwait(false);
                var live = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => c.Id == item.Current.Id);
                if (JsonSerializer.Serialize(live, AppJsonContext.Default.BackupConfig) != JsonSerializer.Serialize(item.Current, AppJsonContext.Default.BackupConfig))
                    throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
                if (live.Automation.AutoBackupEnabled == item.AutoBackup && live.Cloud.Enabled == item.Cloud) continue;
                // Changing preferences never implicitly grants execution permission.
                MigrationSetupStateStore.Revoke(ConfigService.ConfigDirectory, live.Id, true, true);
                var save = await ConfigService.UpdateAndSaveAsync(config =>
                {
                    var project = config.BackupConfigs.Single(c => c.Id == live.Id);
                    if (JsonSerializer.Serialize(project, AppJsonContext.Default.BackupConfig) != JsonSerializer.Serialize(live, AppJsonContext.Default.BackupConfig))
                        throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
                    project.Automation.AutoBackupEnabled = item.AutoBackup;
                    project.Cloud.Enabled = item.Cloud;
                    project.ConfigRevision = Guid.NewGuid().ToString("N");
                }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                if (!save.Success)
                {
                    await UiDispatcherService.RunOnUiAsync(() =>
                    {
                        var project = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == live.Id);
                        project.Automation.AutoBackupEnabled = live.Automation.AutoBackupEnabled;
                        project.Cloud.Enabled = live.Cloud.Enabled;
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                    var rollback = await ConfigService.SaveAsync(publishSavedEvent: false).ConfigureAwait(false);
                    if (!rollback.Success) throw new InvalidOperationException("Original switches could not be restored; execution remains paused.");
                    throw save.Exception ?? new InvalidOperationException(save.ErrorMessage);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { errors.Add(item.Current.Name + ": " + CloudCommandSecurity.Redact(error.Message)); }
        }
        return errors;
    }
}
