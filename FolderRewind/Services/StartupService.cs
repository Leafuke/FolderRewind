using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace FolderRewind.Services
{
    public static class StartupService
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "FolderRewind";

        public static void SetStartup(bool enable)
        {
            try
            {
                // 获取当前可执行文件路径
                // 对于 WinUI 3 Unpackaged，这通常是 .exe 路径
                string exePath = Process.GetCurrentProcess().MainModule.FileName;

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (enable)
                    {
                        key.SetValue(AppName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Startup set failed: {ex.Message}");
            }
        }
    }
}