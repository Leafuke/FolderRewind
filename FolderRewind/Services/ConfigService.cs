using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Configuration;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Graphics;

namespace FolderRewind.Services
{
    public static class ConfigService
    {
        #region 常量与状态

        private const string ConfigFileName = "config.json";
        private const double DefaultStartupWidth = 1300d;
        private const double DefaultStartupHeight = 900d;
        private const double StartupWorkAreaRatio = 0.9d;
        // 配置目录统一交给 GetWritableAppDataDir 决策，避免在不同发布形态下写到无权限位置。
        private static string ConfigPath => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", ConfigFileName);

        private static bool _initialized;

        public static event Action? Saved;

        public static AppConfig CurrentConfig { get; private set; } = new();

        public static bool IsRecoveryMode { get; private set; }

        public static ConfigRecoveryDiagnostic? RecoveryDiagnostic { get; private set; }

        public static string ConfigFilePath => ConfigPath;

        public static string ConfigDirectory => Path.GetDirectoryName(ConfigPath)!;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        #endregion

        #region 路径与默认值

        public static string GetRecommendedDefaultBackupRootPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FolderRewind-Backup");
        }

        public static string GetRecommendedDefaultCloudRemoteBasePath()
        {
            var configured = CurrentConfig.GlobalSettings.DefaultCloudRemoteBasePath.Trim();
            return string.IsNullOrWhiteSpace(configured)
                ? "remote:FolderRewind"
                : configured;
        }

        public static string BuildDefaultDestinationPath(string? configName)
        {
            var safeName = MakeSafeFolderName(configName);
            var root = string.IsNullOrWhiteSpace(CurrentConfig.GlobalSettings.DefaultBackupRootPath)
                ? GetRecommendedDefaultBackupRootPath()
                : CurrentConfig.GlobalSettings.DefaultBackupRootPath;
            return Path.Combine(root, safeName);
        }

        #endregion

        #region 初始化与规范化

        /// <summary>
        /// 初始化配置服务，加载或创建默认配置
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            IsRecoveryMode = false;
            RecoveryDiagnostic = null;
            bool createdDefault = false;
            var preparation = CreateMigrationService().Prepare(ConfigPath);
            AppConfig config;
            if (preparation.Status == ConfigFilePreparationStatus.RecoveryRequired)
            {
                EnterRecoveryMode(preparation.Diagnostic);
                return;
            }

            if (preparation.Status == ConfigFilePreparationStatus.Missing)
            {
                createdDefault = true;
                config = CreateDefaultConfig();
            }
            else
            {
                try
                {
                    config = DeserializeConfig(preparation.Utf8Json!);
                }
                catch (Exception ex)
                {
                    EnterRecoveryMode(new ConfigRecoveryDiagnostic(
                        "config_host_deserialization_failed",
                        ex.Message,
                        ConfigPath,
                        preparation.RecoveryCopyPath));
                    return;
                }
            }

            var originalLanguage = config.GlobalSettings.Language;
            NormalizeConfig(config);
            PrepareSchemaOnePersistence(config);
            var languageNormalized = !string.Equals(
                originalLanguage,
                config.GlobalSettings.Language,
                StringComparison.Ordinal);

            CurrentConfig = config;
            ApplyLogSettings(config.GlobalSettings);
            _initialized = true;

