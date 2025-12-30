using FolderRewind.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace FolderRewind.Services
{
    public static class ConfigService
    {
        private static string ConfigFileName = "config.json";
        //  LocalAppData  AppContext.BaseDirectory
        private static string ConfigPath => Path.Combine(GetWritableAppDataDir(), "FolderRewind", ConfigFileName);

        private static bool _initialized;

        public static AppConfig CurrentConfig { get; private set; }

        public static string ConfigFilePath => ConfigPath;

        public static string ConfigDirectory => Path.GetDirectoryName(ConfigPath)!;

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
                    CurrentConfig = JsonSerializer.Deserialize(jsonString, AppJsonContext.Default.AppConfig);

                    // 反序列化在某些内容下可能返回 null（例如文件内容是字面量 "null"），
                    // 这会导致后续访问 CurrentConfig.* 直接崩溃（打包安装后更容易遇到）。
                    if (CurrentConfig == null)
                    {
                        LogService.Log("[Config] 配置文件解析结果为 null，将重置为默认配置。");
                        CreateDefaultConfig();
                    }
                }
                catch (Exception ex)
                {
                    // 这里应该记录日志：配置文件损坏
                    System.Diagnostics.Debug.WriteLine($"Config load error: {ex.Message}");
                    LogService.Log($"[Config] 配置加载失败，将重置为默认配置：{ex.Message}");
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
                LogService.Log("[Config] CurrentConfig 为空，已强制创建默认配置。");
                CreateDefaultConfig();
            }

            // 修正反序列化后集合为 null 或类型不兼容的情况，防止绑定崩溃
            if (CurrentConfig.BackupConfigs == null)
                CurrentConfig.BackupConfigs = new System.Collections.ObjectModel.ObservableCollection<BackupConfig>();
            else if (CurrentConfig.BackupConfigs.GetType() != typeof(System.Collections.ObjectModel.ObservableCollection<BackupConfig>))
                CurrentConfig.BackupConfigs = new System.Collections.ObjectModel.ObservableCollection<BackupConfig>(CurrentConfig.BackupConfigs);

            foreach (var config in CurrentConfig.BackupConfigs)
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
                if (config.Filters == null)
                    config.Filters = new FilterSettings();
            }
            if (CurrentConfig.GlobalSettings == null)
                CurrentConfig.GlobalSettings = new GlobalSettings();

            NormalizeGlobalSettings(CurrentConfig.GlobalSettings);

            ApplyLogSettings(CurrentConfig.GlobalSettings);

            _initialized = true;

            // 监听保存 (简单的自动保存策略，也可以改为手动调用 Save)
            // 这里暂不自动监听，由 ViewModel 在修改关键数据后调用 Save()
        }

        private static void CreateDefaultConfig()
        {
            CurrentConfig = new AppConfig();

            // 示例：创建一个默认配置引导用户
            var defaultConfig = new BackupConfig
            {
                Name = "示例配置",
                DestinationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyBackups"),
                SummaryText = "新创建"
            };
            // 默认 7z 压缩
            defaultConfig.Archive.Format = "7z";
            defaultConfig.Archive.CompressionLevel = 5;

            CurrentConfig.BackupConfigs.Add(defaultConfig);
            Save();
        }

        public static void Save()
        {
            if (CurrentConfig == null)
            {
                LogService.Log("[Config] CurrentConfig 为空，无法保存。");
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
                File.WriteAllText(ConfigPath, jsonString);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Config save error: {ex.Message}");
                LogService.Log($"[Config] 配置保存失败：{ex.Message}");
            }
        }

        public static bool Reload()
        {
            _initialized = false;
            Initialize();
            return _initialized && CurrentConfig != null;
        }

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
                LogService.Log($"[Config] 无法打开配置目录：{ex.Message}");
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
                LogService.Log($"[Config] 无法打开配置文件：{ex.Message}");
            }
        }

        private static void NormalizeGlobalSettings(GlobalSettings settings)
        {
            if (settings == null) return;

            // Theme: default to light when value is unexpected
            if (settings.ThemeIndex < 0 || settings.ThemeIndex > 1)
            {
                settings.ThemeIndex = 1;
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
                EnableDebugLogs = settings.EnableDebugLogs,
                MaxEntries = 4000,
                MaxFileSizeKb = Math.Max(512, settings.MaxLogFileSizeMb * 1024),
                RetentionDays = Math.Max(1, settings.LogRetentionDays)
            };

            LogService.ApplyOptions(options);
        }
    }
}