using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FolderRewind
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {

        #region 全局状态与共享入口

        public static Window _window { get; set; }

        /// <summary>
        /// 获取主窗口实例（用于 NotificationService 等服务判断窗口状态）
        /// </summary>
        public static MainWindow? MainWindow => _window as MainWindow;

        // 暴露 ShellPage 以便子页面控制导航
        public static Views.ShellPage Shell { get; set; }

        private TaskbarIcon? _trayIcon;
        internal static bool ForceExitRequested { get; private set; }

        #endregion

        #region 构造与全局异常捕获

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
                        LogService.Log(I18n.Format(
                            "App_Log_AppDomainUnhandledException",
                            ex?.Message ?? string.Empty,
                            ex?.StackTrace ?? string.Empty));
                    }
                    catch { }
                };

                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    try
                    {
                        LogService.Log(I18n.Format(
                            "App_Log_TaskUnobservedException",
                            e.Exception.Message,
                            e.Exception.StackTrace ?? string.Empty));
                    }
                    catch { }
                };
            }
            catch
            {

            }
        }

        #endregion

        #region 应用生命周期

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 尽量提前挂载未处理异常处理器，避免初始化阶段直接崩溃而无日志
            this.UnhandledException += App_UnhandledException;

            ForceExitRequested = false;

            try
            {
                Services.ConfigService.Initialize();

                LogService.Log(I18n.GetString("App_Log_OnLaunchedBegin"));
                LogService.MarkSessionStart();

                ApplyLanguageOverride(Services.ConfigService.CurrentConfig.GlobalSettings.Language);

                _window = new MainWindow();
                _window.Closed += OnMainWindowClosed;
                ApplyWindowPreferences(_window);
                Services.ThemeService.ApplyThemeToWindow(_window);
                UpdateWindowTitle();
                _window.Activate();

                _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    Services.TypographyService.ApplyTypography(Services.ConfigService.CurrentConfig?.GlobalSettings);
                });

                // 插件初始化包含热键注册，必须在UI线程执行，所以用DispatcherQueue而非Task.Run
                _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        PluginService.Initialize();
                    }
                    catch (Exception pluginEx)
                    {
                        LogService.Log(I18n.Format("App_Log_PluginInitException", pluginEx.Message));
                    }

                    // 注册 Mini 窗口全局热键处理器
                    try
                    {
                        Services.MiniWindowService.RegisterHotkey();
                    }
                    catch (Exception miniEx)
                    {
                        LogService.Log(I18n.Format("MiniWindow_Log_HotkeyRegisterFailed", miniEx.Message));
                    }
                });

                var startupSettings = Services.ConfigService.CurrentConfig?.GlobalSettings;
                if (startupSettings != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var isEnabled = await Services.StartupService.IsStartupEnabledAsync();
                            if (startupSettings.RunOnStartup != isEnabled)
                            {
                                startupSettings.RunOnStartup = isEnabled;
                                Services.ConfigService.Save();
                            }
                        }
                        catch { }
                    });
                }

                _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, InitializeTrayIcon);

                _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Services.AutomationService.Start);

                // 初始化 KnotLink 互联服务（根据用户设置决定是否启用）
                Task.Run(() =>
                {
                    try
                    {
                        KnotLinkService.Initialize();
                    }
                    catch (Exception knotEx)
                    {
                        LogService.Log(I18n.Format("App_Log_KnotLinkInitException", knotEx.Message));
                    }
                });

                LogService.Log(I18n.GetString("App_Log_OnLaunchedEnd"));
            }
            catch (Exception ex)
            {
                try
                {
                    LogService.Log(I18n.Format(
                        "App_Log_Fatal",
                        ex.Message,
                        ex.StackTrace ?? string.Empty));
                }
                catch
                {

                }

                // 最小错误窗口
                try
                {
                    _window = new Window();
                    _window.Title = I18n.GetString("App_StartupFailedWindowTitle");
                    _window.Content = new ScrollViewer
                    {
                        Padding = new Thickness(24),
                        Content = new TextBlock
                        {
                            Text = I18n.Format("App_StartupFailedWindowContent", ex.Message, ex.StackTrace ?? string.Empty),
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
            LogService.Log(I18n.Format(
                "App_Log_UnhandledException",
                e.Exception.Message,
                e.Exception.StackTrace ?? string.Empty));
            e.Handled = true; // 防止直接崩溃
        }

        #endregion

        #region 语言与窗口标题

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

            }

            try
            {
                _window.AppWindow.Title = title;
            }
            catch
            {

            }
        }

        private static string GetLocalizedWindowTitle()
        {
            return Services.I18n.Format("App_WindowTitle");
        }

        #endregion

        #region 主窗口偏好应用

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
            }
            catch
            {

            }

        }

        #endregion

        #region 托盘图标与命令

        private void InitializeTrayIcon()
        {
            if (_trayIcon != null) return;

            AttachTrayCommandHandlers();

            try
            {
                _trayIcon = Resources["TrayIcon"] as TaskbarIcon;
                _trayIcon?.ForceCreate();
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Tray_InitFailed", ex.Message));
            }
        }

        private void AttachTrayCommandHandlers()
        {
            if (Resources["ShowHideWindowCommand"] is XamlUICommand showHide)
            {
                showHide.ExecuteRequested -= OnShowHideWindowCommandExecuteRequested;
                showHide.ExecuteRequested += OnShowHideWindowCommandExecuteRequested;
            }

            if (Resources["QuitCommand"] is XamlUICommand quit)
            {
                quit.ExecuteRequested -= OnQuitCommandExecuteRequested;
                quit.ExecuteRequested += OnQuitCommandExecuteRequested;
            }
        }

        private void OnShowHideWindowCommandExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
        {
            ToggleWindowVisibility();
        }

        private void ToggleWindowVisibility()
        {
            if (_window?.AppWindow == null) return;

            try
            {
                var appWindow = _window.AppWindow;
                if (appWindow.IsVisible)
                {
                    appWindow.Hide();
                }
                else
                {
                    appWindow.Show();
                    _window.Activate();
                }
            }
            catch (Exception ex)
            {
                LogService.Log(I18n.Format("Tray_ToggleFailed", ex.Message));
            }
        }

        private void OnQuitCommandExecuteRequested(XamlUICommand sender, ExecuteRequestedEventArgs args)
        {
            ForceExitRequested = true;

            // 关闭所有 Mini 窗口并释放 FileSystemWatcher
            try { Services.MiniWindowService.CloseAll(); } catch { }

            CleanupTrayIcon();

            try
            {
                _window?.Close();
            }
            catch
            {
            }

            try
            {
                Exit();
            }
            catch
            {
            }
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            // 主窗口关闭时清理 Mini 窗口
            try { Services.MiniWindowService.CloseAll(); } catch { }
            CleanupTrayIcon();
        }

        private void CleanupTrayIcon()
        {
            try
            {
                _trayIcon?.Dispose();
            }
            catch
            {
            }

            _trayIcon = null;
        }

        #endregion

    }
}

