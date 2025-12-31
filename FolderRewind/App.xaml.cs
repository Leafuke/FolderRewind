using FolderRewind;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Globalization;
using Windows.Graphics;
using Windows.System.UserProfile;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FolderRewind
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {

        public static Window _window { get; set; }

        // 暴露 ShellPage 以便子页面控制导航
        public static Views.ShellPage Shell { get; set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // 尽量早地捕获“秒退无窗口”的异常（不依赖 XAML/Window 是否已初始化）
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    try
                    {
                        var ex = e.ExceptionObject as Exception;
                        LogService.Log($"[AppDomain UnhandledException] {ex?.Message}\n{ex?.StackTrace}");
                    }
                    catch { }
                };

                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    try
                    {
                        LogService.Log($"[Task UnobservedException] {e.Exception.Message}\n{e.Exception.StackTrace}");
                    }
                    catch { }
                };
            }
            catch
            {
                // ignore any handler setup failure
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 尽量提前挂载未处理异常处理器，避免初始化阶段直接崩溃而无日志
            this.UnhandledException += App_UnhandledException;

            try
            {
                LogService.Log("[App] OnLaunched begin");

                Services.ConfigService.Initialize();
                LogService.MarkSessionStart();

                // 插件系统初始化：尽量早，但不影响主窗口创建。
                // 这里使用同步扫描+按启用状态加载（异常会写入 LogService）。
                PluginService.Initialize();

                var startupSettings = Services.ConfigService.CurrentConfig?.GlobalSettings;
                if (startupSettings != null)
                {
                    var startupApplied = Services.StartupService.SetStartup(startupSettings.RunOnStartup);
                    if (!startupApplied && startupSettings.RunOnStartup)
                    {
                        startupSettings.RunOnStartup = false;
                        Services.ConfigService.Save();
                    }
                }

                ApplyLanguageOverride(Services.ConfigService.CurrentConfig.GlobalSettings.Language);

                _window = new MainWindow();

                ApplyWindowPreferences(_window);

                Services.AutomationService.Start();

                Services.ThemeService.ApplyThemeToWindow(_window);
                Services.TypographyService.ApplyTypography(Services.ConfigService.CurrentConfig?.GlobalSettings);

                UpdateWindowTitle();

                _window.Activate();

                LogService.Log("[App] OnLaunched end");
            }
            catch (Exception ex)
            {
                try
                {
                    LogService.Log($"[App Fatal] {ex.Message}\n{ex.StackTrace}");
                }
                catch
                {
                    // ignore
                }

                // 最小错误窗口
                try
                {
                    _window = new Window();
                    _window.Title = "FolderRewind - 启动失败";
                    _window.Content = new ScrollViewer
                    {
                        Padding = new Thickness(24),
                        Content = new TextBlock
                        {
                            Text = $"启动失败：{ex.Message}\n\n{ex.StackTrace}",
                            TextWrapping = TextWrapping.Wrap
                        }
                    };
                    _window.Activate();
                }
                catch
                {
                    // 如果连窗口都创建不了，只能放弃（此时至少尝试写过日志）
                }
            }
        }

        // 调试未处理异常 - 发现是历史记录炸了。。。吗？
        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[App UnhandledException] {e.Exception.Message}\n{e.Exception.StackTrace}");
            // 写入自定义日志文件
            LogService.Log($"[App UnhandledException] {e.Exception.Message}\n{e.Exception.StackTrace}");
            e.Handled = true; // 防止直接崩溃
        }

        private static void ApplyLanguageOverride(string? languageSetting)
        {
            var normalized = NormalizeLanguage(languageSetting);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
                return;
            }

            ApplicationLanguages.PrimaryLanguageOverride = normalized;
        }

        private static string NormalizeLanguage(string? languageSetting)
        {
            if (string.IsNullOrWhiteSpace(languageSetting)) return string.Empty;

            var value = languageSetting.Trim();
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            // legacy values
            if (string.Equals(value, "zh_CN", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (string.Equals(value, "en_US", StringComparison.OrdinalIgnoreCase)) return "en-US";

            // normalize separator
            value = value.Replace('_', '-');

            if (string.Equals(value, "zh-CN", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (string.Equals(value, "en-US", StringComparison.OrdinalIgnoreCase)) return "en-US";

            return value;
        }

        public static void UpdateWindowTitle()
        {
            if (_window == null) return;

            var title = GetLocalizedWindowTitle();

            try
            {
                _window.Title = title;
            }
            catch
            {
                // ignore
            }

            try
            {
                _window.AppWindow.Title = title;
            }
            catch
            {
                // ignore
            }
        }

        private static string GetLocalizedWindowTitle()
        {
            var language = Services.ConfigService.CurrentConfig?.GlobalSettings?.Language;

            if (string.IsNullOrWhiteSpace(language) || string.Equals(language, "system", StringComparison.OrdinalIgnoreCase))
            {
                language = ApplicationLanguages.PrimaryLanguageOverride;
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = GlobalizationPreferences.Languages?.FirstOrDefault() ?? string.Empty;
                }
            }

            var normalized = language.ToLowerInvariant();
            if (normalized.StartsWith("zh")) return "存档时光机";
            return "FolderRewind";
        }

        private static void ApplyWindowPreferences(Window window)
        {
            if (window == null) return;

            var settings = Services.ConfigService.CurrentConfig?.GlobalSettings;
            if (settings == null) return;

            double width = Math.Clamp(settings.StartupWidth, 640, 3840);
            double height = Math.Clamp(settings.StartupHeight, 480, 2160);

            try
            {
                var appWindow = window.AppWindow;
                if (appWindow != null)
                {
                    appWindow.Resize(new SizeInt32((int)Math.Round(width), (int)Math.Round(height)));
                }
                else
                {
                    
                }
            }
            catch
            {
                
            }
        }
    }
}
