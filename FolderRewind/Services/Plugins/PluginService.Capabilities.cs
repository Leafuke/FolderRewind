using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.KnotLink;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ResourceLoader = FolderRewind.Services.AppResourceLoader;

namespace FolderRewind.Services.Plugins
{
    public static partial class PluginService
    {
        public static IReadOnlyList<IFolderRewindDiscoveryProvider> GetDiscoveryProviders()
        {
            if (!IsPluginSystemEnabled())
            {
                return Array.Empty<IFolderRewindDiscoveryProvider>();
            }

            Initialize();
            return GetEnabledLoadedPluginsSnapshot()
                .OfType<IFolderRewindDiscoveryProvider>()
                .OrderByDescending(provider => provider.Descriptor.Priority)
                .ToList();
        }

        public static IReadOnlyList<PluginBackupScopeDefinition> GetBackupScopeDefinitions(BackupConfig config)
        {
            if (config == null || !IsPluginSystemEnabled())
            {
                return Array.Empty<PluginBackupScopeDefinition>();
            }

            var result = new List<PluginBackupScopeDefinition>();
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                if (plugin is not IFolderRewindBackupScopeProvider provider)
                {
                    continue;
                }

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var definitions = provider.GetBackupScopeDefinitions(config, settings);
                    if (definitions == null)
                    {
                        continue;
                    }

                    foreach (var definition in definitions.Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id)))
                    {
                        result.Add(definition);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_GetSettingsDefinitionsFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return result;
        }