            if (createdDefault || languageNormalized)
            {
                var saveResult = SaveWithResult(publishSavedEvent: false);
                if (!saveResult.Success)
                {
                    LogService.LogWarning(
                        $"[Config] Failed to persist normalized startup configuration: {saveResult.ErrorMessage}",
                        "ConfigService");
                }
                else if (languageNormalized)
                {
                    LogService.LogInfo(
                        $"[Config] Language setting normalized to '{config.GlobalSettings.Language}'.",
                        "ConfigService");
                }
            }
        }

        private static ConfigFileMigrationService CreateMigrationService()
            => new(payloadValidator: ValidateHostPayload);

        private static string? ValidateHostPayload(byte[] utf8Json)
        {
            try
            {
                var config = DeserializeConfig(utf8Json);
                NormalizeConfig(config);
                PrepareSchemaOnePersistence(config);
                return null;
            }
            catch (Exception ex)
            {
                return $"Host configuration model rejected the document: {ex.Message}";
            }
        }

        private static AppConfig DeserializeConfig(byte[] utf8Json)
        {
            var config = JsonSerializer.Deserialize(utf8Json, AppJsonContext.Default.AppConfig)
                ?? throw new InvalidDataException("The configuration deserialized to null.");
            if (config.SchemaVersion != ConfigSchema.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Expected configuration schema {ConfigSchema.CurrentVersion}, got {config.SchemaVersion}.");
            }

            return config;
        }

        private static void EnterRecoveryMode(ConfigRecoveryDiagnostic? diagnostic)
        {
            CurrentConfig = new AppConfig();
            ApplyLogSettings(CurrentConfig.GlobalSettings);
            RecoveryDiagnostic = diagnostic ?? new ConfigRecoveryDiagnostic(
                "config_recovery_required",
                "The configuration could not be loaded safely.",
                ConfigPath);
            IsRecoveryMode = true;
            _initialized = true;
            LogService.LogError(
                $"[Config] Recovery Center required: {RecoveryDiagnostic.Code}: {RecoveryDiagnostic.Message}",
                "ConfigService");
        }

        private static AppConfig CreateDefaultConfig()
        {
            var config = new AppConfig();
            var settings = config.GlobalSettings;
            settings.SevenZipPath = "7za.exe";
            settings.DefaultBackupRootPath = GetRecommendedDefaultBackupRootPath();
            settings.DefaultCloudRemoteBasePath = "remote:FolderRewind";

            try
            {
                settings.FontFamily = FontService.GetRecommendedDefaultFontFamily();
                var (startupWidth, startupHeight) = GetRecommendedStartupWindowSize();
                settings.StartupWidth = startupWidth;
                settings.StartupHeight = startupHeight;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Default config initialization fallback: {ex.Message}");
                LogService.Log($"Default config initialization fallback: {ex.Message}");
            }

            var defaultName = I18n.Format("Config_DefaultBackupName");
            var defaultConfig = new BackupConfig
            {
                Name = defaultName,
                DestinationPath = Path.Combine(settings.DefaultBackupRootPath, MakeSafeFolderName(defaultName)),
                SummaryText = ""
            };
            defaultConfig.Archive.Format = "7z";
            defaultConfig.Archive.CompressionLevel = 5;
            defaultConfig.Cloud.RemoteBasePath = settings.DefaultCloudRemoteBasePath;

            config.BackupConfigs.Add(defaultConfig);
            return config;
        }

        private static void NormalizeConfig(AppConfig config)
        {
            NormalizeGlobalSettings(config.GlobalSettings);
            string defaultRemoteBasePath = config.GlobalSettings.DefaultCloudRemoteBasePath;

            foreach (var backupConfig in config.BackupConfigs)
            {
                if (backupConfig == null) continue;

                backupConfig.Automation.Normalize(backupConfig.SourceFolders);
                foreach (var folder in backupConfig.SourceFolders.Where(folder => folder != null))
                {
                    folder.SourceScope ??= new BackupSourceScope();
                    folder.SourceScope.IncludePatterns = new System.Collections.ObjectModel.ObservableCollection<string>(
                        (folder.SourceScope.IncludePatterns ?? new System.Collections.ObjectModel.ObservableCollection<string>())
                            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                            .Select(pattern => pattern.Trim().Replace('\\', '/'))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                }
                if (backupConfig.DiscoveryOrigin != null)
                {
                    backupConfig.DiscoveryOrigin.Identity ??= new DiscoverySetIdentity();
                    backupConfig.DiscoveryOrigin.Identity.ProviderId = backupConfig.DiscoveryOrigin.Identity.ProviderId?.Trim() ?? string.Empty;
                    backupConfig.DiscoveryOrigin.Identity.DefinitionId = backupConfig.DiscoveryOrigin.Identity.DefinitionId?.Trim() ?? string.Empty;
                    backupConfig.DiscoveryOrigin.Identity.SetId = backupConfig.DiscoveryOrigin.Identity.SetId?.Trim() ?? string.Empty;
                    backupConfig.DiscoveryOrigin.Identity.ExternalIds = backupConfig.DiscoveryOrigin.Identity.ExternalIds == null
                        ? new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : new System.Collections.Generic.Dictionary<string, string>(backupConfig.DiscoveryOrigin.Identity.ExternalIds, StringComparer.OrdinalIgnoreCase);
                    backupConfig.DiscoveryOrigin.ReviewedBaseline ??= new ReviewedDiscoveryBaseline();
                    foreach (var source in backupConfig.DiscoveryOrigin.ReviewedBaseline.Sources)
                    {
                        source.NormalizedRootPath = source.NormalizedRootPath?.Trim() ?? string.Empty;
                        source.IncludePatterns = new System.Collections.ObjectModel.ObservableCollection<string>(
                            (source.IncludePatterns ?? new System.Collections.ObjectModel.ObservableCollection<string>())
                                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                                .Select(pattern => pattern.Trim().Replace('\\', '/'))
                                .Distinct(StringComparer.OrdinalIgnoreCase));
                        source.ResourceIds = new System.Collections.ObjectModel.ObservableCollection<string>(
                            (source.ResourceIds ?? new System.Collections.ObjectModel.ObservableCollection<string>())
                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                .Select(id => id.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase));
                    }
                }
                NormalizeBackupScope(backupConfig.BackupScope);
                NormalizeCloudSettings(backupConfig.Cloud, defaultRemoteBasePath);
            }

            foreach (var template in config.BackupPresets)
            {
                if (template == null) continue;

                if (string.IsNullOrWhiteSpace(template.Id))
                    template.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(template.ShareId))
                    template.ShareId = Guid.NewGuid().ToString("N");

                template.ShareCode = template.ShareCode.Trim().ToUpperInvariant();
                template.GameName = template.GameName.Trim();
                if (string.IsNullOrWhiteSpace(template.BaseConfigType))
                    template.BaseConfigType = "Default";

                template.Automation.Normalize();
                NormalizeBackupScope(template.BackupScope);
                NormalizeCloudSettings(template.Cloud, defaultRemoteBasePath);
                template.NormalizeDiscoverySources();

                foreach (var rule in template.PathRules)
                {
                    if (rule != null && string.IsNullOrWhiteSpace(rule.Id))
                        rule.Id = Guid.NewGuid().ToString("N");
                }
            }
        }

        private static void PrepareSchemaOnePersistence(AppConfig config)
        {
            config.SchemaVersion = ConfigSchema.CurrentVersion;
            config.SchemaExtensions ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var usedFolderIds = new HashSet<Guid>();

            var pluginSettings = config.GlobalSettings.Plugins;
            if (pluginSettings.EnabledIntent.Count == 0 && pluginSettings.PluginEnabled.Count > 0)
            {
                pluginSettings.EnabledIntent = new Dictionary<string, bool>(
                    pluginSettings.PluginEnabled,
                    StringComparer.OrdinalIgnoreCase);
            }

            if (pluginSettings.TypedSettings.Count == 0 && pluginSettings.PluginSettings.Count > 0)
            {
                pluginSettings.TypedSettings = BuildTypedPluginSettings(pluginSettings.PluginSettings);
            }

            foreach (var backupConfig in config.BackupConfigs.Where(static item => item != null))
            {
                var isMinecraft = string.Equals(
                    backupConfig.ConfigType,
                    "Minecraft Saves",
                    StringComparison.OrdinalIgnoreCase);
                var ownerId = isMinecraft ? ConfigSchema.MineRewindPluginId : ConfigSchema.CoreOwnerId;
                var kindId = isMinecraft ? ConfigSchema.MineRewindKindId : ConfigSchema.CoreDefaultKindId;
                if (string.IsNullOrWhiteSpace(backupConfig.Kind.OwnerId)
                    || string.IsNullOrWhiteSpace(backupConfig.Kind.KindId)
                    || (isMinecraft
                        && string.Equals(backupConfig.Kind.OwnerId, ConfigSchema.CoreOwnerId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(backupConfig.Kind.KindId, ConfigSchema.CoreDefaultKindId, StringComparison.OrdinalIgnoreCase)))
                {
                    backupConfig.Kind = new ConfigKindReference { OwnerId = ownerId, KindId = kindId };
                }

                backupConfig.ProviderStates ??= new Dictionary<string, ProviderStatePayload>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(backupConfig.ConfigRevision))
                {
                    backupConfig.ConfigRevision = Guid.NewGuid().ToString("N");
                }
                if (backupConfig.ArtifactTransformPolicy is not null)
                {
                    backupConfig.ArtifactTransformPolicy.Transformer ??= new ArtifactTransformerReference();
                    backupConfig.ArtifactTransformPolicy.Parameters = backupConfig.ArtifactTransformPolicy.Parameters is null
                        ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        : new Dictionary<string, JsonElement>(backupConfig.ArtifactTransformPolicy.Parameters, StringComparer.Ordinal);
                }
                if (string.IsNullOrWhiteSpace(backupConfig.BackupScope.OwnerId)
                    && string.IsNullOrWhiteSpace(backupConfig.BackupScope.ScopeId))
                {
                    var selectedRegions = string.Equals(
                        backupConfig.BackupScope.PluginScopeId,
                        "MineRewind.SelectedRegions",
                        StringComparison.OrdinalIgnoreCase);
                    backupConfig.BackupScope.OwnerId = selectedRegions ? ConfigSchema.MineRewindPluginId : string.Empty;
                    backupConfig.BackupScope.ScopeId = selectedRegions ? ConfigSchema.MineRewindSelectedRegionsScopeId : string.Empty;
                }

                foreach (var folder in backupConfig.SourceFolders.Where(static item => item != null))
                {
                    if (!Guid.TryParse(folder.Id, out var folderId)
                        || folderId == Guid.Empty
                        || !usedFolderIds.Add(folderId))
                    {
                        do
                        {
                            folderId = Guid.NewGuid();
                        }
                        while (!usedFolderIds.Add(folderId));

                        folder.Id = folderId.ToString();
                    }

                    folder.ProviderStates ??= new Dictionary<string, ProviderStatePayload>(StringComparer.OrdinalIgnoreCase);
                }
            }

            foreach (var preset in config.BackupPresets.Where(static item => item != null))
            {
                preset.SchemaVersion = ConfigSchema.CurrentVersion;
                var isMinecraft = string.Equals(
                    preset.BaseConfigType,
                    "Minecraft Saves",
                    StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(preset.Kind.OwnerId)
                    || string.IsNullOrWhiteSpace(preset.Kind.KindId)
                    || (isMinecraft
                        && string.Equals(preset.Kind.OwnerId, ConfigSchema.CoreOwnerId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(preset.Kind.KindId, ConfigSchema.CoreDefaultKindId, StringComparison.OrdinalIgnoreCase)))
                {
                    preset.Kind = new ConfigKindReference
                    {
                        OwnerId = isMinecraft ? ConfigSchema.MineRewindPluginId : ConfigSchema.CoreOwnerId,
                        KindId = isMinecraft ? ConfigSchema.MineRewindKindId : ConfigSchema.CoreDefaultKindId
                    };
                }

                preset.ProviderDefaults ??= new Dictionary<string, PresetProviderDefaults>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static Dictionary<string, Dictionary<string, JsonElement>> BuildTypedPluginSettings(
            IReadOnlyDictionary<string, Dictionary<string, string>> settings)
        {
            var root = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pluginId, values) in settings)
            {
                var typedValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in values)
                {
                    if (string.Equals(pluginId, ConfigSchema.MineRewindPluginId, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(key, "AutoDiscoverSaves", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(key, "AutoCreateConfigs", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(key, "PreservePlayerData", StringComparison.OrdinalIgnoreCase))
                        && TryParseLegacyBoolean(value, out var booleanValue))
                    {
                        typedValues[key] = JsonSerializer.SerializeToElement(booleanValue);
                    }
                    else
                    {
                        typedValues[key] = JsonSerializer.SerializeToElement(value);
                    }
                }

                root[pluginId] = typedValues;
            }

            return root;
        }

        private static bool TryParseLegacyBoolean(string? value, out bool result)
        {
            if (bool.TryParse(value, out result)) return true;
            if (value == "1") { result = true; return true; }
            if (value == "0") { result = false; return true; }
            result = false;
            return false;
        }

        private static (double Width, double Height) GetRecommendedStartupWindowSize()
        {
            var scale = GetSystemDpiScale();
            var width = DefaultStartupWidth * scale;
            var height = DefaultStartupHeight * scale;

            try
            {
                var workArea = DisplayArea.Primary.WorkArea;
                if (workArea.Width > 0)
                {
                    width = Math.Min(width, workArea.Width * StartupWorkAreaRatio);
                }

                if (workArea.Height > 0)
                {
                    height = Math.Min(height, workArea.Height * StartupWorkAreaRatio);
                }
            }
            catch
            {
            }

            return (
                Math.Clamp(width, 640, 3840),
                Math.Clamp(height, 480, 2160));
        }

        private static double GetSystemDpiScale()
        {
            try
            {
                var dpi = GetDpiForSystem();
                return dpi > 0 ? Math.Max(1d, dpi / 96d) : 1d;
            }
            catch
            {
                return 1d;
            }
        }

        private static void NormalizeCloudSettings(CloudSettings cloud, string defaultRemoteBasePath)
        {
            if (string.IsNullOrWhiteSpace(cloud.ExecutablePath))
                cloud.ExecutablePath = "rclone.exe";

            if (string.IsNullOrWhiteSpace(cloud.RemoteBasePath))
                cloud.RemoteBasePath = defaultRemoteBasePath;

            if (cloud.TimeoutSeconds <= 0)
                cloud.TimeoutSeconds = 600;

            if (cloud.RetryCount < 0)
                cloud.RetryCount = 0;

            if (string.IsNullOrWhiteSpace(cloud.ArgumentsTemplate) && cloud.CommandMode == CloudCommandMode.Rclone)
            {
                cloud.ArgumentsTemplate = cloud.TemplateKind == CloudTemplateKind.UploadBackupDirectory
                    ? "copy \"{BackupSubDir}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}\""
                    : "copyto \"{ArchiveFilePath}\" \"{RemoteBasePath}/{ConfigName}/{FolderName}/{ArchiveFileName}\"";
            }
        }

        private static void NormalizeBackupScope(BackupScopeSettings scope)
        {
            scope.PluginScopeId = scope.PluginScopeId?.Trim() ?? string.Empty;
            scope.Parameters = scope.Parameters == null
                ? new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new System.Collections.Generic.Dictionary<string, string>(scope.Parameters, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region 持久化与重载

        public static void Save()
        {
            var result = SaveWithResult();
            if (!result.Success)
            {
                System.Diagnostics.Debug.WriteLine($"Config save error: {result.ErrorMessage}");
                LogService.Log(I18n.Format("Config_SaveFailed", result.ErrorMessage));
            }
        }

        public static ConfigSaveResult SaveWithResult(bool publishSavedEvent = true)
        {
            if (IsRecoveryMode)
            {
                return new ConfigSaveResult
                {
                    Success = false,
                    ErrorMessage = "Configuration writes are disabled while Recovery Center is active."
                };
            }

            try
            {
                NormalizeConfig(CurrentConfig);
                PrepareSchemaOnePersistence(CurrentConfig);
                AtomicFileService.Write(
                    ConfigPath,
                    stream => JsonSerializer.Serialize(
                        stream,
                        CurrentConfig,
                        AppJsonContext.Default.AppConfig));
                if (publishSavedEvent)
                {
                    PublishSaved();
                }

                return new ConfigSaveResult { Success = true };
            }
            catch (Exception ex)
            {
                return new ConfigSaveResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex
                };
            }
        }

        internal static void PublishSaved() => Saved?.Invoke();

        public static bool Reload()
        {
            _initialized = false;
            Initialize();
            return _initialized && !IsRecoveryMode;
        }

        #endregion

        #region 配置文件访问

        public static void OpenConfigFolder()
        {
            try
            {
                if (!Directory.Exists(ConfigDirectory))
                {
                    if (IsRecoveryMode) return;
                    Directory.CreateDirectory(ConfigDirectory);
                }
                if (!ShellPathService.TryOpenPath(ConfigDirectory, out var openError))
                {
                    LogService.Log(I18n.Format("Config_OpenConfigDirFailed", openError ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Config_OpenConfigDirFailed", ex.Message));
            }
        }

        public static void OpenConfigFile()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    if (IsRecoveryMode) return;
                    Save();
                }
                if (!ShellPathService.TryOpenPath(ConfigPath, out var openError))
                {
                    LogService.Log(I18n.Format("Config_OpenConfigFileFailed", openError ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Config_OpenConfigFileFailed", ex.Message));
            }
        }

        #endregion

        #region 导入导出

        /// <summary>
        /// 导出当前配置到指定路径
        /// </summary>
        public static bool ExportConfig(string destPath)
        {
            if (IsRecoveryMode)
            {
                return ExportRecoverySource(destPath);
            }

            try
            {
                NormalizeConfig(CurrentConfig);
                PrepareSchemaOnePersistence(CurrentConfig);
                AtomicFileService.Write(
                    destPath,
                    stream => JsonSerializer.Serialize(
                        stream,
                        CurrentConfig,
                        AppJsonContext.Default.AppConfig));
                LogService.Log(I18n.Format("Config_ExportSuccess", destPath));
                return true;
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Config_ExportFailed", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 从指定路径导入配置（替换当前配置）
        /// </summary>
        public static bool ImportConfig(string sourcePath)
        {
            if (IsRecoveryMode)
            {
                return false;
            }

            try
            {
                if (!File.Exists(sourcePath)) return false;
                var sourceBytes = File.ReadAllBytes(sourcePath);
                var gateResult = new ConfigDocumentGate().Prepare(sourceBytes);
                if (!gateResult.IsReady || gateResult.Utf8Json is null)
                {
                    LogService.Log(I18n.Format(
                        "Config_ImportFailed",
                        $"{gateResult.DiagnosticCode}: {gateResult.DiagnosticMessage}"));
                    return false;
                }

                var imported = DeserializeConfig(gateResult.Utf8Json);
                NormalizeConfig(imported);
                PrepareSchemaOnePersistence(imported);
                var importedBytes = SerializeConfig(imported);
                var validation = new ConfigDocumentGate().Prepare(importedBytes);
                if (validation.Status != ConfigDocumentGateStatus.Current)
                {
                    throw new InvalidDataException(
                        $"Imported configuration failed schema validation: {validation.DiagnosticCode} {validation.DiagnosticMessage}");
                }

                var safetyCopy = CreateSafetyCopy("before-import");
                try
                {
                    WriteBytesAtomically(ConfigPath, importedBytes);
                    var readBackError = ValidateHostPayload(File.ReadAllBytes(ConfigPath));
                    if (!string.IsNullOrWhiteSpace(readBackError))
                    {
                        throw new InvalidDataException(readBackError);
                    }
                }
                catch
                {
                    RestoreSafetyCopy(safetyCopy);
                    throw;
                }

                _initialized = false;
                Initialize();
                if (IsRecoveryMode)
                {
                    RestoreSafetyCopy(safetyCopy);
                    _initialized = false;
                    Initialize();
                    throw new InvalidDataException("Imported configuration could not be activated safely.");
                }

                LogService.Log(I18n.Format("Config_ImportSuccess", sourcePath));
                return true;
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Config_ImportFailed", ex.Message));
                return false;
            }
        }

        public static IReadOnlyList<string> GetRecoveryCopies()
        {
            try
            {
                return ConfigFileMigrationService.ListRecoveryCopies(ConfigPath);
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[Config] Failed to enumerate recovery copies: {ex.Message}", "ConfigService");
                return Array.Empty<string>();
            }
        }

        public static bool ExportRecoverySource(string destinationPath)
        {
            if (!IsRecoveryMode || !File.Exists(ConfigPath) || string.IsNullOrWhiteSpace(destinationPath))
            {
                return false;
            }

            try
            {
                var original = File.ReadAllBytes(ConfigPath);
                WriteBytesAtomically(destinationPath, original);
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError($"[Config] Recovery export failed: {ex.Message}", "ConfigService", ex);
                return false;
            }
        }

        public static bool RetryRecovery()
        {
            if (!IsRecoveryMode) return true;
            _initialized = false;
            Initialize();
            return !IsRecoveryMode;
        }

        public static bool RestoreRecoveryCopy(string recoveryCopyPath)
        {
            if (!IsRecoveryMode || string.IsNullOrWhiteSpace(recoveryCopyPath)) return false;
            var requested = Path.GetFullPath(recoveryCopyPath);
            if (!GetRecoveryCopies().Any(path => string.Equals(
                    Path.GetFullPath(path),
                    requested,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            try
            {
                var prepared = new ConfigDocumentGate().Prepare(File.ReadAllBytes(requested));
                if (!prepared.IsReady || prepared.Utf8Json is null) return false;
                var restored = DeserializeConfig(prepared.Utf8Json);
                NormalizeConfig(restored);
                PrepareSchemaOnePersistence(restored);
                return ReplaceFromRecoveryAction(SerializeConfig(restored), "before-restore-copy");
            }
            catch (Exception ex)
            {
                LogService.LogError($"[Config] Recovery-copy restore failed: {ex.Message}", "ConfigService", ex);
                return false;
            }
        }

        public static bool ResetFromRecovery()
        {
            if (!IsRecoveryMode) return false;
            try
            {
                var replacement = CreateDefaultConfig();
                NormalizeConfig(replacement);
                PrepareSchemaOnePersistence(replacement);
                return ReplaceFromRecoveryAction(SerializeConfig(replacement), "before-reset");
            }
            catch (Exception ex)
            {
                LogService.LogError($"[Config] Recovery reset failed: {ex.Message}", "ConfigService", ex);
                return false;
            }
        }

        private static bool ReplaceFromRecoveryAction(byte[] replacement, string safetyReason)
        {
            var validation = new ConfigDocumentGate().Prepare(replacement);
            if (validation.Status != ConfigDocumentGateStatus.Current)
            {
                return false;
            }

            var safetyCopy = CreateSafetyCopy(safetyReason);
            try
            {
                WriteBytesAtomically(ConfigPath, replacement);
                var readBackError = ValidateHostPayload(File.ReadAllBytes(ConfigPath));
                if (!string.IsNullOrWhiteSpace(readBackError))
                {
                    throw new InvalidDataException(readBackError);
                }

                _initialized = false;
                Initialize();
                if (IsRecoveryMode)
                {
                    throw new InvalidDataException("The replacement could not be activated safely.");
                }

                return true;
            }
            catch
            {
                RestoreSafetyCopy(safetyCopy);
                _initialized = false;
                Initialize();
                return false;
            }
        }

        private static byte[] SerializeConfig(AppConfig config)
        {
            using var stream = new MemoryStream();
            JsonSerializer.Serialize(stream, config, AppJsonContext.Default.AppConfig);
            return stream.ToArray();
        }

        private static void WriteBytesAtomically(string destinationPath, byte[] bytes)
            => AtomicFileService.Write(destinationPath, stream => stream.Write(bytes));

        private static string? CreateSafetyCopy(string reason)
        {
            if (!File.Exists(ConfigPath)) return null;
            Directory.CreateDirectory(ConfigDirectory);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
            for (var sequence = 0; sequence < 10_000; sequence++)
            {
                var suffix = sequence == 0 ? string.Empty : $".{sequence:D4}";
                var candidate = Path.Combine(
                    ConfigDirectory,
                    $"{Path.GetFileName(ConfigPath)}.recovery.{timestamp}.{reason}{suffix}.json");
                try
                {
                    using var source = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var destination = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        FileOptions.WriteThrough);
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                }
            }

            throw new IOException("Unable to create a unique configuration safety copy.");
        }

        private static void RestoreSafetyCopy(string? safetyCopy)
        {
            if (string.IsNullOrWhiteSpace(safetyCopy) || !File.Exists(safetyCopy)) return;
            WriteBytesAtomically(ConfigPath, File.ReadAllBytes(safetyCopy));
        }

        #endregion

        #region 规范化与内部工具

        private static void NormalizeGlobalSettings(GlobalSettings settings)
        {
            settings.Language = LanguageSettingPolicy.Normalize(settings.Language);

            if (settings.ThemeIndex < 0 || settings.ThemeIndex > 2)
            {
                settings.ThemeIndex = 1;
            }

            if (!settings.RunOnStartup)
            {
                settings.SilentStartup = false;
            }

            if (string.IsNullOrWhiteSpace(settings.FontFamily))
            {
                settings.FontFamily = FontService.GetRecommendedDefaultFontFamily();
            }

            if (string.IsNullOrWhiteSpace(settings.SevenZipPath))
            {
                settings.SevenZipPath = "7za.exe";
            }

            settings.RcloneExecutablePath = settings.RcloneExecutablePath?.Trim() ?? string.Empty;
            settings.DefaultCloudRemoteBasePath = string.IsNullOrWhiteSpace(settings.DefaultCloudRemoteBasePath)
                ? "remote:FolderRewind"
                : settings.DefaultCloudRemoteBasePath.Trim();
            if (string.IsNullOrWhiteSpace(settings.DefaultBackupRootPath))
            {
                settings.DefaultBackupRootPath = GetRecommendedDefaultBackupRootPath();
            }

            if (double.IsNaN(settings.BaseFontSize) || settings.BaseFontSize <= 0)
            {
                settings.BaseFontSize = 14;
            }
            else
            {
                settings.BaseFontSize = Math.Clamp(settings.BaseFontSize, 12, 20);
            }

            if (string.IsNullOrWhiteSpace(settings.HomeSortMode))
            {
                settings.HomeSortMode = "NameAsc";
            }

            // Toast 等级约束在有效区间（0-3）。
            settings.ToastNotificationLevel = Math.Clamp(settings.ToastNotificationLevel, 0, 3);

            settings.AppUpdatePreferredSource = Math.Clamp(settings.AppUpdatePreferredSource, 0, 3);
            settings.AppUpdateCustomMirrorUrl = settings.AppUpdateCustomMirrorUrl?.Trim() ?? string.Empty;

            settings.GameDiscovery ??= new GameDiscoverySettings();
            settings.GameDiscovery.SecondaryManifestPath = settings.GameDiscovery.SecondaryManifestPath?.Trim() ?? string.Empty;
            settings.GameDiscovery.OverridePath = settings.GameDiscovery.OverridePath?.Trim() ?? string.Empty;
            settings.GameDiscovery.LibraryRoots = new System.Collections.ObjectModel.ObservableCollection<GameLibraryRootSetting>(
                settings.GameDiscovery.LibraryRoots
                    .Where(root => root != null && !string.IsNullOrWhiteSpace(root.Path))
                    .Select(root =>
                    {
                        root.Path = root.Path.Trim();
                        return root;
                    })
                    .GroupBy(root => $"{root.Store}|{root.Path}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()));

            settings.SponsorAccentColorIndex = Math.Clamp(settings.SponsorAccentColorIndex, 0, ThemeService.SponsorAccentPresetCount - 1);
            settings.SponsorBackdropIndex = Math.Clamp(settings.SponsorBackdropIndex, 0, 1);
            settings.SponsorTitleText = settings.SponsorTitleText?.Trim() ?? string.Empty;
            settings.SponsorTitleIconGlyph = string.IsNullOrWhiteSpace(settings.SponsorTitleIconGlyph)
                ? IconCatalog.DefaultConfigIconGlyph
                : settings.SponsorTitleIconGlyph;
            settings.SponsorBackgroundImagePath = settings.SponsorBackgroundImagePath?.Trim() ?? string.Empty;
            settings.SponsorBackgroundStretchIndex = Math.Clamp(settings.SponsorBackgroundStretchIndex, 0, 2);
            settings.SponsorBackgroundImageOpacity = ClampUnit(settings.SponsorBackgroundImageOpacity, 0.28);
            settings.SponsorBackgroundOverlayOpacity = ClampUnit(settings.SponsorBackgroundOverlayOpacity, 0.62);
            settings.CompletionSoundIndex = Math.Clamp(settings.CompletionSoundIndex, 0, CompletionSoundService.PresetCount - 1);
            settings.CompletionSoundCustomPath = settings.CompletionSoundCustomPath?.Trim() ?? string.Empty;
            if (!settings.SponsorEntitlementCached)
            {
                settings.SponsorEntitlementLastVerifiedUtc = DateTime.MinValue;
            }
        }

        private static double ClampUnit(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return fallback;
            }

            return Math.Clamp(value, 0, 1);
        }

        private static string MakeSafeFolderName(string? name)
        {
            var fallback = "Backup";
            var raw = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                raw = raw.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(raw) ? fallback : raw;
        }

        private static void ApplyLogSettings(GlobalSettings settings)
        {
            var options = new LogOptions
            {
                EnableFileLogging = settings.EnableFileLogging,
                MaxEntries = 4000,
                MaxFileSizeKb = Math.Max(512, settings.MaxLogFileSizeMb * 1024),
                RetentionDays = Math.Max(1, settings.LogRetentionDays)
            };

            LogService.ApplyOptions(options);
        }

        #endregion
    }
}
