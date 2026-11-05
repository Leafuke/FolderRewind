#if PERFORMANCE_UI_PROFILING
using FolderRewind.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services.Development;

internal static partial class PerformanceUiProfiling
{
    // Real Host models and source-generated JSON; the ordinary unit-test project has model doubles.
    // Run before both workload clocks, without changing the performance fixture's configuration.
    internal static void VerifySnapshotCopies(string root)
    {
        if (Environment.GetEnvironmentVariable("FOLDERREWIND_UI_PROFILING") != "1") return;
        var defaultStatus = I18n.Format("FolderManager_Status");
        var neverBackedUp = I18n.Format("FolderManager_NeverBackedUp");
        var defaults = new ManagedFolder();
        var hydrated = JsonSerializer.Deserialize("{}", AppJsonContext.Default.ManagedFolder)!;
        if (defaults.StatusText != defaultStatus || defaults.LastBackupTime != neverBackedUp ||
            hydrated.StatusText != defaultStatus || hydrated.LastBackupTime != neverBackedUp)
            throw new InvalidOperationException("ManagedFolder public construction/JSON defaults changed.");

        const string previousBackup = "2026-10-30T12:34:56.789+08:00";
        var folder = new ManagedFolder { Path = Path.Combine(root, "snapshot-validation-source"),
            DisplayName = "Snapshot validation", Description = "Persisted metadata", IsFavorite = true,
            StatusText = "Transient runtime status", LastBackupTime = previousBackup };
        var source = new BackupConfig { Name = "Snapshot validation", DestinationPath = Path.Combine(root, "snapshot-validation-repository") };
        source.SourceFolders.Add(folder);
        using var document = JsonDocument.Parse("{\"value\":7,\"nested\":{\"text\":\"detached\"}}");
        var payload = new ProviderStatePayload { SchemaVersion = 1, Data = document.RootElement };
        folder.ProviderStates.Add("folderrewind.snapshot-validation", payload);
        var before = JsonSerializer.Serialize(source, AppJsonContext.Default.BackupConfig);
        var configClone = ConfigSnapshotMapper.Copy(source);
        var folderClone = ConfigSnapshotMapper.Copy(folder);
        if (before != JsonSerializer.Serialize(configClone, AppJsonContext.Default.BackupConfig) ||
            JsonSerializer.Serialize(folder, AppJsonContext.Default.ManagedFolder) != JsonSerializer.Serialize(folderClone, AppJsonContext.Default.ManagedFolder))
            throw new InvalidOperationException("Snapshot JSON changed.");
        if (configClone.SourceFolders[0].StatusText != defaultStatus || folderClone.StatusText != defaultStatus ||
            configClone.SourceFolders[0].LastBackupTime != previousBackup || folderClone.LastBackupTime != previousBackup)
            throw new InvalidOperationException("Snapshot must reset transient status and preserve persisted LastBackupTime.");
        if (ReferenceEquals(configClone.SourceFolders, source.SourceFolders) || ReferenceEquals(configClone.SourceFolders[0], folder) ||
            ReferenceEquals(configClone.SourceFolders[0].ProviderStates, folder.ProviderStates) ||
            ReferenceEquals(configClone.SourceFolders[0].ProviderStates["folderrewind.snapshot-validation"], payload))
            throw new InvalidOperationException("Snapshot model/provider state was not detached.");
        document.Dispose();
        payload.SchemaVersion = 99;
        payload.Data = JsonSerializer.SerializeToElement(new { value = 99 });
        folder.ProviderStates.Add("extra", new ProviderStatePayload { SchemaVersion = 1, Data = JsonSerializer.SerializeToElement(new { changed = true }) });
        foreach (var clonedFolder in new[] { configClone.SourceFolders[0], folderClone })
        {
            var clonedPayload = clonedFolder.ProviderStates["folderrewind.snapshot-validation"];
            if (clonedPayload.SchemaVersion != 1 || clonedPayload.Data.GetProperty("value").GetInt32() != 7 ||
                clonedPayload.Data.GetProperty("nested").GetProperty("text").GetString() != "detached" || clonedFolder.ProviderStates.ContainsKey("extra"))
                throw new InvalidOperationException("Snapshot provider JSON did not survive source document disposal or mutation.");
        }
        var emptyBackupTime = new ManagedFolder { LastBackupTime = null! };
        if (ConfigSnapshotMapper.Copy(emptyBackupTime).LastBackupTime != string.Empty ||
            ConfigSnapshotMapper.Copy((ManagedFolder?)null) is not null || ConfigSnapshotMapper.Copy((BackupConfig?)null) is not null)
            throw new InvalidOperationException("Snapshot null/empty backup-time behavior changed.");
        var nullFolder = new BackupConfig(); nullFolder.SourceFolders.Add(null!);
        if (ConfigSnapshotMapper.Copy(nullFolder).SourceFolders[0] is not null)
            throw new InvalidOperationException("Null folder entries no longer survive snapshot copying.");

        var fixture = ConfigService.CurrentConfig;
        if (JsonSerializer.Serialize(fixture, AppJsonContext.Default.AppConfig) !=
            JsonSerializer.Serialize(ConfigSnapshotMapper.Copy(fixture), AppJsonContext.Default.AppConfig))
            throw new InvalidOperationException("Full 1,000-source fixture snapshot JSON changed.");
        File.WriteAllText(Path.Combine(root, "snapshot-validation.json"), JsonSerializer.Serialize(new
        {
            status = "PASS", publicConstructorDefaults = true, jsonConstructorDefaults = true,
            configAndFolderJsonEqual = true, transientStatusReset = true, lastBackupTimePreserved = true,
            providerStateDetached = true, providerJsonSurvivesSourceDisposal = true,
            nullBehaviorPreserved = true, fullFixtureJsonEqual = true,
            fixtureSourceCount = fixture.BackupConfigs.Sum(config => config.SourceFolders.Count)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
#endif
