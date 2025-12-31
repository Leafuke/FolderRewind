using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace FolderRewind.Services
{
    public static class StartupService
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "FolderRewind";

        public static bool SetStartup(bool enable)
        {
            try
            {
                // 获取当前可执行文件路径
                // 对于 WinUI 3 Unpackaged，这通常是 .exe 路径
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    Debug.WriteLine("Startup set failed: unable to resolve process path.");
                    return false;
                }

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        Debug.WriteLine("Startup set failed: cannot open Run key.");
                        return false;
                    }

                    if (enable)
                    {
                        key.SetValue(AppName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Startup set failed: {ex.Message}");
                LogService.Log($"[Startup] 设置开机自启失败：{ex.Message}");
                return false;
            }
        }

        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var value = key?.GetValue(AppName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }
}