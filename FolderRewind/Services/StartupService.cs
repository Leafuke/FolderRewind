using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace FolderRewind.Services
{
    public static class StartupService
    {
        private const string StartupTaskId = "FolderRewindStartupTask";
        private const string ClassicStartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ClassicStartupValueName = "FolderRewind";
        internal const string ClassicStartupArgument = "--startup";

        // <param name="enable">True to enable startup, false to disable.</param>
        // <returns>True if the operation succeeded.</returns>
        public static bool SetStartup(bool enable)
        {
            try
            {
                return Task.Run(() => SetStartupAsync(enable)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Startup set failed: {ex.Message}");
                LogService.Log(I18n.Format("Startup_SetFailed", ex.Message));
                return false;
            }
        }
        public static async Task<bool> SetStartupAsync(bool enable)
        {
            if (AppRuntimeInfo.IsMsiDistribution)
            {
                return SetClassicStartup(enable);
            }

            try
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);

                if (enable)
                {
                    var state = await startupTask.RequestEnableAsync();

                    switch (state)
                    {
                        case StartupTaskState.Enabled:
                        case StartupTaskState.EnabledByPolicy:
                            return true;
                        case StartupTaskState.DisabledByUser:
                            LogService.Log(I18n.GetString("Startup_DisabledByUser"));
                            return false;
                        case StartupTaskState.DisabledByPolicy:
                            LogService.Log(I18n.GetString("Startup_DisabledByPolicy"));
                            return false;
                        default:
                            return false;
                    }
                }
                else
                {
                    startupTask.Disable();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Startup async set failed: {ex.Message}");
                LogService.Log(I18n.Format("Startup_SetFailed", ex.Message));
                return false;
            }
        }
        public static bool IsStartupEnabled()
        {
            try
            {
                return Task.Run(() => IsStartupEnabledAsync()).GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }
        public static async Task<bool> IsStartupEnabledAsync()
        {
            var probe = await TryGetStartupEnabledAsync();
            return probe.success && probe.enabled;
        }

        public static async Task<(bool success, bool enabled)> TryGetStartupEnabledAsync()
        {
            if (AppRuntimeInfo.IsMsiDistribution)
            {
                return (true, IsClassicStartupEnabled());
            }

            try
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);
                var enabled = startupTask.State == StartupTaskState.Enabled ||
                              startupTask.State == StartupTaskState.EnabledByPolicy;
                return (true, enabled);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Startup state probe failed: {ex.Message}");
                return (false, false);
            }
        }

        public static async Task<StartupTaskState> GetStartupStateAsync()
        {
            if (AppRuntimeInfo.IsMsiDistribution)
            {
                return IsClassicStartupEnabled() ? StartupTaskState.Enabled : StartupTaskState.Disabled;
            }

            try
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);
                return startupTask.State;
            }
            catch
            {
                return StartupTaskState.Disabled;
            }
        }

        private static bool SetClassicStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(ClassicStartupRunKey, writable: true);
                if (key == null) return false;

                if (!enable)
                {
                    key.DeleteValue(ClassicStartupValueName, throwOnMissingValue: false);
                    return true;
                }

                var executablePath = AppRuntimeInfo.ExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    throw new FileNotFoundException("The application executable was not found.", executablePath);
                }

                key.SetValue(
                    ClassicStartupValueName,
                    $"\"{executablePath}\" {ClassicStartupArgument}",
                    RegistryValueKind.String);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Classic startup set failed: {ex.Message}");
                LogService.Log(I18n.Format("Startup_SetFailed", ex.Message));
                return false;
            }
        }

        private static bool IsClassicStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassicStartupRunKey, writable: false);
                return !string.IsNullOrWhiteSpace(key?.GetValue(ClassicStartupValueName) as string);
            }
            catch
            {
                return false;
            }
        }
    }
}
