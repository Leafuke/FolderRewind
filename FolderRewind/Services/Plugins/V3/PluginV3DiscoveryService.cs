using FolderRewind.Models;
using FolderRewind.History.Application;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>
/// v3 Discovery 的 Host 适配层。插件只产生候选草稿；稳定 Config/Folder 身份、
/// 重复检测、默认目录和原子持久化全部由 Host 决定。
/// </summary>
public static class PluginV3DiscoveryService
{
    public static async Task<IReadOnlyList<ManagedFolder>> DiscoverFoldersAsync(
        BackupConfig config,
        string userRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(userRoot)) return Array.Empty<ManagedFolder>();

        var pluginId = new PluginId(config.Kind.OwnerId);
        var coordinator = new PluginDiscoveryCoordinator(
            PluginV3RuntimeService.Runtime,
            NoCommitDiscoveryDraftStore.Instance);
        var run = await coordinator.DiscoverAsync(
            pluginId,
            new FolderRewind.Plugin.Abstractions.DiscoveryRequest([userRoot]),
            autoCreateConfigs: false,
            cancellationToken).ConfigureAwait(false);
        var kind = PluginV3ModelMapper.ToKind(config);
        return run.Discovery.Candidates
            .SelectMany(candidate => candidate.ConfigDrafts)
            .Where(draft => draft.Kind == kind)
            .SelectMany(draft => draft.Folders)
            .Select(ToManagedFolder)
            .ToArray();
    }

    public static async Task RunStartupAutoCreateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var pluginId in PluginV3RuntimeService.GetActivePlugins())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunAutoCreateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task RunAutoCreateAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAutoCreateEnabled(pluginId)) return;
        using var capability = PluginV3RuntimeService.Runtime.TryAcquire<IDiscoveryCapability>(
            pluginId,
            cancellationToken);
        if (capability is null) return;

        var coordinator = new PluginDiscoveryCoordinator(
            PluginV3RuntimeService.Runtime,
            new HostDiscoveryDraftStore());
        try
        {
            await coordinator.DiscoverAsync(
                pluginId,
                new FolderRewind.Plugin.Abstractions.DiscoveryRequest(
                    await PluginV3DiscoveryRootService.BuildDefaultRootsAsync(pluginId).ConfigureAwait(false)),
                autoCreateConfigs: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.LogError(
                $"v3 discovery failed for '{pluginId.Value}': {ex.Message}",
                "PluginV3Discovery",
                ex);
        }
    }

    private static bool IsAutoCreateEnabled(PluginId pluginId)
        => ConfigService.CurrentConfig.GlobalSettings.Plugins.TypedSettings
               .TryGetValue(pluginId.Value, out var settings)
           && settings.TryGetValue("AutoCreateConfigs", out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();

    private static ManagedFolder ToManagedFolder(FolderDraft draft)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Path = draft.Path,
            DisplayName = draft.DisplayName,
            CoverImagePath = ResolveDefaultCoverImagePath(draft.Path),
            ProviderStates = draft.ProviderStates.ToDictionary(
                pair => pair.Key.Value,
                pair => new ProviderStatePayload
                {
                    SchemaVersion = pair.Value.SchemaVersion,
                    Data = pair.Value.Data.Clone()
                },
                StringComparer.OrdinalIgnoreCase)
        };

    private static string ResolveDefaultCoverImagePath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return string.Empty;
        try
        {
            var iconPath = Path.Combine(folderPath, "icon.png");
            return File.Exists(iconPath) ? iconPath : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class NoCommitDiscoveryDraftStore : IDiscoveryDraftStore
    {
        public static NoCommitDiscoveryDraftStore Instance { get; } = new();

        public ValueTask CommitAsync(DiscoveryDraftCommit commit, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The preview discovery store cannot commit drafts.");
    }

    private sealed class HostDiscoveryDraftStore : IDiscoveryDraftStore
    {
        public async ValueTask CommitAsync(
            DiscoveryDraftCommit commit,
            CancellationToken cancellationToken)
        {
            var manifest = PluginV3RuntimeService.FindManifest(commit.PluginId)
                ?? throw new InvalidOperationException("Discovery owner Manifest is unavailable.");
            var knownKinds = manifest.ConfigKinds.Select(value => value.Kind).ToHashSet();
            var candidates = commit.Candidates.SelectMany(value => value.ConfigDrafts).ToArray();
            if (candidates.Any(draft => !knownKinds.Contains(draft.Kind)))
                throw new InvalidOperationException("A discovery draft declared an unknown Config Kind.");

            string? error = null;
            List<BackupConfig> additions = [];
            await UiDispatcherService.RunOnUiAsync(() =>
            {
                // 重复检测、稳定身份分配、集合写入和保存必须位于同一个 UI 临界区，
                // 避免发现期间用户编辑配置造成 TOCTOU 或跨线程 ObservableCollection 访问。
                additions = BuildAdditions(commit);
                if (additions.Count == 0) return;
                foreach (var config in additions) ConfigService.CurrentConfig.BackupConfigs.Add(config);
                var save = ConfigService.SaveWithResult(publishSavedEvent: false);
                if (!save.Success)
                {
                    foreach (var config in additions) ConfigService.CurrentConfig.BackupConfigs.Remove(config);
                    error = save.ErrorMessage;
                    return;
                }
                ConfigService.PublishSaved();
            }).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
                throw new IOException(error);
            foreach (var config in additions)
                _ = await NativeHistoryCoreGateway.EnsureReadyAsync(config, cancellationToken).ConfigureAwait(false);
        }

        private static List<BackupConfig> BuildAdditions(DiscoveryDraftCommit commit)
        {
            var additions = new List<BackupConfig>();
            foreach (var candidate in commit.Candidates)
            foreach (var draft in candidate.ConfigDrafts)
            {
                if (draft.Folders.All(folder => IsAlreadyManaged(draft.Kind, folder.Path))) continue;
                var name = UniqueConfigName(draft.SuggestedName, additions);
                var config = new BackupConfig
                {
                    Name = name,
                    DestinationPath = ConfigService.BuildDefaultDestinationPath(name),
                    Kind = new ConfigKindReference
                    {
                        OwnerId = draft.Kind.OwnerId.Value,
                        KindId = draft.Kind.KindId
                    },
                    RequiredPluginId = commit.PluginId.Value,
                    HostOrigin = new HostConfigOrigin
                    {
                        DiscoveryProviderId = commit.ProviderId.Value,
                        DiscoveryCandidateId = candidate.CandidateId
                    },
                    ProviderStates = draft.ProviderStates.ToDictionary(
                        pair => pair.Key.Value,
                        pair => new ProviderStatePayload
                        {
                            SchemaVersion = pair.Value.SchemaVersion,
                            Data = pair.Value.Data.Clone()
                        },
                        StringComparer.OrdinalIgnoreCase)
                };
                foreach (var folder in draft.Folders.Where(folder => !IsAlreadyManaged(draft.Kind, folder.Path)))
                    config.SourceFolders.Add(ToManagedFolder(folder));
                if (config.SourceFolders.Count > 0) additions.Add(config);
            }
            return additions;
        }

        private static bool IsAlreadyManaged(ConfigKindRef kind, string path)
            => ConfigService.CurrentConfig.BackupConfigs.Any(config =>
                PluginV3ModelMapper.ToKind(config) == kind
                && config.SourceFolders.Any(folder => PathsEqual(folder.Path, path)));

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string UniqueConfigName(string requested, IEnumerable<BackupConfig> pending)
        {
            var baseName = string.IsNullOrWhiteSpace(requested) ? "Discovered configuration" : requested.Trim();
            var names = ConfigService.CurrentConfig.BackupConfigs.Select(value => value.Name)
                .Concat(pending.Select(value => value.Name))
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var name = baseName;
            var suffix = 2;
            while (!names.Add(name)) name = $"{baseName} ({suffix++})";
            return name;
        }
    }
}
