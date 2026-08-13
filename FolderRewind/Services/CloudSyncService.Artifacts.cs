using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class CloudSyncService
    {
        private const int ArtifactCloudSchemaVersion = 1;
        private static readonly JsonSerializerOptions ArtifactCloudJson = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private sealed record ArtifactCloudManifest(
            int SchemaVersion,
            string HistoryItemId,
            Guid RootArtifactId,
            string GraphRevision,
            ArtifactLedgerDocument Ledger,
            IReadOnlyDictionary<Guid, bool> FilePayloads);

        private static async Task<string?> UploadArtifactClosureAsync(
            BackupTask task,
            BackupConfig config,
            ManagedFolder folder,
            CloudSettings settings,
            CloudCommandContext context)
        {
            if (settings.CommandMode != CloudCommandMode.Rclone) return null;
            var history = HistoryService.GetHistoryForFolder(config, folder)
                .FirstOrDefault(value => StringComparer.OrdinalIgnoreCase.Equals(value.FileName, context.ArchiveFileName));
            if (history?.ArtifactRootId is not Guid rootId) return null;

            var store = new FileArtifactLedgerStore(config.DestinationPath);
            var ledger = await store.LoadAsync().ConfigureAwait(false);
            var historyRoot = ledger.HistoryRoots.SingleOrDefault(value =>
                StringComparer.Ordinal.Equals(value.HistoryItemId, history.Id)
                && value.RootArtifactId.Value == rootId);
            if (historyRoot is null) return "Artifact Cloud queue rejected an inconsistent History root.";
            var root = ledger.Artifacts.Single(value => value.ArtifactId == historyRoot.RootArtifactId);
            if (root.Format.OwnerId.Value == "folderrewind.core") return null;

            var reachable = ArtifactLedgerValidator.ComputeReachable(ledger, [history.Id]);
            var artifacts = ledger.Artifacts.Where(value => reachable.Contains(value.ArtifactId)).ToArray();
            var slice = new ArtifactLedgerDocument(
                ledger.SchemaVersion,
                ledger.Revision,
                artifacts,
                [historyRoot]);
            ArtifactLedgerValidator.Validate(slice);
            var kinds = artifacts.ToDictionary(
                value => value.ArtifactId.Value,
                value => File.Exists(ResolveArtifactPath(config.DestinationPath, value.ContentRelativePath)));
            var manifest = new ArtifactCloudManifest(
                ArtifactCloudSchemaVersion,
                history.Id,
                rootId,
                ledger.Revision.Value,
                slice,
                kinds);
            var temporary = Path.Combine(Path.GetTempPath(), "FolderRewind-artifact-cloud-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                var executable = ResolveRcloneExecutable(settings);
                var workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
                var remoteRoot = ArtifactRemoteRoot(settings, config, folder);
                foreach (var artifact in artifacts)
                {
                    var source = ResolveArtifactPath(config.DestinationPath, artifact.ContentRelativePath);
                    var payloadRoot = AppendRemotePath(remoteRoot, "payloads", artifact.ArtifactId.Value.ToString("N"));
                    var arguments = kinds[artifact.ArtifactId.Value]
                        ? BuildRcloneCopyToArguments(source, AppendRemotePath(payloadRoot, "payload"))
                        : BuildRcloneCopyArguments(source, payloadRoot);
                    var result = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        CreateDirectCommand(executable, workingDirectory, arguments),
                        "Uploading Artifact closure",
                        artifact.ArtifactId.Value.ToString("N")).ConfigureAwait(false);
                    if (!result.Success) return "Artifact Cloud payload upload failed: " + result.ErrorMessage;
                }

                var manifestPath = Path.Combine(temporary, "manifest.json");
                await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest, ArtifactCloudJson)).ConfigureAwait(false);
                var manifestResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    CreateDirectCommand(
                        executable,
                        workingDirectory,
                        BuildRcloneCopyToArguments(
                            manifestPath,
                            AppendRemotePath(remoteRoot, "manifests", history.Id + ".json"))),
                    "Committing Artifact Cloud manifest",
                    history.FileName).ConfigureAwait(false);
                return manifestResult.Success ? null : "Artifact Cloud manifest upload failed: " + manifestResult.ErrorMessage;
            }
            finally
            {
                try { Directory.Delete(temporary, recursive: true); } catch { }
            }
        }

        public static async Task<(bool Success, string Message)> EnsureArtifactClosureAvailableAsync(
            BackupConfig config,
            ManagedFolder folder,
            HistoryItem history,
            CancellationToken cancellationToken = default)
        {
            if (history.ArtifactRootId is not Guid expectedRoot) return (true, string.Empty);
            var store = new FileArtifactLedgerStore(config.DestinationPath);
            try
            {
                var local = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                var localRoot = local.HistoryRoots.SingleOrDefault(value => StringComparer.Ordinal.Equals(value.HistoryItemId, history.Id));
                if (localRoot?.RootArtifactId.Value == expectedRoot)
                {
                    var rootArtifact = local.Artifacts.Single(value => value.ArtifactId == localRoot.RootArtifactId);
                    if (rootArtifact.Format.OwnerId.Value == "folderrewind.core") return (true, string.Empty);
                    var reachable = ArtifactLedgerValidator.ComputeReachable(local, [history.Id]);
                    await store.VerifyArtifactsAsync(local, reachable, cancellationToken).ConfigureAwait(false);
                    return (true, string.Empty);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or KeyNotFoundException)
            {
                LogService.LogWarning("Local Artifact closure requires Cloud completion: " + ex.Message, nameof(CloudSyncService));
            }

            var settings = config.Cloud;
            if (ConfigService.CurrentConfig?.GlobalSettings?.AutoDownloadMissingCloudBackupsBeforeRestore != true
                || settings?.CommandMode != CloudCommandMode.Rclone
                || !CanUseManualCloudActions(config))
                return (false, "Artifact closure is unavailable locally and automatic Cloud completion is disabled.");

            var task = CreateTask("Download Artifact closure", DownloadTaskIconGlyph);
            await RunOnUIAsync(() => BackupService.ActiveTasks.Insert(0, task)).ConfigureAwait(false);
            await CommandSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            var temporary = Path.Combine(Path.GetTempPath(), "FolderRewind-artifact-cloud-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                var executable = ResolveRcloneExecutable(settings);
                var workingDirectory = settings.WorkingDirectory?.Trim() ?? string.Empty;
                var remoteRoot = ArtifactRemoteRoot(settings, config, folder);
                var manifestPath = Path.Combine(temporary, "manifest.json");
                var manifestResult = await ExecuteCommandWithRetryAsync(
                    task,
                    settings,
                    CreateDirectCommand(
                        executable,
                        workingDirectory,
                        BuildRcloneCopyToArguments(
                            AppendRemotePath(remoteRoot, "manifests", history.Id + ".json"),
                            manifestPath)),
                    "Downloading Artifact Cloud manifest",
                    history.FileName).ConfigureAwait(false);
                if (!manifestResult.Success) return (false, manifestResult.ErrorMessage);
                var manifest = JsonSerializer.Deserialize<ArtifactCloudManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                    ArtifactCloudJson) ?? throw new InvalidDataException("Artifact Cloud manifest is empty.");
                if (manifest.SchemaVersion != ArtifactCloudSchemaVersion
                    || !StringComparer.Ordinal.Equals(manifest.HistoryItemId, history.Id)
                    || manifest.RootArtifactId != expectedRoot
                    || !StringComparer.Ordinal.Equals(manifest.GraphRevision, history.ArtifactGraphRevision)
                    || manifest.Ledger.Revision.Value != manifest.GraphRevision)
                    throw new InvalidDataException("Artifact Cloud manifest does not match the requested History revision.");
                ArtifactLedgerValidator.Validate(manifest.Ledger);
                var root = manifest.Ledger.HistoryRoots.Single(value => StringComparer.Ordinal.Equals(value.HistoryItemId, history.Id));
                if (root.RootArtifactId.Value != expectedRoot) throw new InvalidDataException("Artifact Cloud root identity mismatch.");

                var payloads = new Dictionary<ArtifactId, CloudArtifactPayload>();
                foreach (var artifact in manifest.Ledger.Artifacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!manifest.FilePayloads.TryGetValue(artifact.ArtifactId.Value, out var isFile))
                        throw new InvalidDataException("Artifact Cloud payload kind is missing.");
                    var local = Path.Combine(temporary, "payloads", artifact.ArtifactId.Value.ToString("N"));
                    Directory.CreateDirectory(local);
                    var remote = AppendRemotePath(remoteRoot, "payloads", artifact.ArtifactId.Value.ToString("N"));
                    var arguments = isFile
                        ? BuildRcloneCopyToArguments(AppendRemotePath(remote, "payload"), Path.Combine(local, "payload"))
                        : BuildRcloneCopyArguments(remote, local);
                    var result = await ExecuteCommandWithRetryAsync(
                        task,
                        settings,
                        CreateDirectCommand(executable, workingDirectory, arguments),
                        "Downloading Artifact payload",
                        artifact.ArtifactId.Value.ToString("N")).ConfigureAwait(false);
                    if (!result.Success) return (false, result.ErrorMessage);
                    payloads.Add(
                        artifact.ArtifactId,
                        new CloudArtifactPayload(isFile ? Path.Combine(local, "payload") : local, isFile));
                }
                await store.ImportCloudClosureAsync(manifest.Ledger, payloads, cancellationToken).ConfigureAwait(false);
                var imported = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                var reachable = ArtifactLedgerValidator.ComputeReachable(imported, [history.Id]);
                await store.VerifyArtifactsAsync(imported, reachable, cancellationToken).ConfigureAwait(false);
                return (true, string.Empty);
            }
            finally
            {
                CommandSemaphore.Release();
                try { Directory.Delete(temporary, recursive: true); } catch { }
            }
        }

        private static string ArtifactRemoteRoot(
            CloudSettings settings,
            BackupConfig config,
            ManagedFolder folder)
            => AppendRemotePath(
                settings.RemoteBasePath ?? string.Empty,
                config.Name ?? string.Empty,
                folder.DisplayName ?? string.Empty,
                "_folderrewind",
                "artifacts");

        private static string ResolveArtifactPath(string destination, string relativePath)
        {
            var root = Path.GetFullPath(destination);
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artifact path escapes its backup repository.");
            return path;
        }
    }
}
