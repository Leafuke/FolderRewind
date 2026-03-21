using Microsoft.UI.Xaml;
using System;
using Windows.Graphics;
using WinRT.Interop;

namespace FolderRewind.Services
{
    public static class MainWindowService
    {
        // 由 App.OnLaunched 注入主窗口，供非视图层按需访问窗口能力。
        private static Window? _window;

        public static void Initialize(Window? window)
        {
            if (window != null)
            {
                // 启动后可能因窗口重建再次注入，这里允许覆盖旧引用。
                _window = window;
            }
        }

        private static Window? GetMainWindow()
        {
            return _window;
        }

        public static void UpdateWindowTitle()
        {
            App.UpdateWindowTitle();
        }

        public static XamlRoot? GetXamlRoot()
        {
            return GetMainWindow()?.Content?.XamlRoot;
        }

        public static void ApplyCurrentTheme()
        {
            ThemeService.ApplyThemeToWindow(GetMainWindow());
        }

        public static void Resize(double width, double height)
        {
            var window = GetMainWindow();
            if (window == null)
            {
                return;
            }

            try
            {
                window.AppWindow?.Resize(new SizeInt32((int)Math.Round(width), (int)Math.Round(height)));
            }
            catch
            {
            }
        }

        public static bool IsMainWindowVisible()
        {
            try
            {
                var window = GetMainWindow();
                if (window?.AppWindow != null)
                {
                    return window.AppWindow.IsVisible;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        public static void InitializePicker(object picker)
        {
            if (picker == null)
            {
                return;
            }

            var window = GetMainWindow();
            if (window == null)
            {
                return;
            }

            // WinUI 桌面应用中的 Picker 需要显式绑定窗口句柄，否则无法正常弹出。
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        }
    }
}