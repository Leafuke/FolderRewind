using FolderRewind.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static class ConfigService
    {
        private static string ConfigFileName = "config.json";
        // 建议保存在 LocalAppData 中，避免权限问题。如果想做便携版，可以改为 AppContext.BaseDirectory
        private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        private static bool _initialized;

        public static AppConfig CurrentConfig { get; private set; }

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
                    CurrentConfig = JsonSerializer.Deserialize<AppConfig>(jsonString);
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
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true, // 格式化 JSON，方便人类阅读
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 防止中文被转义
                };
                string jsonString = JsonSerializer.Serialize(CurrentConfig, options);
                File.WriteAllText(ConfigPath, jsonString);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Config save error: {ex.Message}");
                LogService.Log($"[Config] 配置保存失败：{ex.Message}");
            }
        }
    }
}