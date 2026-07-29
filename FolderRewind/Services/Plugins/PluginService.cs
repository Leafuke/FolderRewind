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
    /// <summary>
    /// 插件服务：安装/卸载、扫描、加载、启停与对外调用。
    /// 
    /// 安全/鲁棒性约束：
    /// - 插件目录只读写在 AppData 下，避免污染安装目录；
    /// - 读取 manifest 与加载插件时尽量捕获异常，确保主程序不崩；
    /// - 插件被禁用时不调用其逻辑（但为了简单，仍可能已被加载）。
    /// </summary>
    public static class PluginService
    {
        private const string ManifestFileName = "manifest.json";
        private const string PendingDeleteFileName = ".pending_delete";
        private const string PendingUpdateFileName = ".pending_update";

        private static readonly ResourceLoader _rl = ResourceLoader.GetForViewIndependentUse();

        private static readonly object _lock = new();
        private static bool _initialized;

        private static readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ObservableCollection<InstalledPluginInfo> _installed = new();
        private static readonly SemaphoreSlim _configAugmentationLock = new(1, 1);

        public static ReadOnlyObservableCollection<InstalledPluginInfo> InstalledPlugins { get; } = new(_installed);

        public static string PluginRootDirectory => Path.Combine(GetWritableAppDataDir(), "FolderRewind", "plugins");

        /// <summary>
        /// 获取当前 Host 版本号
        /// </summary>
        public static string GetHostVersion()
        {
            var version = AppRuntimeInfo.GetApplicationVersion();
            return version == null
                ? "1.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        /// <summary>
        /// 检查版本兼容性
        /// </summary>
        private static bool IsVersionCompatible(string? minHostVersion, out string? incompatibleReason)
        {
            incompatibleReason = null;
            if (string.IsNullOrWhiteSpace(minHostVersion)) return true;

            try
            {
                var hostVersion = Version.Parse(GetHostVersion());
                var minVersion = Version.Parse(minHostVersion);

                if (hostVersion < minVersion)
                {
                    incompatibleReason = string.Format(_rl.GetString("PluginService_IncompatibleHostVersion"), minHostVersion, GetHostVersion());
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                incompatibleReason = string.Format(_rl.GetString("PluginService_VersionParseFailed"), ex.Message);
                return false;
            }
        }

        public static void Initialize()
        {
            if (_initialized) return;

            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    Directory.CreateDirectory(PluginRootDirectory);

                    // 清理上次未能删除的插件
                    CleanPendingDeletions();

                    // 应用上次未完成的插件更新
                    ApplyPendingUpdates();
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_CreatePluginDirFailed", ex.Message), "PluginService", ex);
                }

                // 扫描安装清单（不一定加载插件）
                RefreshInstalledList();

                // 根据总开关/启用状态加载
                TryLoadEnabledPlugins();

                // 注册已启用插件的热键定义（窗口就绪后 HotkeyManager 会自动应用）
                TryRegisterPluginHotkeysForEnabled();

                _initialized = true;

                // 异步检查更新（不阻塞初始化）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000); // 延迟检查，避免启动时网络请求过多
                        await CheckAllPluginUpdatesAsync();
                    }
                    catch { }
                });
            }
        }

        private static void TryRegisterPluginHotkeysForEnabled()
        {
            if (!IsPluginSystemEnabled()) return;

            lock (_lock)
            {
                foreach (var kv in _loaded)
                {
                    var loaded = kv.Value;
                    if (loaded?.Instance == null) continue;
                    if (!GetPluginEnabled(loaded.Manifest.Id)) continue;

                    HotkeyManager.RegisterPluginHotkeys(loaded.Manifest, loaded.Instance);
                }
            }
        }

        public static void RefreshInstalledList()
        {
            lock (_lock)
            {
                _installed.Clear();

                try
                {
                    if (!Directory.Exists(PluginRootDirectory))
                    {
                        Directory.CreateDirectory(PluginRootDirectory);
                    }

                    foreach (var dir in Directory.EnumerateDirectories(PluginRootDirectory))
                    {
                        var info = ReadInstalledPluginInfo(dir);
                        if (info != null)
                        {
                            _installed.Add(info);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_ScanPluginsFailed", ex.Message), "PluginService", ex);
                }
            }
        }

        /// <summary>
        /// 手动刷新：用于 UI 中点击“刷新插件列表”。
        /// </summary>
        public static void RefreshAndLoadEnabled()
        {
            RefreshInstalledList();
            TryLoadEnabledPlugins();
            TryRegisterPluginHotkeysForEnabled();

            try { HotkeyManager.ApplyBindingsToUiAndNative(); } catch { }
        }

        /// <summary>
        /// KnotLink v2 commands are offered to plugins before built-in handlers.
        /// </summary>
        public static async Task<(bool Handled, string Response)> TryHandleParameterizedKnotLinkCommandAsync(KnotLinkCommandContext context)
        {
            if (!IsPluginSystemEnabled()) return (false, string.Empty);
            if (context == null || string.IsNullOrWhiteSpace(context.Command)) return (false, string.Empty);

            var request = context.Request;
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                if (plugin is not IFolderRewindParameterizedKnotLinkCommandHandler handler) continue;

                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var ctx = PluginHostContext.CreateForCurrentApp(plugin.Manifest.Id, plugin.Manifest.Name);
                    using var scope = KnotLinkService.PushCommandContext(context);
                    var result = await handler.TryHandleParameterizedKnotLinkCommandAsync(
                        request,
                        settings,
                        ctx).ConfigureAwait(false);

                    if (result?.Handled == true)
                    {
                        return (true, string.IsNullOrWhiteSpace(result.Response) ? "OK:" : result.Response!);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_KnotLinkCommandFailed", plugin.Manifest.Id, request.Command, ex.Message), "PluginService", ex);
                }
            }

            return (false, string.Empty);
        }

        internal static IReadOnlyList<(string PluginId, PluginKnotLinkCapabilityContribution Contribution)>
            GetKnotLinkCapabilityContributions()
        {
            var result = new List<(string, PluginKnotLinkCapabilityContribution)>();
            if (!IsPluginSystemEnabled()) return result;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot().OrderBy(item => item.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (plugin is not IFolderRewindKnotLinkCapabilityProvider provider) continue;

                try
                {
                    result.Add((plugin.Manifest.Id, provider.GetKnotLinkCapabilities() ?? new PluginKnotLinkCapabilityContribution()));
                }
                catch (Exception ex)
                {
                    LogService.LogError(
                        $"Plugin '{plugin.Manifest.Id}' failed to declare KnotLink capabilities: {ex.Message}",
                        "PluginService",
                        ex);
                }
            }

            return result;
        }

        public static bool IsPluginSystemEnabled()
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            return settings?.Enabled == true;
        }

        public static void SetPluginSystemEnabled(bool enabled)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;

            settings.Enabled = enabled;
            ConfigService.Save();

            if (enabled)
            {
                TryLoadEnabledPlugins();
            }
        }

        public static bool GetPluginEnabled(string pluginId)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return false;

            if (settings.PluginEnabled.TryGetValue(pluginId, out var enabled)) return enabled;
            return false;
        }

        public static void SetPluginEnabled(string pluginId, bool enabled)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;

            settings.PluginEnabled[pluginId] = enabled;
            ConfigService.Save();

            // 设计选择：启用可尝试立即加载；禁用仅停止调用（不强制卸载）。
            if (enabled && IsPluginSystemEnabled())
            {
                TryLoadPlugin(pluginId);
            }

            // 热键：插件启停后刷新绑定
            try
            {
                if (enabled && _loaded.TryGetValue(pluginId, out var loaded) && loaded != null)
                {
                    HotkeyManager.RegisterPluginHotkeys(loaded.Manifest, loaded.Instance);
                }
                HotkeyManager.ApplyBindingsToUiAndNative();
            }
            catch
            {
            }

            // 同步 UI 信息
            var item = _installed.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (item != null) item.IsEnabled = enabled;
        }

        public static IReadOnlyDictionary<string, string> GetPluginSettings(string pluginId)
        {
            var plugins = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (plugins == null) return new Dictionary<string, string>();

            if (plugins.PluginSettings.TryGetValue(pluginId, out var dict) && dict != null)
            {
                return dict;
            }

            return new Dictionary<string, string>();
        }

        public static PluginSettingsSaveResult SavePluginSettings(string pluginId, IReadOnlyDictionary<string, string> values)
        {
            var plugins = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (plugins == null)
            {
                return new PluginSettingsSaveResult();
            }

            var previous = new Dictionary<string, string>(GetPluginSettings(pluginId), StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                next[pair.Key] = pair.Value ?? string.Empty;
            }

            plugins.PluginSettings[pluginId] = next;
            ConfigService.Save();

            return new PluginSettingsSaveResult
            {
                PreviousSettings = previous,
                CurrentSettings = new Dictionary<string, string>(next, StringComparer.OrdinalIgnoreCase)
            };
        }

        public static async Task TryRunConfigAugmentationForSettingsChangeAsync(
            string pluginId,
            IReadOnlyDictionary<string, string> previousSettings,
            IReadOnlyDictionary<string, string> currentSettings)
        {
            if (string.IsNullOrWhiteSpace(pluginId) || !IsPluginSystemEnabled())
            {
                return;
            }

            IFolderRewindConfigAugmenter? augmenter = null;

            lock (_lock)
            {
                if (_loaded.TryGetValue(pluginId, out var loaded) && loaded.Instance is IFolderRewindConfigAugmenter typedAugmenter)
                {
                    augmenter = typedAugmenter;
                }
            }

            if (augmenter == null)
            {
                return;
            }

            bool shouldAugment;
            try
            {
                shouldAugment = augmenter.ShouldAugmentAfterSettingsChange(previousSettings, currentSettings);
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    $"[PluginService] Config augmentation settings trigger failed: {pluginId}: {ex.Message}",
                    "PluginService",
                    ex);
                return;
            }

            if (!shouldAugment)
            {
                return;
            }

            await RunConfigAugmentationAsync(
                PluginConfigAugmentationReason.SettingsEnabled,
                new[] { pluginId }).ConfigureAwait(false);
        }

        public static void SetPluginSetting(string pluginId, string key, string value)
        {
            var plugins = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (plugins == null) return;

            if (!plugins.PluginSettings.TryGetValue(pluginId, out var dict) || dict == null)
            {
                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                plugins.PluginSettings[pluginId] = dict;
            }

            dict[key] = value ?? string.Empty;
            ConfigService.Save();
        }

        /// <summary>
        /// 当用户在 UI 中修改插件设置时，尝试让已加载插件立即重新读取设置。
        /// </summary>
        public static void TryReinitialize(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return;
            if (!IsPluginSystemEnabled()) return;

            lock (_lock)
            {
                if (!_loaded.TryGetValue(pluginId, out var loaded) || loaded.Instance == null) return;
                try
                {
                    var settings = GetPluginSettings(pluginId);
                    loaded.Instance.Initialize(settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_ReinitializeFailed", pluginId, ex.Message), "PluginService", ex);
                }
            }
        }

        public static IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions(string pluginId)
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<PluginSettingDefinition>();

            if (_loaded.TryGetValue(pluginId, out var loaded) && loaded.Instance != null)
            {
                try
                {
                    // GetSettingsDefinitions 的返回类型已经是 IReadOnlyList，无需再 ToList。
                    return loaded.Instance.GetSettingsDefinitions() ?? Array.Empty<PluginSettingDefinition>();
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_GetSettingsDefinitionsFailed", pluginId, ex.Message), "PluginService", ex);
                }
            }

            return Array.Empty<PluginSettingDefinition>();
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
                    AddDistinctRule(clone.Filters.BackupWhitelist, rule);
                }
            }

            if (hasBlacklist && clone.Filters.BackupFilterMode != BackupFilterMode.Whitelist)
            {
                foreach (var rule in contribution.BackupBlacklist!.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    AddDistinctRule(clone.Filters.Blacklist, rule);
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

        private static void AddDistinctRule(ObservableCollection<string> rules, string rule)
        {
            var trimmed = rule.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return;
            }

            if (rules.Any(existing => string.Equals(existing?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            rules.Add(trimmed);
        }

        public static string? InvokeBeforeBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions? invocationOptions = null)
        {
            if (!IsPluginSystemEnabled()) return null;

            invocationOptions ??= BackupInvocationOptions.Default;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var newPath = plugin is IFolderRewindBackupPreparationProvider preparationProvider
                        ? preparationProvider.OnBeforeBackupFolder(config, folder, invocationOptions, settings)
                        : plugin.OnBeforeBackupFolder(config, folder, settings);
                    if (!string.IsNullOrWhiteSpace(newPath))
                    {
                        // 允许多个插件串联修改路径：使用最后一个返回的路径
                        folder = new ManagedFolder { Path = newPath, DisplayName = folder.DisplayName, Description = folder.Description, IsFavorite = folder.IsFavorite, CoverImagePath = folder.CoverImagePath };
                        return newPath;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_BeforeBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return null;
        }

        public static void InvokeAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName)
        {
            if (!IsPluginSystemEnabled()) return;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    plugin.OnAfterBackupFolder(config, folder, success, generatedArchiveFileName, settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_AfterBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }
        }

        /// <summary>
        /// 还原前钩子：调用所有已启用插件的 OnBeforeRestoreFolder。
        /// 返回每个插件的 (pluginId, state) 列表，供 InvokeAfterRestoreFolder 使用。
        /// </summary>
        public static List<(string PluginId, IFolderRewindPlugin Plugin, object? State)> InvokeBeforeRestoreFolder(
            BackupConfig config, ManagedFolder folder, string archiveFileName)
        {
            var results = new List<(string, IFolderRewindPlugin, object?)>();
            if (!IsPluginSystemEnabled()) return results;

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var state = plugin.OnBeforeRestoreFolder(config, folder, archiveFileName, settings);
                    results.Add((plugin.Manifest.Id, plugin, state));
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_BeforeRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                    results.Add((plugin.Manifest.Id, plugin, null));
                }
            }

            return results;
        }

        /// <summary>
        /// 还原后钩子：调用所有已启用插件的 OnAfterRestoreFolder。
        /// pluginStates 为 InvokeBeforeRestoreFolder 的返回值。
        /// </summary>
        public static void InvokeAfterRestoreFolder(
            BackupConfig config, ManagedFolder folder, bool success, string archiveFileName,
            List<(string PluginId, IFolderRewindPlugin Plugin, object? State)>? pluginStates)
        {
            if (!IsPluginSystemEnabled()) return;
            if (pluginStates == null || pluginStates.Count == 0) return;

            foreach (var (pluginId, plugin, state) in pluginStates)
            {
                try
                {
                    var settings = GetPluginSettings(pluginId);
                    plugin.OnAfterRestoreFolder(config, folder, success, archiveFileName, state, settings);
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_AfterRestoreFailed", pluginId, ex.Message), "PluginService", ex);
                }
            }
        }

        public static IReadOnlyList<ManagedFolder> InvokeDiscoverManagedFolders(string selectedRootPath)
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<ManagedFolder>();
            if (string.IsNullOrWhiteSpace(selectedRootPath)) return Array.Empty<ManagedFolder>();

            var results = new List<ManagedFolder>();
            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var discovered = plugin.TryDiscoverManagedFolders(selectedRootPath, settings);
                    if (discovered != null && discovered.Count > 0)
                    {
                        results.AddRange(discovered);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_DiscoverManagedFoldersFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return results;
        }

        /// <summary>
        /// 获取所有已加载插件支持的配置类型
        /// </summary>
        public static IReadOnlyList<string> GetAllSupportedConfigTypes()
        {
            if (!IsPluginSystemEnabled()) return Array.Empty<string>();

            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Default" };

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    var pluginTypes = plugin.GetSupportedConfigTypes();
                    if (pluginTypes != null)
                    {
                        foreach (var t in pluginTypes)
                        {
                            if (!string.IsNullOrWhiteSpace(t))
                                types.Add(t);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_GetSupportedConfigTypesFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return types.ToList();
        }

        public static async Task<PluginConfigAugmentationRunResult> RunConfigAugmentationAsync(
            PluginConfigAugmentationReason reason,
            IEnumerable<string>? pluginIds = null)
        {
            if (!IsPluginSystemEnabled())
            {
                return new PluginConfigAugmentationRunResult();
            }

            await _configAugmentationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var currentConfig = ConfigService.CurrentConfig;
                var liveConfigs = currentConfig?.BackupConfigs?.ToList() ?? new List<BackupConfig>();
                if (liveConfigs.Count == 0)
                {
                    return new PluginConfigAugmentationRunResult();
                }

                var configSnapshot = liveConfigs
                    .Select(CloneConfigForAugmentationSnapshot)
                    .ToList();

                HashSet<string>? pluginFilter = pluginIds == null
                    ? null
                    : new HashSet<string>(pluginIds, StringComparer.OrdinalIgnoreCase);

                var pendingItems = new List<(string PluginId, PluginConfigAugmentationItem Item)>();

                foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
                {
                    if (plugin is not IFolderRewindConfigAugmenter augmenter)
                    {
                        continue;
                    }

                    if (pluginFilter != null && !pluginFilter.Contains(plugin.Manifest.Id))
                    {
                        continue;
                    }

                    try
                    {
                        var result = augmenter.AugmentConfigs(
                            new PluginConfigAugmentationRequest
                            {
                                Reason = reason,
                                Configs = configSnapshot
                            },
                            GetPluginSettings(plugin.Manifest.Id));

                        if (!result.Handled || result.Items == null || result.Items.Count == 0)
                        {
                            continue;
                        }

                        foreach (var item in result.Items.Where(static entry => entry != null && !string.IsNullOrWhiteSpace(entry.ConfigId)))
                        {
                            pendingItems.Add((plugin.Manifest.Id, item));
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(
                            $"[PluginService] Config augmentation failed: {plugin.Manifest.Id}: {ex.Message}",
                            "PluginService",
                            ex);
                    }
                }

                if (pendingItems.Count == 0)
                {
                    return new PluginConfigAugmentationRunResult();
                }

                var addedByPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var touchedConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await UiDispatcherService.RunOnUiAsync(() =>
                {
                    foreach (var (pluginId, item) in pendingItems)
                    {
                        var targetConfig = currentConfig?.BackupConfigs?
                            .FirstOrDefault(config => string.Equals(config.Id, item.ConfigId, StringComparison.OrdinalIgnoreCase));
                        if (targetConfig == null)
                        {
                            continue;
                        }

                        int addedCount = TryApplyAugmentationItem(targetConfig, item);
                        if (addedCount <= 0)
                        {
                            continue;
                        }

                        touchedConfigs.Add(targetConfig.Id);
                        addedByPlugin[pluginId] = addedByPlugin.TryGetValue(pluginId, out var currentAdded)
                            ? currentAdded + addedCount
                            : addedCount;
                    }

                    if (touchedConfigs.Count > 0)
                    {
                        ConfigService.Save();
                        NotifyConfigAugmentationAdded(addedByPlugin);
                    }
                }).ConfigureAwait(false);

                return new PluginConfigAugmentationRunResult
                {
                    AddedFolderCount = addedByPlugin.Values.Sum(),
                    UpdatedConfigCount = touchedConfigs.Count,
                    TouchedPluginIds = addedByPlugin.Keys.ToArray()
                };
            }
            finally
            {
                _configAugmentationLock.Release();
            }
        }

        private static BackupConfig CloneConfigForAugmentationSnapshot(BackupConfig source)
        {
            try
            {
                return BackupConfigCloneService.CloneForRuntimeMutation(
                    source,
                    "Failed to clone backup config for plugin augmentation snapshot.");
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    $"[PluginService] Failed to clone config '{source?.Id}'; using a shallow augmentation snapshot: {ex.Message}",
                    "PluginService");

                var clone = new BackupConfig
                {
                    Id = source?.Id ?? string.Empty,
                    Name = source?.Name ?? string.Empty,
                    DestinationPath = source?.DestinationPath ?? string.Empty,
                    ConfigType = source?.ConfigType ?? "Default",
                    IconGlyph = source?.IconGlyph ?? string.Empty,
                    IsEncrypted = source?.IsEncrypted == true,
                    ExtendedProperties = source?.ExtendedProperties == null
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(source.ExtendedProperties, StringComparer.OrdinalIgnoreCase)
                };

                foreach (var folder in (source?.SourceFolders as IEnumerable<ManagedFolder>) ?? Array.Empty<ManagedFolder>())
                {
                    clone.SourceFolders.Add(CloneAugmentedFolder(folder));
                }

                return clone;
            }
        }

        private static int TryApplyAugmentationItem(BackupConfig config, PluginConfigAugmentationItem item)
        {
            if (config.SourceFolders == null)
            {
                config.SourceFolders = new ObservableCollection<ManagedFolder>();
            }

            var knownPaths = new HashSet<string>(
                config.SourceFolders.Select(folder => folder.Path ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var knownDisplayNames = new HashSet<string>(
                config.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var folder in item.FoldersToAdd.Where(static folder => folder != null && !string.IsNullOrWhiteSpace(folder.Path)))
            {
                string candidatePath = folder.Path.Trim();
                string candidateName = FolderNameConflictService.ResolveDisplayName(folder);

                if (knownPaths.Contains(candidatePath) || knownDisplayNames.Contains(candidateName))
                {
                    continue;
                }

                config.SourceFolders.Add(CloneAugmentedFolder(folder));
                knownPaths.Add(candidatePath);
                knownDisplayNames.Add(candidateName);
                added++;
            }

            return added;
        }

        private static ManagedFolder CloneAugmentedFolder(ManagedFolder source)
        {
            return new ManagedFolder
            {
                Path = source.Path?.Trim() ?? string.Empty,
                DisplayName = source.DisplayName?.Trim() ?? string.Empty,
                Description = source.Description?.Trim() ?? string.Empty,
                CoverImagePath = source.CoverImagePath?.Trim() ?? string.Empty
            };
        }

        private static void NotifyConfigAugmentationAdded(IReadOnlyDictionary<string, int> addedByPlugin)
        {
            foreach (var pair in addedByPlugin.Where(static entry => entry.Value > 0))
            {
                string pluginName = _installed
                    .FirstOrDefault(item => string.Equals(item.Id, pair.Key, StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? pair.Key;

                NotificationService.ShowInfo(
                    I18n.Format("PluginService_ConfigAugmentationAdded_Message", pluginName, pair.Value),
                    I18n.GetString("PluginService_ConfigAugmentationAdded_Title"));
            }
        }

        /// <summary>
        /// 检查是否有插件希望接管指定配置的备份
        /// </summary>
        public static (bool ShouldHandle, IFolderRewindPlugin? Plugin) CheckPluginWantsToHandleBackup(BackupConfig config)
        {
            if (!IsPluginSystemEnabled()) return (false, null);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (plugin.WantsToHandleBackup(config))
                    {
                        return (true, plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_WantsToHandleBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return (false, null);
        }

        /// <summary>
        /// 检查是否有插件希望接管指定配置的还原
        /// </summary>
        public static (bool ShouldHandle, IFolderRewindPlugin? Plugin) CheckPluginWantsToHandleRestore(BackupConfig config)
        {
            if (!IsPluginSystemEnabled()) return (false, null);

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (plugin.WantsToHandleRestore(config))
                    {
                        return (true, plugin);
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_WantsToHandleRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return (false, null);
        }

        /// <summary>
        /// 调用插件执行备份
        /// </summary>
        public static async Task<PluginBackupResult> InvokePluginBackupAsync(
            IFolderRewindPlugin plugin,
            BackupConfig config,
            ManagedFolder folder,
            string comment,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                var settings = GetPluginSettings(plugin.Manifest.Id);
                return await plugin.PerformBackupAsync(config, folder, comment, settings, progressCallback);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_PerformBackupFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                return new PluginBackupResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 调用插件执行还原
        /// </summary>
        public static async Task<PluginRestoreResult> InvokePluginRestoreAsync(
            IFolderRewindPlugin plugin,
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                var settings = GetPluginSettings(plugin.Manifest.Id);
                return await plugin.PerformRestoreAsync(config, folder, archiveFileName, settings, progressCallback);
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_PerformRestoreFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                return new PluginRestoreResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 调用插件批量创建配置
        /// </summary>
        public static PluginCreateConfigResult InvokeCreateConfigs(string selectedRootPath, string? configType = null)
        {
            if (!IsPluginSystemEnabled()) return new PluginCreateConfigResult { Handled = false };
            if (string.IsNullOrWhiteSpace(selectedRootPath)) return new PluginCreateConfigResult { Handled = false };

            var typeFilter = string.IsNullOrWhiteSpace(configType) ? null : configType;
            if (string.Equals(typeFilter, "Default", StringComparison.OrdinalIgnoreCase))
            {
                // Default 不是插件类型，不走插件批量创建
                return new PluginCreateConfigResult { Handled = false };
            }

            foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(typeFilter))
                    {
                        bool canHandleType = false;
                        try
                        {
                            canHandleType = plugin.CanHandleConfigType(typeFilter);
                        }
                        catch
                        {
                            // ignore; fallback to supported types list
                        }

                        if (!canHandleType)
                        {
                            var supported = plugin.GetSupportedConfigTypes();
                            canHandleType = supported != null && supported.Any(t => string.Equals(t, typeFilter, StringComparison.OrdinalIgnoreCase));
                        }

                        if (!canHandleType)
                        {
                            continue;
                        }
                    }

                    var settings = GetPluginSettings(plugin.Manifest.Id);
                    var result = plugin.TryCreateConfigs(selectedRootPath, settings);
                    if (result.Handled)
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_TryCreateConfigsFailed", plugin.Manifest.Id, ex.Message), "PluginService", ex);
                }
            }

            return new PluginCreateConfigResult { Handled = false };
        }

        /// <summary>
        /// 从 zip 安装插件（zip 内需包含 manifest.json）。
        /// 目标目录：plugins/{pluginId}/...
        /// </summary>
        public static async Task<(bool Success, string Message)> InstallFromZipAsync(string zipFilePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                return (false, _rl.GetString("PluginService_PackageNotFound"));
            }

            try
            {
                Directory.CreateDirectory(PluginRootDirectory);

                // 先读 manifest（防止解压一堆垃圾后才发现不合法）
                PluginInstallManifest? manifest;
                using (var zip = ZipFile.OpenRead(zipFilePath))
                {
                    var entry = zip.Entries.FirstOrDefault(e => string.Equals(NormalizeZipPath(e.FullName), ManifestFileName, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return (false, _rl.GetString("PluginService_MissingManifest"));

                    await using var s = entry.Open();
                    manifest = await JsonSerializer.DeserializeAsync(s, AppJsonContext.Default.PluginInstallManifest, ct);
                }

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    return (false, _rl.GetString("PluginService_InvalidManifestNoId"));
                }

                ApplyManifestLocalization(manifest);

                var targetDir = Path.Combine(PluginRootDirectory, SanitizeFolderName(manifest.Id));
                var wasEnabled = GetPluginEnabled(manifest.Id);

                // 覆盖安装：先删除原目录
                if (Directory.Exists(targetDir))
                {
                    var unloadSuccess = TryUnloadPlugin(manifest.Id);
                    if (!unloadSuccess)
                    {
                        var persistedPackage = PersistPendingUpdatePackage(zipFilePath, manifest.Id);
                        if (string.IsNullOrWhiteSpace(persistedPackage))
                        {
                            return (false, _rl.GetString("PluginService_QueueInstallPendingFailed"));
                        }

                        QueuePendingUpdate(manifest.Id, persistedPackage, wasEnabled);
                        return (true, _rl.GetString("PluginService_InstallPendingRestart"));
                    }

                    try
                    {
                        Directory.Delete(targetDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        LogService.LogWarning(I18n.Format("PluginService_DeleteFailed", manifest.Id, ex.Message), "PluginService");

                        var persistedPackage = PersistPendingUpdatePackage(zipFilePath, manifest.Id);
                        if (string.IsNullOrWhiteSpace(persistedPackage))
                        {
                            return (false, string.Format(_rl.GetString("PluginService_OverwriteFailed"), ex.Message));
                        }

                        QueuePendingUpdate(manifest.Id, persistedPackage, wasEnabled);
                        return (true, _rl.GetString("PluginService_InstallPendingRestart"));
                    }
                }

                Directory.CreateDirectory(targetDir);

                // 解压（防 ZipSlip）
                using (var zip = ZipFile.OpenRead(zipFilePath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();

                        var normalized = NormalizeZipPath(entry.FullName);
                        if (string.IsNullOrWhiteSpace(normalized)) continue;

                        // 目录项
                        if (normalized.EndsWith("/", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(Path.Combine(targetDir, normalized.TrimEnd('/')));
                            continue;
                        }

                        var destPath = Path.GetFullPath(Path.Combine(targetDir, normalized));
                        var fullTargetDir = Path.GetFullPath(targetDir);
                        if (!destPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
                        {
                            return (false, _rl.GetString("PluginService_ZipSlip"));
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }

                // 写回 UI/配置
                RefreshInstalledList();

                // 默认：新安装插件设为禁用，避免“装完立刻执行”带来的惊吓。
                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
                if (settings != null)
                {
                    if (!settings.PluginEnabled.ContainsKey(manifest.Id))
                    {
                        settings.PluginEnabled[manifest.Id] = false;
                        ConfigService.Save();
                    }
                }

                return (true, _rl.GetString("PluginService_InstallSuccessDisabled"));
            }
            catch (OperationCanceledException)
            {
                return (false, _rl.GetString("Common_Canceled"));
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_InstallFailed_Log", ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_InstallFailed"), ex.Message));
            }
        }

        private static string? PersistPendingUpdatePackage(string sourceZipPath, string pluginId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath)) return null;

                var pendingDir = Path.Combine(PluginRootDirectory, "_pending_updates");
                Directory.CreateDirectory(pendingDir);

                var fileName = $"{SanitizeFolderName(pluginId)}-{Guid.NewGuid():N}.zip";
                var targetPath = Path.Combine(pendingDir, fileName);
                File.Copy(sourceZipPath, targetPath, overwrite: false);

                return targetPath;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_PersistPendingPackageFailed_Log", pluginId, ex.Message), "PluginService");
                return null;
            }
        }

        public static (bool Success, string Message) Uninstall(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId)) return (false, _rl.GetString("PluginService_EmptyPluginId"));

            try
            {
                var dir = Path.Combine(PluginRootDirectory, SanitizeFolderName(pluginId));
                if (!Directory.Exists(dir)) return (false, _rl.GetString("PluginService_NotInstalled"));

                // 尝试卸载已加载的插件
                bool unloadSuccess = TryUnloadPlugin(pluginId);
                if (!unloadSuccess)
                {
                    // 插件仍被占用，标记为待删除，下次启动时清理
                    MarkPluginForDeletion(pluginId);
                    return (true, _rl.GetString("PluginService_UninstallPendingRestart"));
                }

                // 尝试删除目录
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception deleteEx)
                {
                    // 文件被占用，标记为待删除
                    LogService.LogWarning(I18n.Format("PluginService_DeleteFailed", pluginId, deleteEx.Message), "PluginService");
                    MarkPluginForDeletion(pluginId);
                    return (true, _rl.GetString("PluginService_UninstallPendingRestart"));
                }

                RefreshInstalledList();

                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
                if (settings != null)
                {
                    settings.PluginEnabled.Remove(pluginId);
                    settings.PluginSettings.Remove(pluginId);
                    ConfigService.Save();
                }

                return (true, _rl.GetString("PluginService_UninstallSuccess"));
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_UninstallFailed_Log", pluginId, ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_UninstallFailed"), ex.Message));
            }
        }

        /// <summary>
        /// 尝试卸载插件的 AssemblyLoadContext
        /// </summary>
        private static bool TryUnloadPlugin(string pluginId)
        {
            lock (_lock)
            {
                if (!_loaded.TryGetValue(pluginId, out var loaded)) return true;

                try
                {
                    // 取消热键注册
                    try
                    {
                        HotkeyManager.UnregisterPluginHotkeys(pluginId);
                    }
                    catch { }

                    // 尝试调用插件的 Dispose
                    try
                    {
                        if (loaded.Instance is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                    catch { }

                    // 获取弱引用以检查卸载是否成功
                    var weakRef = new WeakReference(loaded.LoadContext);

                    // 从已加载列表移除
                    _loaded.Remove(pluginId);

                    // 卸载 AssemblyLoadContext
                    loaded.LoadContext.Unload();

                    // 强制 GC 回收
                    for (int i = 0; i < 10 && weakRef.IsAlive; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }

                    return !weakRef.IsAlive;
                }
                catch (Exception ex)
                {
                    LogService.LogError(I18n.Format("PluginService_UnloadFailed", pluginId, ex.Message), "PluginService", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 标记插件为待删除状态（下次启动时清理）
        /// </summary>
        private static void MarkPluginForDeletion(string pluginId)
        {
            try
            {
                var pendingFile = Path.Combine(PluginRootDirectory, PendingDeleteFileName);
                var lines = File.Exists(pendingFile)
                    ? File.ReadAllLines(pendingFile).ToList()
                    : new List<string>();

                if (!lines.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add(pluginId);
                    File.WriteAllLines(pendingFile, lines);
                }
            }
            catch { }
        }

        /// <summary>
        /// 清理待删除的插件（在初始化时调用）
        /// </summary>
        private static void CleanPendingDeletions()
        {
            try
            {
                var pendingFile = Path.Combine(PluginRootDirectory, PendingDeleteFileName);
                if (!File.Exists(pendingFile)) return;

                var lines = File.ReadAllLines(pendingFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var remaining = new List<string>();

                foreach (var pluginId in lines)
                {
                    var dir = Path.Combine(PluginRootDirectory, SanitizeFolderName(pluginId));
                    if (Directory.Exists(dir))
                    {
                        try
                        {
                            Directory.Delete(dir, recursive: true);
                            LogService.LogInfo(I18n.Format("PluginService_PendingDeleteSuccess", pluginId), "PluginService");
                        }
                        catch
                        {
                            remaining.Add(pluginId);
                        }
                    }
                }

                if (remaining.Count > 0)
                {
                    File.WriteAllLines(pendingFile, remaining);
                }
                else
                {
                    File.Delete(pendingFile);
                }
            }
            catch { }
        }

        private static void QueuePendingUpdate(string pluginId, string packagePath, bool restoreEnabled)
        {
            try
            {
                Directory.CreateDirectory(PluginRootDirectory);

                var pendingFile = Path.Combine(PluginRootDirectory, PendingUpdateFileName);
                var lines = File.Exists(pendingFile)
                    ? File.ReadAllLines(pendingFile)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList()
                    : new List<string>();

                // 同一个插件仅保留最后一次更新包
                lines.RemoveAll(l =>
                {
                    var parts = l.Split('|', 3);
                    return parts.Length == 3 && string.Equals(parts[0], pluginId, StringComparison.OrdinalIgnoreCase);
                });

                lines.Add($"{pluginId}|{packagePath}|{(restoreEnabled ? "1" : "0")}");
                File.WriteAllLines(pendingFile, lines);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_QueueUpdateFailed_Log", pluginId, ex.Message), "PluginService");
            }
        }

        private static void ApplyPendingUpdates()
        {
            try
            {
                var pendingFile = Path.Combine(PluginRootDirectory, PendingUpdateFileName);
                if (!File.Exists(pendingFile)) return;

                var lines = File.ReadAllLines(pendingFile)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();

                var remaining = new List<string>();
                var saveSettings = false;
                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;

                foreach (var line in lines)
                {
                    var parts = line.Split('|', 3);
                    if (parts.Length != 3)
                    {
                        continue;
                    }

                    var pluginId = parts[0];
                    var packagePath = parts[1];
                    var restoreEnabled = string.Equals(parts[2], "1", StringComparison.OrdinalIgnoreCase);

                    if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                    {
                        continue;
                    }

                    try
                    {
                        var result = InstallFromZipAsync(packagePath).GetAwaiter().GetResult();
                        if (result.Success)
                        {
                            if (settings != null)
                            {
                                settings.PluginEnabled[pluginId] = restoreEnabled;
                                saveSettings = true;
                            }

                            try { File.Delete(packagePath); } catch { }

                            LogService.LogInfo(I18n.Format("PluginService_PendingUpdateApplied_Log", pluginId), "PluginService");
                        }
                        else
                        {
                            remaining.Add(line);
                            LogService.LogWarning(I18n.Format("PluginService_PendingUpdateApplyFailed_Log", pluginId, result.Message), "PluginService");
                        }
                    }
                    catch (Exception ex)
                    {
                        remaining.Add(line);
                        LogService.LogWarning(I18n.Format("PluginService_PendingUpdateApplyFailed_Log", pluginId, ex.Message), "PluginService");
                    }
                }

                if (saveSettings)
                {
                    ConfigService.Save();
                }

                if (remaining.Count > 0)
                {
                    File.WriteAllLines(pendingFile, remaining);
                }
                else
                {
                    File.Delete(pendingFile);
                }
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_ApplyPendingUpdatesFailed_Log", ex.Message), "PluginService");
            }
        }

        /// <summary>
        /// 检查所有已安装插件的更新
        /// </summary>
        public static async Task CheckAllPluginUpdatesAsync(CancellationToken ct = default, bool respectAutoCheckSetting = true)
        {
            var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
            if (settings == null) return;
            if (respectAutoCheckSetting && !settings.AutoCheckUpdates) return;

            foreach (var plugin in _installed.ToList())
            {
                if (ct.IsCancellationRequested) break;
                await CheckPluginUpdateAsync(plugin, ct);
            }
        }

        /// <summary>
        /// 检查单个插件的更新
        /// </summary>
        public static async Task CheckPluginUpdateAsync(InstalledPluginInfo plugin, CancellationToken ct = default)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Repository)) return;

            try
            {
                // 每次检查先清空旧状态，避免残留状态导致“误报有更新”
                plugin.HasUpdate = false;
                plugin.LatestVersion = null;
                plugin.UpdateDownloadUrl = null;

                var (hasUpdate, latestVersion, downloadUrl) = await CheckGitHubReleaseAsync(plugin.Repository, plugin.Version, ct);

                // 没有可下载链接时，不应视为可更新
                var actionable = hasUpdate && !string.IsNullOrWhiteSpace(downloadUrl);

                plugin.HasUpdate = actionable;
                plugin.LatestVersion = actionable ? latestVersion : null;
                plugin.UpdateDownloadUrl = actionable ? downloadUrl : null;
            }
            catch (Exception ex)
            {
                LogService.LogWarning(I18n.Format("PluginService_CheckUpdateFailed", plugin.Id, ex.Message), "PluginService");
            }
        }

        /// <summary>
        /// 检查 GitHub Release 获取最新版本
        /// </summary>
        private static async Task<(bool HasUpdate, string? LatestVersion, string? DownloadUrl)> CheckGitHubReleaseAsync(
            string repository, string currentVersion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(repository)) return (false, null, null);

            // 格式: owner/repo
            var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) return (false, null, null);

            var release = await GitHubReleaseService.GetLatestReleaseAsync(parts[0], parts[1], ct);
            if (!string.IsNullOrWhiteSpace(release.ErrorMessage) || string.IsNullOrWhiteSpace(release.TagName))
            {
                return (false, null, null);
            }

            var tagName = release.TagName;
            if (string.IsNullOrWhiteSpace(tagName)) return (false, null, null);

            // 清理版本号（移除 v 前缀）
            var latestVersion = tagName.TrimStart('v', 'V');
            var current = currentVersion?.TrimStart('v', 'V') ?? "0.0.0";

            // 比较版本
            bool hasUpdate = false;
            try
            {
                var latestVer = Version.Parse(latestVersion);
                var currentVer = Version.Parse(current);
                hasUpdate = latestVer > currentVer;
            }
            catch
            {
                // 版本格式不标准，使用字符串比较
                hasUpdate = !string.Equals(latestVersion, current, StringComparison.OrdinalIgnoreCase);
            }

            var downloadUrl = release.Assets
                .FirstOrDefault(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?.DownloadUrl;

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                downloadUrl = release.ZipballUrl;
            }

            return (hasUpdate, latestVersion, downloadUrl);
        }

        /// <summary>
        /// 从 URL 更新插件
        /// </summary>
        public static async Task<(bool Success, string Message)> UpdatePluginFromUrlAsync(
            InstalledPluginInfo plugin, CancellationToken ct = default)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.UpdateDownloadUrl))
            {
                return (false, _rl.GetString("PluginService_NoUpdateUrl"));
            }

            try
            {
                var wasEnabled = GetPluginEnabled(plugin.Id);

                // 保存到临时文件
                var tempFile = Path.Combine(Path.GetTempPath(), $"FolderRewind_Plugin_{plugin.Id}_{Guid.NewGuid():N}.zip");
                var bytes = await GitHubReleaseService.DownloadAssetAsync(plugin.UpdateDownloadUrl, ct);
                await using (var fs = File.Create(tempFile))
                {
                    await fs.WriteAsync(bytes, ct);
                }

                // 先卸载旧版本
                var unloadSuccess = TryUnloadPlugin(plugin.Id);
                if (!unloadSuccess)
                {
                    QueuePendingUpdate(plugin.Id, tempFile, wasEnabled);
                    return (true, _rl.GetString("PluginService_UpdatePendingRestart"));
                }

                // 安装新版本
                var result = await InstallFromZipAsync(tempFile, ct);

                if (!result.Success)
                {
                    // 大概率是文件占用导致覆盖失败，回退为“下次启动应用更新”
                    QueuePendingUpdate(plugin.Id, tempFile, wasEnabled);
                    return (true, _rl.GetString("PluginService_UpdatePendingRestart"));
                }

                // 清理临时文件
                try { File.Delete(tempFile); } catch { }

                if (result.Success)
                {
                    // 重新启用插件
                    if (wasEnabled)
                    {
                        TryLoadPlugin(plugin.Id);
                    }

                    // 清除更新标记
                    plugin.HasUpdate = false;
                    plugin.LatestVersion = null;
                    plugin.UpdateDownloadUrl = null;
                }

                return result;
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_UpdateFailed", plugin.Id, ex.Message), "PluginService", ex);
                return (false, string.Format(_rl.GetString("PluginService_UpdateFailed"), ex.Message));
            }
        }

        public static void OpenPluginFolder()
        {
            try
            {
                Directory.CreateDirectory(PluginRootDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = PluginRootDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_OpenPluginFolderFailed", ex.Message), "PluginService", ex);
            }
        }

        private static void TryLoadEnabledPlugins()
        {
            if (!IsPluginSystemEnabled()) return;

            // 确保 installed 已刷新
            foreach (var plugin in _installed.ToList())
            {
                var enabled = GetPluginEnabled(plugin.Id);
                plugin.IsEnabled = enabled;

                if (enabled)
                {
                    TryLoadPlugin(plugin.Id);
                }
            }
        }

        private static bool TryLoadPlugin(string pluginId)
        {
            lock (_lock)
            {
                if (_loaded.ContainsKey(pluginId)) return true;

                var installed = _installed.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (installed == null) return false;

                // 从安装目录读取 manifest
                var dir = installed.InstallPath;
                var manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_MissingManifest");
                    return false;
                }

                PluginInstallManifest? manifest;
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.PluginInstallManifest);
                }
                catch (Exception ex)
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_ManifestParseFailed");
                    LogService.LogError(I18n.Format("PluginService_ManifestParseFailed", pluginId, ex.Message), "PluginService", ex);
                    return false;
                }

                if (manifest != null)
                {
                    ApplyManifestLocalization(manifest);
                }

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_MissingEntry");
                    return false;
                }

                // 版本兼容性检查
                if (!IsVersionCompatible(manifest.MinHostVersion, out var incompatibleReason))
                {
                    installed.LoadError = incompatibleReason;
                    LogService.LogWarning(I18n.Format("PluginService_VersionIncompatible", pluginId, incompatibleReason ?? string.Empty), "PluginService");
                    return false;
                }

                var entryAssemblyPath = Path.Combine(dir, manifest.EntryAssembly);
                if (!File.Exists(entryAssemblyPath))
                {
                    installed.LoadError = _rl.GetString("PluginService_Load_EntryAssemblyMissing");
                    return false;
                }

                try
                {
                    var alc = new PluginLoadContext(dir);
                    var asm = alc.LoadFromAssemblyPath(entryAssemblyPath);
                    var type = asm.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false);

                    if (type == null)
                    {
                        installed.LoadError = _rl.GetString("PluginService_Load_EntryTypeMissing");
                        return false;
                    }

                    if (!typeof(IFolderRewindPlugin).IsAssignableFrom(type))
                    {
                        installed.LoadError = _rl.GetString("PluginService_Load_EntryTypeNotPlugin");
                        return false;
                    }

                    var instance = (IFolderRewindPlugin?)Activator.CreateInstance(type);
                    if (instance == null)
                    {
                        installed.LoadError = _rl.GetString("PluginService_Load_CreateInstanceFailed");
                        return false;
                    }

                    // 初始化插件（仅在启用时）
                    var settings = GetPluginSettings(manifest.Id);
                    instance.Initialize(settings);

                    // 注入宿主上下文，供插件主动使用 KnotLink 等能力
                    try
                    {
                        var hostCtx = PluginHostContext.CreateForCurrentApp(manifest.Id, manifest.Name ?? string.Empty);
                        instance.SetHostContext(hostCtx);
                    }
                    catch (Exception ctxEx)
                    {
                        LogService.LogWarning($"Plugin {manifest.Id}: SetHostContext failed: {ctxEx.Message}", "PluginService");
                    }

                    _loaded[manifest.Id] = new LoadedPlugin(manifest, instance, alc);
                    installed.LoadError = null;

                    LogService.LogInfo(I18n.Format("PluginService_Loaded", manifest.Id, manifest.Name ?? string.Empty, manifest.Version ?? string.Empty), "PluginService");
                    return true;
                }
                catch (Exception ex)
                {
                    installed.LoadError = ex.Message;
                    LogService.LogError(I18n.Format("PluginService_LoadFailed", pluginId, ex.Message), "PluginService", ex);
                    return false;
                }
            }
        }

        private static InstalledPluginInfo? ReadInstalledPluginInfo(string pluginDir)
        {
            try
            {
                var manifestPath = Path.Combine(pluginDir, ManifestFileName);
                if (!File.Exists(manifestPath)) return null;

                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize(json, AppJsonContext.Default.PluginInstallManifest);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id)) return null;

                ApplyManifestLocalization(manifest);

                var enabled = GetPluginEnabled(manifest.Id);
                return new InstalledPluginInfo
                {
                    Id = manifest.Id,
                    Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
                    Version = manifest.Version ?? string.Empty,
                    Author = manifest.Author ?? string.Empty,
                    Description = manifest.Description ?? string.Empty,
                    InstallPath = pluginDir,
                    IsEnabled = enabled,
                    Repository = manifest.Repository,
                    Homepage = manifest.Homepage
                };
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("PluginService_ReadInfoFailed", pluginDir, ex.Message), "PluginService", ex);
                return null;
            }
        }

        private static void ApplyManifestLocalization(PluginInstallManifest manifest)
        {
            if (manifest == null) return;

            var name = I18n.PickBest(manifest.LocalizedName, manifest.Name);
            if (!string.IsNullOrWhiteSpace(name)) manifest.Name = name;

            var desc = I18n.PickBest(manifest.LocalizedDescription, manifest.Description);
            if (!string.IsNullOrWhiteSpace(desc)) manifest.Description = desc;
        }

        private static IReadOnlyList<IFolderRewindPlugin> GetEnabledLoadedPluginsSnapshot()
        {
            // 注意：这里尽量少锁，避免备份过程中 UI 卡顿。
            lock (_lock)
            {
                var settings = ConfigService.CurrentConfig?.GlobalSettings?.Plugins;
                if (settings == null) return Array.Empty<IFolderRewindPlugin>();

                return _loaded.Values
                    .Where(p => settings.PluginEnabled.TryGetValue(p.Manifest.Id, out var en) && en)
                    .Select(p => p.Instance)
                    .ToList();
            }
        }

        private static string NormalizeZipPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            // zip entry 使用 / 分隔
            var p = path.Replace('\\', '/');
            // 去掉开头的 /，防止 Path.Combine 变成绝对路径
            while (p.StartsWith("/", StringComparison.Ordinal)) p = p[1..];
            return p;
        }

        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Trim();
        }

        private static string GetWritableAppDataDir()
        {
            return AppRuntimeInfo.WritableAppDataBaseDirectory;
        }

        private sealed record LoadedPlugin(PluginInstallManifest Manifest, IFolderRewindPlugin Instance, PluginLoadContext LoadContext);
    }
}
