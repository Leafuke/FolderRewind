using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public sealed record BackupSetupCommitResult(IReadOnlyList<string> CreatedIds);

public static class BackupSetupCoordinator
{
    private static readonly SemaphoreSlim CommitGate = new(1, 1);

    // 发现更新保留原三方复核。本边界只提交全新项目，不修改既有规则。
    public static string ValidateNewProjects(IReadOnlyList<BackupConfig> projects)
    {
        var existing = ConfigService.CurrentConfig.BackupConfigs;
        var names = existing.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = existing.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var destinations = existing.Select(c => FolderNameConflictService.NormalizeDestinationPath(c.DestinationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var config in projects)
        {
            if (string.IsNullOrWhiteSpace(config.Name) || !names.Add(config.Name.Trim()) || !ids.Add(config.Id))
                return I18n.GetString("Setup_InvalidName");
            if (string.IsNullOrWhiteSpace(config.DestinationPath) || !Path.IsPathFullyQualified(config.DestinationPath)
                || !destinations.Add(FolderNameConflictService.NormalizeDestinationPath(config.DestinationPath)))
                return I18n.GetString("Setup_InvalidDestination");
            if (config.SourceFolders.Count == 0) return I18n.GetString("Setup_NoSources");
            var sourceError = ValidateSources(config, config.SourceFolders, []);
            if (sourceError.Length != 0) return sourceError;
        }
        return string.Empty;
    }

    internal static string ValidateSources(BackupConfig config, IEnumerable<ManagedFolder> sources, IEnumerable<ManagedFolder> retained)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in retained)
        {
            roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path)));
            if (BackupStoragePathService.TryResolveBackupStoragePaths(config.DestinationPath, source.DisplayName, source.Path,
                out var name, out _, out _)) storageNames.Add(name);
        }
        foreach (var source in sources)
        {
            if (!Path.IsPathFullyQualified(source.Path) || !Directory.Exists(source.Path)) return I18n.GetString("Setup_SourceUnavailable");
            if (BackupSourceRootSafetyPolicy.IsBroadRoot(source.Path) && source.SourceScope.Mode == BackupSourceScopeMode.All)
                return I18n.GetString("Setup_BroadRoot");
            if (!roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path)))
                || !BackupStoragePathService.TryResolveBackupStoragePaths(config.DestinationPath, source.DisplayName, source.Path,
                    out var storageName, out var archivePath, out var metadataPath)
                || !storageNames.Add(storageName)) return I18n.GetString("Setup_DuplicateSource");
            if (!BackupPathOverlapPolicy.Validate(source.Path, config.DestinationPath, archivePath, metadataPath).IsSafe)
                return I18n.GetString("Setup_PathOverlap");
        }
        return string.Empty;
    }

    internal static async Task AddSourcesAsync(BackupConfig config, string expectedSignature,
        IReadOnlyList<ManagedFolder> sources, CancellationToken token = default)
    {
        if (sources.Count == 0) return;
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, token);
        void RequireCurrent()
        {
            if (!ConfigService.CurrentConfig.BackupConfigs.Contains(config) || NativeHistoryConfigLease.Signature(config) != expectedSignature)
                throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
            ConfigMutationProtection.RequireWritable(config);
        }
        RequireCurrent();
        var error = ValidateSources(config, sources, config.SourceFolders);
        if (error.Length != 0) throw new InvalidOperationException(error);
        var draft = BackupConfigCloneService.CloneForRuntimeMutation(config, I18n.GetString("Common_Failed"));
        foreach (var source in sources) draft.SourceFolders.Add(source);
        foreach (var source in sources)
        {
            var resolved = await PluginV3BackupSourceResolver.ResolveAsync(draft, source, token);
            if (resolved.IsBoundaryBlocked) throw new InvalidOperationException(string.Join("; ", resolved.Diagnostics.Select(d => d.Code)));
        }
        token.ThrowIfCancellationRequested();
        RequireCurrent();
        error = ValidateSources(config, sources, config.SourceFolders);
        if (error.Length != 0) throw new InvalidOperationException(error);
        var previousRevision = config.ConfigRevision;
        await ConfigEditTransaction.ApplyAsync(() =>
        {
            foreach (var source in sources) config.SourceFolders.Add(source);
            config.ConfigRevision = Guid.NewGuid().ToString("N");
        }, () =>
        {
            foreach (var source in sources) config.SourceFolders.Remove(source);
            config.ConfigRevision = previousRevision;
        }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
    }

    internal static async Task<BackupSetupCommitResult> CommitAsync(IReadOnlyList<BackupConfig> projects,
        IReadOnlyDictionary<string, string>? passwords = null, CancellationToken token = default)
    {
        await CommitGate.WaitAsync(token);
        var newCredentials = new List<string>();
        try
        {
            var error = ValidateNewProjects(projects);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            foreach (var config in projects)
            {
                foreach (var source in config.SourceFolders)
                {
                    var resolved = await PluginV3BackupSourceResolver.ResolveAsync(config, source, token);
                    if (resolved.IsBoundaryBlocked) throw new InvalidOperationException(
                        string.Join("; ", resolved.Diagnostics.Select(d => d.Code)));
                }
                if (config.IsEncrypted)
                {
                    if (passwords is null || !passwords.TryGetValue(config.Id, out var password) || string.IsNullOrEmpty(password))
                        throw new InvalidOperationException(I18n.GetString("BackupService_MissingEncryptionPassword"));
                    newCredentials.Add(config.Id);
                    EncryptionService.StorePassword(config.Id, password);
                    if (!EncryptionService.VerifyPassword(config.Id, password)) throw new IOException(I18n.GetString("Common_Failed"));
                }
            }
            // 异步校验期间可能有其他入口写入，最终提交前再次检查。
            token.ThrowIfCancellationRequested();
            error = ValidateNewProjects(projects);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            var configs = ConfigService.CurrentConfig.BackupConfigs;
            await ConfigEditTransaction.ApplyAsync(
                () => { foreach (var config in projects) configs.Add(config); },
                () => { foreach (var config in projects) configs.Remove(config); },
                () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
            newCredentials.Clear();
            return new(projects.Select(c => c.Id).ToArray());
        }
        catch (ConfigEditRollbackException)
        {
            // 持久化结果不确定时保留可能仍被已排队快照引用的密码。
            newCredentials.Clear();
            throw;
        }
        finally
        {
            foreach (var id in newCredentials) EncryptionService.RemovePassword(id);
            CommitGate.Release();
        }
    }
}
