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
            FilterMigrationCloudConnections(portable);
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
        ConfigImportMode mode, CancellationToken token, string migrationWorkflowId = "")
    {
        var ids = selected.ToHashSet();
        return await UiDispatcherService.RunOnUiAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (IsRecoveryMode) return Task.FromResult(new ConfigImportResult(ConfigImportStatus.Failed, "Configuration recovery must be completed first."));
            var target = ConfigSnapshotMapper.Copy(CurrentConfig);
            var imported = PrepareImportedConfig(source, mode, target, ids);
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
            }
            var profiles = imported.CloudConnections.ToArray();
            if (ReferenceEquals(merged, imported))
            {
                // An installation without projects can still have managed cloud connections.
                merged.CloudConnections.Clear();
                foreach (var profile in target.CloudConnections) merged.CloudConnections.Add(profile);
            }
            var reservedIds = merged.CloudConnections.Select(p => p.Id).Concat(profiles.Select(p => p.Id)).ToHashSet(StringComparer.Ordinal);
            var remappedIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                var matching = merged.CloudConnections.FirstOrDefault(p => p.Id == profile.Id);
                if (matching is not null)
                {
                    if (mode == ConfigImportMode.LocalRestore && SameMigrationCloudProfile(matching, profile)) continue;
                    var previousId = profile.Id;
                    do { profile.Id = Guid.NewGuid().ToString("N"); } while (!reservedIds.Add(profile.Id));
                    remappedIds.Add(previousId, profile.Id);
                }
                merged.CloudConnections.Add(profile);
            }
            foreach (var project in chosen)
                if (project.Cloud.ConnectionProfileId is { } previousId && remappedIds.TryGetValue(previousId, out var replacement))
                    project.Cloud.ConnectionProfileId = replacement;
            NormalizeConfig(merged); PrepareSchemaOnePersistence(merged);
            return Task.FromResult(ImportStore.Stage(SerializeConfig(merged), ValidateHostPayload, chosen.Select(c => c.Id).ToArray(), mode,
                preserveOtherProjectSetup: true, migrationWorkflowId: migrationWorkflowId));
        }).ConfigureAwait(false);
    }

    // Only detached migration snapshots enter this method; target profiles are never pruned.
    private static void FilterMigrationCloudConnections(AppConfig config)
    {
        var profiles = config.CloudConnections.Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Where(group => group.Skip(1).All(profile => SameMigrationCloudProfile(group.First(), profile)))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in config.BackupConfigs)
        {
            var id = project.Cloud.ConnectionProfileId;
            if (string.IsNullOrWhiteSpace(id) || !profiles.TryGetValue(id, out var profile)
                || !(profile.Scope == CloudConnectionScope.Shared || profile.Scope == CloudConnectionScope.Project
                    && MigrationRecoveryPolicy.SameId(project.Id, profile.OwnerProjectId ?? "")))
            {
                project.Cloud = new();
                continue;
            }
            required.Add(id);
        }
        config.CloudConnections.Clear();
        foreach (var profile in profiles.Values.Where(p => required.Contains(p.Id))) config.CloudConnections.Add(profile);
    }

    private static bool SameMigrationCloudProfile(CloudConnectionProfile first, CloudConnectionProfile second)
        => JsonSerializer.Serialize(first, AppJsonContext.Default.CloudConnectionProfile)
            == JsonSerializer.Serialize(second, AppJsonContext.Default.CloudConnectionProfile);
}
