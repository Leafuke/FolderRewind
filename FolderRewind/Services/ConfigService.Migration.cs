using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record MigrationConfigurationExport(AppConfig Original, byte[] PortableUtf8, string Fingerprint);

public static partial class ConfigService
{
    internal static Task<MigrationConfigurationExport> CaptureMigrationExportAsync(IEnumerable<HistoryConfigId> selected)
        => UiDispatcherService.RunOnUiAsync(() =>
        {
            if (IsRecoveryMode) throw new InvalidOperationException("Complete configuration recovery before exporting a migration.");
            var ids = selected.ToHashSet();
            var original = ConfigSnapshotMapper.Copy(CurrentConfig);
            var portable = ConfigSnapshotMapper.Copy(original);
            var chosen = portable.BackupConfigs.Where(c => ids.Contains(new(c.Id))).ToArray();
            if (chosen.Length != ids.Count) throw new InvalidOperationException("Migration project selection changed.");
            portable.BackupConfigs.Clear(); foreach (var project in chosen) portable.BackupConfigs.Add(project);
            RemoveOnboardingMachineBindings(portable); NormalizeConfig(portable); PrepareSchemaOnePersistence(portable);
            return Task.FromResult(new MigrationConfigurationExport(original, SerializeConfig(portable),
                JsonSerializer.Serialize(CurrentConfig, AppJsonContext.Default.AppConfig)));
        });

    internal static Task ValidateMigrationExportAsync(string fingerprint, CancellationToken token)
        => UiDispatcherService.RunOnUiAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (JsonSerializer.Serialize(CurrentConfig, AppJsonContext.Default.AppConfig) != fingerprint)
                throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
            return Task.CompletedTask;
        });

    internal static async Task<ConfigImportResult> StageMigrationConfigurationAsync(byte[] source, IEnumerable<HistoryConfigId> selected,
        ConfigImportMode mode, CancellationToken token)
    {
        var ids = selected.ToHashSet();
        return await UiDispatcherService.RunOnUiAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (IsRecoveryMode) return Task.FromResult(new ConfigImportResult(ConfigImportStatus.Failed, "Configuration recovery must be completed first."));
            var target = ConfigSnapshotMapper.Copy(CurrentConfig);
            var imported = PrepareImportedConfig(source, mode, target);
            var chosen = imported.BackupConfigs.Where(c => ids.Contains(new(c.Id))).ToArray();
            if (chosen.Length != ids.Count) throw new InvalidDataException("Migration configuration does not contain the selected identities.");
            var merged = target.BackupConfigs.Count == 0 ? imported : target;
            if (ReferenceEquals(merged, imported))
            {
                merged.BackupConfigs.Clear(); foreach (var project in chosen) merged.BackupConfigs.Add(project);
            }
            else
            {
                foreach (var project in chosen)
                {
                    var old = merged.BackupConfigs.FirstOrDefault(c => new HistoryConfigId(c.Id).Matches(project.Id));
                    if (old is not null) merged.BackupConfigs.Remove(old);
                    merged.BackupConfigs.Add(project);
                }
                foreach (var profile in imported.CloudConnections)
                {
                    var matching = merged.CloudConnections.FirstOrDefault(p => p.Id == profile.Id);
                    if (matching is not null && mode == ConfigImportMode.CrossDevice)
                    {
                        var previousId = profile.Id; profile.Id = Guid.NewGuid().ToString("N");
                        foreach (var project in chosen.Where(c => c.Cloud.ConnectionProfileId == previousId)) project.Cloud.ConnectionProfileId = profile.Id;
                    }
                    else if (matching is not null) continue;
                    merged.CloudConnections.Add(profile);
                }
            }
            NormalizeConfig(merged); PrepareSchemaOnePersistence(merged);
            return Task.FromResult(ImportStore.Stage(SerializeConfig(merged), ValidateHostPayload, chosen.Select(c => c.Id).ToArray(), mode,
                preserveOtherProjectSetup: true));
        }).ConfigureAwait(false);
    }
}
