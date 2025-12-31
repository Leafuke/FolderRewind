using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using FolderRewind.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FolderRewind
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            ThemeService.ThemeChanged += ThemeService_ThemeChanged;
            
            // Apply initial theme
            var currentTheme = ThemeService.GetCurrentTheme();
            ThemeService.ApplyThemeToWindow(this);
            UpdateTitleBar(currentTheme);
        }

        private void ThemeService_ThemeChanged(ElementTheme theme)
        {
            ThemeService.ApplyThemeToWindow(this);
            UpdateTitleBar(theme);
        }

        private void UpdateTitleBar(ElementTheme theme)
        {
            if (AppWindow.TitleBar != null)
            {
                // Ensure we are using the system title bar colors, or custom if extended
                // If not extended, we can set the colors.
                // If extended, we need to set the button colors.
                
                // Let's assume we want to colorize the standard title bar or the buttons of an extended one.
                // Since the user asked for "Windows window title bar", they likely mean the standard one or the caption buttons.
                
                var titleBar = AppWindow.TitleBar;
                
                // Check if we are in Dark mode
                bool isDark = theme == ElementTheme.Dark;
                // If theme is Default, we should check system setting, but for now let's assume Light if not Dark, 
                // or better, check actual requested theme. 
                // ThemeService.GetCurrentTheme() returns Dark or Light based on index.
                
                var backgroundColor = isDark ? Colors.Black : Colors.White;
                var foregroundColor = isDark ? Colors.White : Colors.Black;
                var inactiveBackgroundColor = isDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
                var inactiveForegroundColor = isDark ? Color.FromArgb(255, 128, 128, 128) : Color.FromArgb(255, 100, 100, 100);

                // Button colors
                titleBar.ButtonBackgroundColor = backgroundColor;
                titleBar.ButtonForegroundColor = foregroundColor;
                titleBar.ButtonInactiveBackgroundColor = inactiveBackgroundColor;
                titleBar.ButtonInactiveForegroundColor = inactiveForegroundColor;
                
                titleBar.ButtonHoverBackgroundColor = isDark ? Color.FromArgb(255, 50, 50, 50) : Color.FromArgb(255, 230, 230, 230);
                titleBar.ButtonHoverForegroundColor = foregroundColor;
                titleBar.ButtonPressedBackgroundColor = isDark ? Color.FromArgb(255, 80, 80, 80) : Color.FromArgb(255, 200, 200, 200);
                titleBar.ButtonPressedForegroundColor = foregroundColor;

                // Title bar background (if not extended)
                titleBar.BackgroundColor = backgroundColor;
                titleBar.ForegroundColor = foregroundColor;
                titleBar.InactiveBackgroundColor = inactiveBackgroundColor;
                titleBar.InactiveForegroundColor = inactiveForegroundColor;
            }
        }
    }
}
