using FolderRewind.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace FolderRewind.Services
{
    public static class ConfigService
    {
        #region 常量与状态

        private const string ConfigFileName = "config.json";
        //  LocalAppData  AppContext.BaseDirectory
        private static string ConfigPath => Path.Combine(GetWritableAppDataDir(), "FolderRewind", ConfigFileName);

        private static bool _initialized;

        public static AppConfig CurrentConfig { get; private set; } = null!;

        public static string ConfigFilePath => ConfigPath;

        public static string ConfigDirectory => Path.GetDirectoryName(ConfigPath)!;

        #endregion

        #region 路径与默认值

        public static string GetRecommendedDefaultBackupRootPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FolderRewind-Backup");
        }

        public static string GetRecommendedRestoreTempRootPath()
        {
            return Path.Combine(Path.GetTempPath(), "FolderRewind", "RestoreSnapshots");
        }

        public static string BuildDefaultDestinationPath(string? configName)
        {
            var safeName = MakeSafeFolderName(configName);
            return Path.Combine(CurrentConfig?.GlobalSettings?.DefaultBackupRootPath ?? GetRecommendedDefaultBackupRootPath(), safeName);
        }

        #endregion

        #region 初始化与迁移

        /// <summary>
        /// 初始化配置服务，加载或创建默认配置
        /// </summary>
        public static void Initialize()
        {
            // 避免在应用运行中重复初始化导致 CurrentConfig 被替换，进而破坏页面绑定与导航参数引用
            if (_initialized && CurrentConfig != null) return;

            if (File.Exists(ConfigPath))
            {
                try
                {
                    string jsonString = File.ReadAllText(ConfigPath);
                    var loadedConfig = JsonSerializer.Deserialize(jsonString, AppJsonContext.Default.AppConfig);

                    // 反序列化在某些内容下可能返回 null（例如文件内容是字面量 "null"），
                    // 这会导致后续访问 CurrentConfig.* 直接崩溃（打包安装后更容易遇到）。
                    if (loadedConfig == null)
                    {
                        LogService.Log(I18n.GetString("Config_ParseNull_Reset"));
                        CreateDefaultConfig();
                    }
                    else
                    {
                        CurrentConfig = loadedConfig;
                    }
                }
                catch (Exception ex)
                {
                    // 这里应该记录日志：配置文件损坏
                    System.Diagnostics.Debug.WriteLine($"Config load error: {ex.Message}");
                    LogService.Log(I18n.Format("Config_LoadFailed_Reset", ex.Message));
                    CreateDefaultConfig();
                }
            }
            else
            {
                CreateDefaultConfig();
            }

            // 兜底：确保无论如何 CurrentConfig 都不为 null
            if (CurrentConfig == null)
            {
                LogService.Log(I18n.GetString("Config_CurrentConfigNull_ForceDefault"));
                CreateDefaultConfig();
            }

            var currentConfig = CurrentConfig;
            if (currentConfig == null)
            {
                CreateDefaultConfig();
                currentConfig = CurrentConfig ?? throw new InvalidOperationException("CurrentConfig was not initialized.");
            }

            // 修正反序列化后集合为 null 或类型不兼容的情况，防止绑定崩溃
            if (currentConfig.BackupConfigs == null)
                currentConfig.BackupConfigs = new System.Collections.ObjectModel.ObservableCollection<BackupConfig>();
            else if (currentConfig.BackupConfigs.GetType() != typeof(System.Collections.ObjectModel.ObservableCollection<BackupConfig>))
                currentConfig.BackupConfigs = new System.Collections.ObjectModel.ObservableCollection<BackupConfig>(currentConfig.BackupConfigs);

            foreach (var config in currentConfig.BackupConfigs)
            {
                if (config.SourceFolders == null)
                    config.SourceFolders = new System.Collections.ObjectModel.ObservableCollection<ManagedFolder>();
                else if (config.SourceFolders.GetType() != typeof(System.Collections.ObjectModel.ObservableCollection<ManagedFolder>))
                    config.SourceFolders = new System.Collections.ObjectModel.ObservableCollection<ManagedFolder>(config.SourceFolders);
                if (config.ExtendedProperties == null)
                    config.ExtendedProperties = new System.Collections.Generic.Dictionary<string, string>();
                if (config.Archive == null)
                    config.Archive = new ArchiveSettings();
                if (config.Automation == null)
                    config.Automation = new AutomationSettings();

                // Migrate and fix ScheduleEntries
                if (config.Automation.ScheduleEntries == null)
                    config.Automation.ScheduleEntries = new System.Collections.ObjectModel.ObservableCollection<ScheduleEntry>();
                else if (config.Automation.ScheduleEntries.GetType() != typeof(System.Collections.ObjectModel.ObservableCollection<ScheduleEntry>))
                    config.Automation.ScheduleEntries = new System.Collections.ObjectModel.ObservableCollection<ScheduleEntry>(config.Automation.ScheduleEntries);
                config.Automation.MigrateFromLegacy();

                if (config.Filters == null)
                    config.Filters = new FilterSettings();

                if (config.Cloud == null)
                    config.Cloud = new CloudSettings();
                else
                    NormalizeCloudSettings(config.Cloud);

                // 兼容旧版配置--还原白名单可能为 null
                if (config.Filters.RestoreWhitelist == null)
                    config.Filters.RestoreWhitelist = new System.Collections.ObjectModel.ObservableCollection<string>();
                else if (config.Filters.RestoreWhitelist.GetType() != typeof(System.Collections.ObjectModel.ObservableCollection<string>))
                    config.Filters.RestoreWhitelist = new System.Collections.ObjectModel.ObservableCollection<string>(config.Filters.RestoreWhitelist);

                // 兼容旧版配置：自定义文件类型处理规则可能为 null
                if (config.Archive.FileTypeRules == null)
                    config.Archive.FileTypeRules = new System.Collections.ObjectModel.ObservableCollection<FileTypeRule>();
                else if (config.Archive.FileTypeRules.GetType() != typeof(System.Collections.ObjectModel.ObservableCollection<FileTypeRule>))
                    config.Archive.FileTypeRules = new System.Collections.ObjectModel.ObservableCollection<FileTypeRule>(config.Archive.FileTypeRules);
            }
            if (currentConfig.GlobalSettings == null)
                currentConfig.GlobalSettings = new GlobalSettings();

            // 兼容旧版配置：Plugins 节点可能为 null
            if (currentConfig.GlobalSettings.Plugins == null)
                currentConfig.GlobalSettings.Plugins = new PluginHostSettings();

            // 兼容旧版配置：Hotkeys 节点可能为 null
            if (currentConfig.GlobalSettings.Hotkeys == null)
                currentConfig.GlobalSettings.Hotkeys = new HotkeySettings();

            if (currentConfig.GlobalSettings.Hotkeys.Bindings == null)
                currentConfig.GlobalSettings.Hotkeys.Bindings = new System.Collections.Generic.Dictionary<string, string>();

            // 兼容旧版配置：字典可能反序列化为 null
            if (currentConfig.GlobalSettings.Plugins.PluginEnabled == null)
                currentConfig.GlobalSettings.Plugins.PluginEnabled = new System.Collections.Generic.Dictionary<string, bool>();
            if (currentConfig.GlobalSettings.Plugins.PluginSettings == null)
                currentConfig.GlobalSettings.Plugins.PluginSettings = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>();

            NormalizeGlobalSettings(currentConfig.GlobalSettings);

            ApplyLogSettings(currentConfig.GlobalSettings);

            _initialized = true;

            // 监听保存 (简单的自动保存策略，也可以改为手动调用 Save)
            // 这里暂不自动监听，由 ViewModel 在修改关键数据后调用 Save()
        }

        private static void CreateDefaultConfig()
        {
            CurrentConfig = new AppConfig();

            try
            {
                CurrentConfig.GlobalSettings.SevenZipPath = "7za.exe";
                CurrentConfig.GlobalSettings.FontFamily = FontService.GetRecommendedDefaultFontFamily();
                CurrentConfig.GlobalSettings.DefaultBackupRootPath = GetRecommendedDefaultBackupRootPath();
                CurrentConfig.GlobalSettings.RestoreTempRootPath = GetRecommendedRestoreTempRootPath();
            }
            catch
            {

            }

            // 示例配置
            var defaultConfig = new BackupConfig
            {
                Name = I18n.Format("Config_DefaultBackupName"),
                DestinationPath = BuildDefaultDestinationPath(I18n.Format("Config_DefaultBackupName")),
                SummaryText = ""
            };
            // 默认 7z 压缩
            defaultConfig.Archive.Format = "7z";
            defaultConfig.Archive.CompressionLevel = 5;

            CurrentConfig.BackupConfigs.Add(defaultConfig);
            Save();
        }

        private static void NormalizeCloudSettings(CloudSettings cloud)
        {
            if (string.IsNullOrWhiteSpace(cloud.ExecutablePath))
                cloud.ExecutablePath = "rclone.exe";

            if (string.IsNullOrWhiteSpace(cloud.RemoteBasePath))
                cloud.RemoteBasePath = "remote:FolderRewind";

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

        #endregion

        #region 持久化与重载

        public static void Save()
        {
            if (CurrentConfig == null)
            {
                LogService.Log(I18n.GetString("Config_Save_CurrentConfigNull"));
                return;
            }

            try
            {
                var configDir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir!);
                }
                string jsonString = JsonSerializer.Serialize(CurrentConfig, AppJsonContext.Default.AppConfig);
                string tempPath = ConfigPath + ".tmp";
                File.WriteAllText(tempPath, jsonString);
                File.Move(tempPath, ConfigPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Config save error: {ex.Message}");
                LogService.Log(I18n.Format("Config_SaveFailed", ex.Message));
            }
        }

        public static bool Reload()
        {
            _initialized = false;
            Initialize();
            return _initialized && CurrentConfig != null;
        }

        #endregion

        #region 配置文件访问

        public static void OpenConfigFolder()
        {
            try
            {
                if (!Directory.Exists(ConfigDirectory)) Directory.CreateDirectory(ConfigDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = ConfigDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = ConfigPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
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
                if (CurrentConfig == null) return false;
                string json = JsonSerializer.Serialize(CurrentConfig, AppJsonContext.Default.AppConfig);
                File.WriteAllText(destPath, json);
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
                string json = File.ReadAllText(sourcePath);
                var imported = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (imported == null)
                {
                    LogService.Log(I18n.GetString("Config_ImportFailed_Null"));
                    return false;
                }

                // 备份当前配置
                string backupPath = ConfigPath + ".bak";
                try { File.Copy(ConfigPath, backupPath, true); } catch { }

                // 写入新配置并重新加载
                File.WriteAllText(ConfigPath, json);
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
            if (settings == null) return;

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

            if (string.IsNullOrWhiteSpace(settings.DefaultBackupRootPath))
            {
                settings.DefaultBackupRootPath = GetRecommendedDefaultBackupRootPath();
            }

            if (string.IsNullOrWhiteSpace(settings.RestoreTempRootPath))
            {
                settings.RestoreTempRootPath = GetRecommendedRestoreTempRootPath();
            }

            if (double.IsNaN(settings.BaseFontSize) || settings.BaseFontSize <= 0)
            {
                settings.BaseFontSize = 14;
            }
            else
            {
                settings.BaseFontSize = Math.Clamp(settings.BaseFontSize, 12, 20);
            }

            // Startup size: clamp to reasonable desktop values
            if (double.IsNaN(settings.StartupWidth) || settings.StartupWidth < 640 || settings.StartupWidth > 3840)
            {
                settings.StartupWidth = 1200;
            }

            if (double.IsNaN(settings.StartupHeight) || settings.StartupHeight < 480 || settings.StartupHeight > 2160)
            {
                settings.StartupHeight = 800;
            }

            if (string.IsNullOrWhiteSpace(settings.HomeSortMode))
            {
                settings.HomeSortMode = "NameAsc";
            }

            // Toast notification level: clamp to valid range (0-3)
            settings.ToastNotificationLevel = Math.Clamp(settings.ToastNotificationLevel, 0, 3);
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

        private static string GetWritableAppDataDir()
        {
            // Packaged (MSIX) 下：LocalState
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                if (!string.IsNullOrWhiteSpace(localFolder?.Path)) return localFolder.Path;
            }
            catch
            {
            }

            // Unpackaged 下：常规 LocalAppData
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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