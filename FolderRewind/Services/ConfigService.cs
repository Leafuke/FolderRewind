using FolderRewind.Models;
using Microsoft.UI.Windowing;
using System;
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

            bool createdDefault;
            var config = LoadConfig(out createdDefault);
            var originalLanguage = config.GlobalSettings.Language;
            NormalizeConfig(config);
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

        private static AppConfig LoadConfig(out bool createdDefault)
        {
            createdDefault = false;
            if (!File.Exists(ConfigPath))
            {
                createdDefault = true;
                return CreateDefaultConfig();
            }

            try
            {
                using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var loaded = JsonSerializer.Deserialize(stream, AppJsonContext.Default.AppConfig);
                if (loaded != null)
                {
                    return loaded;
                }

                LogService.Log(I18n.GetString("Config_ParseNull_Reset"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Config load error: {ex.Message}");
                LogService.Log(I18n.Format("Config_LoadFailed_Reset", ex.Message));
            }

            createdDefault = true;
            return CreateDefaultConfig();
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
            try
            {
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
            return _initialized;
        }

        #endregion

        #region 配置文件访问

        public static void OpenConfigFolder()
        {
            try
            {
                if (!Directory.Exists(ConfigDirectory)) Directory.CreateDirectory(ConfigDirectory);
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
                if (!File.Exists(ConfigPath)) Save();
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
            try
            {
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
            try
            {
                if (!File.Exists(sourcePath)) return false;
                using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var imported = JsonSerializer.Deserialize(sourceStream, AppJsonContext.Default.AppConfig);
                if (imported == null)
                {
                    LogService.Log(I18n.GetString("Config_ImportFailed_Null"));
                    return false;
                }

                // 先备份旧文件，导入后如果用户反悔还能手动找回。
                string backupPath = ConfigPath + ".bak";
                try { File.Copy(ConfigPath, backupPath, true); } catch { }

                NormalizeConfig(imported);
                AtomicFileService.Write(
                    ConfigPath,
                    stream => JsonSerializer.Serialize(
                        stream,
                        imported,
                        AppJsonContext.Default.AppConfig));
                _initialized = false;
                Initialize();

                LogService.Log(I18n.Format("Config_ImportSuccess", sourcePath));
                return true;
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Config_ImportFailed", ex.Message));
                return false;
            }
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
