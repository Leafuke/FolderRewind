#if NATIVE_ARTIFACT_ACCEPTANCE
using FolderRewind.Models;
using FolderRewind.ViewModels;
using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

// Explicit development-only entry point; all data and network inputs come from the marked fixture.
internal static class UiUxRoundTwoAcceptance
{
    internal static async Task<int> RunAsync(string path)
    {
        var root = Path.GetFullPath(path);
        if (!AppRuntimeInfo.IsMsiDistribution || !File.Exists(Path.Combine(root, "acceptance-owner"))) return 2;
        Environment.SetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT", Path.Combine(root, "appdata"));
        var results = new List<object>();
        ConfigService.Initialize();
        if (!ConfigService.ConfigDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); results.Add(new { name, status = "PASS" }); }
            catch (Exception error) { results.Add(new { name, status = "FAIL", detail = error.ToString() }); }
            await File.WriteAllTextAsync(Path.Combine(root, "service-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        await ConfigService.UpdateAndSaveAsync(c =>
        {
            c.GlobalSettings.SevenZipPath = Path.Combine(Environment.CurrentDirectory, "7za.exe");
            c.GlobalSettings.RcloneExecutablePath = File.ReadAllText(Path.Combine(root, "rclone-path")).Trim();
            c.GlobalSettings.Plugins.AutoCheckUpdates = false;
            c.GlobalSettings.EnableNotifications = false;
        });
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "round two original");
        var project = new BackupConfig { Name = "Round two project", DestinationPath = Path.Combine(root, "backups") };
        project.SourceFolders.Add(new ManagedFolder { Path = source, DisplayName = "source" });
        if (ConfigService.CurrentConfig.BackupConfigs.Count != 0) return 2;
        await BackupSetupCoordinator.CommitAsync([project]);
        await Check("Settings repeated saves and known runtime facts", async () =>
        {
            var draft = new ConfigSettingsDraftService(ConfigService.CurrentConfig.BackupConfigs.Single());
            draft.Draft.Name = "Round two saved";
            Require((await draft.CommitAsync()).Success, "First save failed.");
            draft.Draft.Filters.Blacklist.Add(new("skip.tmp"));
            Require((await draft.CommitAsync()).Success, "Second save failed.");
            var live = ConfigService.CurrentConfig.BackupConfigs.Single();
            live.Cloud.LastRunUtc = DateTime.UtcNow; live.Cloud.LastExitCode = 5; live.Cloud.LastErrorMessage = "fixture-offline";
            draft.Draft.Filters.Blacklist.Add(new("another.tmp"));
            Require((await draft.CommitAsync()).Success, "Runtime cloud result caused a stale draft.");
            Require(ConfigService.CurrentConfig.BackupConfigs.Single().Cloud.LastExitCode == 5, "Runtime result was overwritten.");
            var saving = draft.CommitAsync(); draft.Draft.Name = "Unsaved next edit";
            Require((await saving).Success && draft.IsDirty, "Edits during save were lost.");
        });
        await Check("Real concurrent settings remain conflicts and preserve edits", async () =>
        {
            var live = ConfigService.CurrentConfig.BackupConfigs.Single(); var draft = new ConfigSettingsDraftService(live);
            draft.Draft.Name = "Keep my draft"; live.Name = "Round two project";
            Require(!(await draft.CommitAsync()).Success && draft.ConflictSections.Contains("Name"), "Real conflict was accepted.");
            Require(draft.Draft.Name == "Keep my draft", "Conflicting draft was discarded.");
            Require((await ConfigService.SaveAsync()).Success, "Could not persist external update.");
        });
        await Check("Consumed setup draft disappears and failed creation retains its draft", async () =>
        {
            await BackupSetupSessionStore.SaveAsync(new() { Name = "Consumed draft", DestinationPath = Path.Combine(root, "draft-backups"), SourcePaths = [source] });
            var vm = new BackupSetupViewModel(); vm.Initialize(null); vm.Resume(); vm.Next(); vm.Next();
            await vm.SubmitAsync(false, () => Task.FromResult<string?>(null));
            Require(vm.CreatedConfigId is not null && !BackupSetupSessionStore.HasDraft, "Consumed draft remains after commit.");
            Require(ConfigService.CurrentConfig.BackupConfigs.Count == 2, "Project was not created.");
            await BackupSetupSessionStore.SaveAsync(new() { Name = "Failed creation", DestinationPath = source, SourcePaths = [source] });
            var failed = new BackupSetupViewModel(); failed.Initialize(null); failed.Resume(); failed.Next(); failed.Next();
            await failed.SubmitAsync(false, () => Task.FromResult<string?>(null));
            Require(failed.CreatedConfigId is null && BackupSetupSessionStore.HasDraft, "Failed creation lost its draft.");
            BackupSetupSessionStore.Discard();
        });
        await Check("Migration valid to corrupt to valid revokes stale preview", async () =>
        {
            var valid = Path.Combine(root, "valid.json");
            await DataMigrationService.ExportAsync(valid, ConfigService.CurrentConfig.BackupConfigs.Select(c => new FolderRewind.History.Domain.HistoryConfigId(c.Id)).ToArray(), DataMigrationScope.Configuration, false, default);
            var invalid = Path.Combine(root, "invalid.frhistory"); await File.WriteAllTextAsync(invalid, "not a zip");
            using var vm = new MigrationWizardViewModel(); await vm.InitializeAsync(new(MigrationWizardMode.Import));
            var selected = valid; vm.PickPackage += _ => Task.FromResult<string?>(selected); await vm.ChoosePackageAsync();
            Require(vm.CanContinue && vm.Projects.Count > 0, "Valid preview unavailable.");
            selected = invalid; await vm.ChoosePackageAsync();
            Require(!vm.CanContinue && vm.Projects.Count == 0 && vm.HasErrorDetails, "Failed read retained old preview.");
            selected = valid; await vm.ChoosePackageAsync();
            Require(vm.CanContinue && !vm.HasErrorDetails, "Could not recover by reselecting.");
        });
        project = ConfigService.CurrentConfig.BackupConfigs.First();
        await Check("Production backup creates a cloud upload candidate", async () =>
        {
            var result = await BackupService.BackupForSetupAsync(project, null);
            Require(result.Sources.Any(s => s.Outcome == SetupSourceOutcome.Captured), "No source was backed up.");
        });
        await Check("WebDAV check upload offline retry and named independent discovery", async () =>
        {
            var endpoint = File.ReadAllText(Path.Combine(root, "webdav-url")).Trim();
            using var vm = new CloudSetupViewModel(); await vm.InitializeAsync(project.Id);
            vm.Executable = ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath;
            await vm.CreateWebDavAsync(endpoint, "fixture", "fixture-password", default);
            await vm.VerifyWriteAsync(default); Require(vm.HasCurrentWriteEvidence, "WebDAV write verification failed.");
            await vm.SaveAsync(default); Require(vm.LastSaveResult?.Success == true, "Connection was not saved.");
            project = ConfigService.CurrentConfig.BackupConfigs.First(c => c.Id == project.Id);
            var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(project);
            var version = (await runtime.Query.GetAllVersionsAsync(default)).OrderByDescending(v => v.CreatedAtUtc).First();
            var upload = await CloudSyncService.UploadVersionClosureAsync(project, version.VersionId);
            Require(upload.Complete, "Production upload failed.");
            var completed = runtime.CloudJobs.Read().Jobs.Single(j => j.Phase == CloudJobPhase.Succeeded);
            Require(completed.CompletedUtc is not null && completed.CompletedUtc >= completed.CreatedUtc, "Completion time missing.");
            using var status = new CloudSetupViewModel(); await status.InitializeAsync(new FolderRewind.History.Domain.HistoryConfigId(project.Id).Value);
            Require(status.ProjectId == project.Id && status.Jobs.Count > 0, "Normalized notification project ID was not resolved.");
            Require(!status.ConnectionCheckSummary.Contains(I18n.GetString("CloudStatus_CheckUnknown")), "Saved verification forgotten.");
            using var context = new RcloneExecutionContext(vm.Executable, vm.ConfigPath, vm.WorkingDirectory, vm.RemoteRoot, ConfigService.RcloneTasksDirectory);
            Require((await CloudRepositoryDisplayService.ReadAsync(context, runtime.ConfigId, default))?.ProjectName == project.Name, "Project display was not published.");
            await File.WriteAllTextAsync(Path.Combine(root, "webdav-offline"), "offline");
            await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "round two changed");
            await BackupService.BackupForSetupAsync(project, null);
            var next = (await runtime.Query.GetAllVersionsAsync(default)).OrderByDescending(v => v.CreatedAtUtc).First();
            var failed = await CloudSyncService.UploadVersionClosureAsync(project, next.VersionId);
            Require(!failed.Complete, "Offline upload reported success.");
            Require(runtime.CloudJobs.Read().Jobs.Any(j => j.Protection == CloudProtectionDisposition.Held), "Offline payload was released.");
            File.Delete(Path.Combine(root, "webdav-offline"));
            await File.WriteAllTextAsync(Path.Combine(root, "webdav-display-denied"), "deny optional metadata");
            await CloudSyncService.RetryCloudJobsAsync(project);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (runtime.CloudJobs.Read().Jobs.Any(j => j.Protection == CloudProtectionDisposition.Held) && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            Require(runtime.CloudJobs.Read().Jobs.All(j => j.Phase == CloudJobPhase.Succeeded), "Retry did not complete.");
            // Wait for the worker's optional publication attempt after the durable success fact.
            deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (!LogService.GetEntriesSnapshot().Select(LogService.FormatEntry).Any(s => s.Contains("Repository display:", StringComparison.Ordinal))
                && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            Require(LogService.GetEntriesSnapshot().Select(LogService.FormatEntry).Any(s => s.Contains("Repository display:", StringComparison.Ordinal)), "Optional metadata failure was not observed.");
            File.Delete(Path.Combine(root, "webdav-display-denied"));
            var rename = new ConfigSettingsDraftService(project); rename.Draft.Name = "Round two renamed";
            Require((await rename.CommitAsync()).Success, "Project rename failed.");
            project = ConfigService.CurrentConfig.BackupConfigs.First(c => c.Id == project.Id);
            Require((await CloudSyncService.UploadVersionClosureAsync(project, next.VersionId)).Complete, "Display retry failed.");
            Require((await CloudRepositoryDisplayService.ReadAsync(context, runtime.ConfigId, default))?.ProjectName == project.Name, "Renamed display was not updated.");
            using var recovery = new CloudSetupViewModel(); await recovery.InitializeAsync("");
            recovery.Executable = vm.Executable; recovery.ConfigPath = vm.ConfigPath; recovery.RemoteRoot = vm.RemoteRoot;
            await recovery.DiscoverRepositoriesAsync(default);
            Require(recovery.RecoveryRepositories.Any(s => s.Contains(project.Name)), "Independent recovery cannot identify the project.");
            recovery.SelectedRepositoryIndex = 0; await recovery.AnalyzeRepositoryAsync(default);
            Require(recovery.Versions.Count >= 2, "Cloud history is incomplete.");
            recovery.SelectedVersionIndex = 0;
            var confirmation = await recovery.PreviewRecoveryAsync(default);
            Require(confirmation.Preview.CanPrepare, "Independent recovery could not be prepared.");
            var destination = Path.Combine(root, "cloud-restored");
            await recovery.RestoreAnalyzedAsync(destination, null, confirmation);
            var restored = Directory.GetFiles(destination, "hello.txt", SearchOption.AllDirectories);
            Require(restored.Length == 1 && await File.ReadAllTextAsync(restored[0]) == "round two changed", "Recovered content differs from the uploaded version.");
            await recovery.RunCheckAsync(recovery.DiscoverRepositoriesAsync);
            Require(recovery.RecoveryRepositories.Any(s => s.Contains(project.Name)), "Refreshing a selected repository canceled its own discovery.");
        });
        await ConfigService.SaveAsync();
        return results.Any(r => JsonSerializer.Serialize(r).Contains("\"FAIL\"")) ? 1 : 0;
    }
}
#endif