        public static Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
            BackupConfig config,
            ManagedFolder folder,
            CancellationToken cancellationToken)
        {
            if (!IsPluginSystemEnabled())
            {
                return Task.FromResult<IReadOnlyList<FolderDetailsSection>>(Array.Empty<FolderDetailsSection>());
            }

            var snapshot = GetEnabledLoadedPluginsSnapshot();

            return GetFolderDetailsSectionsFromPluginsAsync(
                snapshot,
                config,
                folder,
                cancellationToken);
        }

        public static async Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsFromPluginsAsync(
            IEnumerable<IFolderRewindPlugin> plugins,
            BackupConfig config,
            ManagedFolder folder,
            CancellationToken cancellationToken)
        {
            var sections = new List<FolderDetailsSection>();

            foreach (var plugin in plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (plugin is not IFolderRewindFolderDetailsProvider provider)
                {
                    LogService.LogInfo(
                        $"[PluginService] Plugin '{plugin.Manifest.Id}' does NOT implement IFolderRewindFolderDetailsProvider, skipping.",
                        nameof(PluginService));
                    continue;
                }

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var pluginSections = await provider.GetFolderDetailsSectionsAsync(
                        config,
                        folder,
                        settings,
                        cancellationToken).ConfigureAwait(false);

                    if (pluginSections != null)
                    {
                        LogService.LogInfo(
                            $"[PluginService] Plugin '{plugin.Manifest.Id}' returned {pluginSections.Count} section(s) with {pluginSections.Sum(s => s.Items?.Count ?? 0)} item(s).",
                            nameof(PluginService));
                        sections.AddRange(pluginSections);
                    }
                    else
                    {
                        LogService.LogInfo(
                            $"[PluginService] Plugin '{plugin.Manifest.Id}' returned null sections.",
                            nameof(PluginService));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogService.LogError(
                        $"[PluginService] Folder details provider failed: {plugin.Manifest.Id}: {ex.Message}",
                        nameof(PluginService),
                        ex);
                }
            }

            LogService.LogInfo(
                $"[PluginService] GetFolderDetailsSectionsFromPluginsAsync complete: {plugins.Count()} plugin(s) checked, {sections.Count} section(s) collected.",
                nameof(PluginService));

            return sections;
        }

        public static PluginBackupFilterConfigResolution ResolveConfigWithBackupFilterContributions(
            BackupConfig config,
            ManagedFolder folder)
        {
            static PluginBackupFilterConfigResolution Failed(
                BackupConfig source,
                string errorCode,
                string errorMessage)
                => new()
                {
                    Success = false,
                    EffectiveConfig = source,
                    Status = PluginBackupScopeResolutionStatus.Invalid,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage
                };

            if (config == null || folder == null)
            {
                return Failed(
                    config!,
                    "invalid_scope_context",
                    I18n.Format("PluginService_BackupScope_ContextIncomplete"));
            }

            var scope = config.BackupScope;
            if (scope == null || string.IsNullOrWhiteSpace(scope.PluginScopeId))
            {
                return new PluginBackupFilterConfigResolution
                {
                    Success = true,
                    EffectiveConfig = config,
                    Status = PluginBackupScopeResolutionStatus.NotApplicable
                };
            }

            if (!IsPluginSystemEnabled())
            {
                return Failed(
                    config,
                    "scope_plugin_system_disabled",
                    I18n.Format("PluginService_BackupScope_SystemDisabled"));
            }

            var scopeContext = new PluginBackupScopeContext
            {
                ScopeId = scope.PluginScopeId,
                Parameters = scope.Parameters == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(scope.Parameters, StringComparer.OrdinalIgnoreCase)
            };

            var claimants = new List<(IFolderRewindPlugin Plugin, IFolderRewindBackupScopeProvider Provider)>();
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                if (plugin is not IFolderRewindBackupScopeProvider provider)
                {
                    continue;
                }

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var definitions = provider.GetBackupScopeDefinitions(config, settings)
                        ?? Array.Empty<PluginBackupScopeDefinition>();
                    int matchingDefinitionCount = definitions.Count(definition =>
                        definition != null
                        && string.Equals(
                            definition.Id,
                            scope.PluginScopeId,
                            StringComparison.OrdinalIgnoreCase));
                    if (matchingDefinitionCount > 1)
                    {
                        return Failed(
                            config,
                            "scope_provider_duplicate_declaration",
                            I18n.Format(
                                "PluginService_BackupScope_DuplicateDeclaration",
                                plugin.Manifest.Id,
                                scope.PluginScopeId));
                    }

                    if (matchingDefinitionCount == 1)
                    {
                        claimants.Add((plugin, provider));
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(
                        $"[PluginService] Backup scope discovery failed for '{plugin.Manifest.Id}': {ex.Message}",
                        nameof(PluginService),
                        ex);
                    return Failed(
                        config,
                        "scope_provider_exception",
                        I18n.Format(
                            "PluginService_BackupScope_DiscoveryFailed",
                            plugin.Manifest.Id,
                            ex.Message));
                }
            }

            if (claimants.Count == 0)
            {
                return Failed(
                    config,
                    "scope_provider_missing",
                    I18n.Format("PluginService_BackupScope_ProviderMissing", scope.PluginScopeId));
            }

            if (claimants.Count > 1)
            {
                return Failed(
                    config,
                    "scope_provider_ambiguous",
                    I18n.Format("PluginService_BackupScope_ProviderAmbiguous", scope.PluginScopeId));
            }

            var claimant = claimants[0];
            PluginBackupScopeResolution resolution;
            try
            {
                var settings = GetPluginSettings(claimant.Plugin.Manifest.Id);
                resolution = claimant.Provider.ResolveBackupScope(
                    config,
                    folder,
                    scopeContext,
                    settings);
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    $"[PluginService] Backup scope resolution failed for '{claimant.Plugin.Manifest.Id}': {ex.Message}",
                    nameof(PluginService),
                    ex);
                return Failed(
                    config,
                    "scope_provider_exception",
                    I18n.Format(
                        "PluginService_BackupScope_ResolutionFailed",
                        claimant.Plugin.Manifest.Id,
                        ex.Message));
            }

            if (resolution == null)
            {
                return Failed(
                    config,
                    "scope_resolution_missing",
                    I18n.Format("PluginService_BackupScope_ResolutionMissing"));
            }

            if (resolution.Status == PluginBackupScopeResolutionStatus.Invalid)
            {
                return Failed(
                    config,
                    string.IsNullOrWhiteSpace(resolution.ErrorCode)
                        ? "invalid_backup_scope"
                        : resolution.ErrorCode,
                    string.IsNullOrWhiteSpace(resolution.ErrorMessage)
                        ? I18n.Format("PluginService_BackupScope_Invalid")
                        : resolution.ErrorMessage);
            }

            if (resolution.Status == PluginBackupScopeResolutionStatus.NotApplicable)
            {
                return new PluginBackupFilterConfigResolution
                {
                    Success = true,
                    EffectiveConfig = config,
                    Status = PluginBackupScopeResolutionStatus.NotApplicable
                };
            }

            var contribution = resolution.Contribution;
            bool hasWhitelist = contribution?.BackupWhitelist?.Any(rule => !string.IsNullOrWhiteSpace(rule)) == true;
            bool hasBlacklist = contribution?.BackupBlacklist?.Any(rule => !string.IsNullOrWhiteSpace(rule)) == true;
            if (contribution == null || (!hasWhitelist && !hasBlacklist))
            {
                return Failed(
                    config,
                    "scope_contribution_empty",
                    I18n.Format("PluginService_BackupScope_ContributionEmpty"));
            }

            var clone = CloneBackupConfigForRuntimeFilters(config);
            clone.Filters ??= new FilterSettings();
            clone.Filters.Blacklist ??= new ObservableCollection<string>();
            clone.Filters.BackupWhitelist ??= new ObservableCollection<string>();

            if (contribution.UseWhitelistMode || hasWhitelist)
            {
                clone.Filters.BackupFilterMode = BackupFilterMode.Whitelist;
                if (resolution.MergeMode == PluginBackupRuleMergeMode.Replace)
                {
                    clone.Filters.BackupWhitelist.Clear();
                }
            }
            else if (resolution.MergeMode == PluginBackupRuleMergeMode.Replace)
            {
                clone.Filters.Blacklist.Clear();
            }

            if (hasWhitelist)
            {
                foreach (var rule in contribution.BackupWhitelist!.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    BackupFilterRulePolicy.AddDistinct(clone.Filters.BackupWhitelist, rule);
                }
            }

            if (hasBlacklist && clone.Filters.BackupFilterMode != BackupFilterMode.Whitelist)
            {
                foreach (var rule in contribution.BackupBlacklist!.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    BackupFilterRulePolicy.AddDistinct(clone.Filters.Blacklist, rule);
                }
            }

            return new PluginBackupFilterConfigResolution
            {
                Success = true,
                EffectiveConfig = clone,
                Status = PluginBackupScopeResolutionStatus.Applied
            };
        }

        public static PluginBackupScopeValidationResult ValidateBackupScope(BackupConfig config)
        {
            if (config?.BackupScope == null
                || string.IsNullOrWhiteSpace(config.BackupScope.PluginScopeId))
            {
                return new PluginBackupScopeValidationResult { Success = true };
            }

            int appliedCount = 0;
            foreach (var folder in config.SourceFolders ?? new ObservableCollection<ManagedFolder>())
            {
                if (folder == null)
                {
                    continue;
                }

                var resolution = ResolveConfigWithBackupFilterContributions(config, folder);
                if (!resolution.Success)
                {
                    return new PluginBackupScopeValidationResult
                    {
                        Success = false,
                        ErrorCode = resolution.ErrorCode,
                        ErrorMessage = resolution.ErrorMessage
                    };
                }

                if (!BackupService.TryValidateBackupFilterRules(
                        resolution.EffectiveConfig.Filters,
                        out string filterError))
                {
                    return new PluginBackupScopeValidationResult
                    {
                        Success = false,
                        ErrorCode = "invalid_scope_filter_rule",
                        ErrorMessage = filterError
                    };
                }

                if (resolution.Status == PluginBackupScopeResolutionStatus.Applied)
                {
                    appliedCount++;
                }
            }

            return appliedCount > 0
                ? new PluginBackupScopeValidationResult { Success = true }
                : new PluginBackupScopeValidationResult
                {
                    Success = false,
                    ErrorCode = "scope_not_applicable",
                    ErrorMessage = I18n.Format("PluginService_BackupScope_NotApplicable")
                };
        }

        private static BackupConfig CloneBackupConfigForRuntimeFilters(BackupConfig source)
        {
            return BackupConfigCloneService.CloneForRuntimeMutation(
                source,
                "Failed to clone backup config for plugin filters.");
        }

    }
}
