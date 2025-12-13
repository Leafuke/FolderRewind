using FolderRewind.Views;
using Microsoft.UI.Xaml;
using System;

namespace FolderRewind.Services
{
    /// <summary>
    /// Provides a single reusable instance of the log window so it is kept alive
    /// and we can safely reactivate it without reinitializing the visual tree.
    /// </summary>
    public static class LogWindowService
    {
        private static LogWindow _currentWindow;

        public static void Show()
        {
            if (_currentWindow != null)
            {
                _currentWindow.Activate();
                return;
            }

            try
            {
                _currentWindow = new LogWindow();
                _currentWindow.Closed += OnWindowClosed;
                _currentWindow.Activate();
            }
            catch (Exception ex)
            {
                _currentWindow = null;
                LogService.Log($"[LogWindow] Failed to open log window: {ex.Message}");
            }
        }

        private static void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_currentWindow != null)
            {
                _currentWindow.Closed -= OnWindowClosed;
                _currentWindow = null;
            }
        }
    }
}
