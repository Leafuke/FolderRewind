using FolderRewind;
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
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Globalization;

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
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            Services.ConfigService.Initialize();

            ApplyLanguageOverride(Services.ConfigService.CurrentConfig.GlobalSettings.Language);

            _window = new MainWindow();

            Services.AutomationService.Start();

            Services.ThemeService.ApplyThemeToWindow(_window);

            _window.Activate();
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
    }
}
