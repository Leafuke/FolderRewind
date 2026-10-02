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
        private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        internal const string ClassicStartupArgument = "--startup";

        // <param name="enable">True to enable startup, false to disable.</param>
        // <returns>True if the operation succeeded.</returns>
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
        public static async Task<bool> IsStartupEnabledAsync()
        {
            var probe = await TryGetStartupEnabledAsync();
            return probe.success && probe.enabled;
        }

        public static async Task<(bool success, bool enabled)> TryGetStartupEnabledAsync()
        {
            if (AppRuntimeInfo.IsMsiDistribution)
            {
                return TryGetClassicStartupState(out var state) ? (true, state == StartupTaskState.Enabled) : (false, false);
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
                if (!TryGetClassicStartupState(out var state)) throw new IOException("Unable to read startup state.");
                return state;
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
                    if (ClassicStartupPolicy.IsOwnedCommand(key.GetValue(ClassicStartupValueName) as string, AppRuntimeInfo.ExecutablePath))
                        key.DeleteValue(ClassicStartupValueName, throwOnMissingValue: false);
                    return true;
                }

                if (!TryGetClassicStartupState(out _))
                    return false;
                var existing = key.GetValue(ClassicStartupValueName) as string;
                if (!string.IsNullOrEmpty(existing) && !ClassicStartupPolicy.IsOwnedCommand(existing, AppRuntimeInfo.ExecutablePath))
                    throw new IOException("The startup entry belongs to another installation.");
                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: false);
                var approval = approved?.GetValue(ClassicStartupValueName);
                if (approval != null && approval is not byte[]) return false;
                if (ClassicStartupPolicy.Evaluate($"\"{AppRuntimeInfo.ExecutablePath}\" {ClassicStartupArgument}",
                    AppRuntimeInfo.ExecutablePath, approval as byte[]) is ClassicStartupState.DisabledByUser)
                {
                    // Make a removed entry visible in Windows startup settings,
                    // while preserving the user's disabled approval marker.
                    if (existing == null && File.Exists(AppRuntimeInfo.ExecutablePath))
                        key.SetValue(ClassicStartupValueName, $"\"{AppRuntimeInfo.ExecutablePath}\" {ClassicStartupArgument}", RegistryValueKind.String);
                    return false;
                }
                if (ClassicStartupPolicy.Evaluate($"\"{AppRuntimeInfo.ExecutablePath}\" {ClassicStartupArgument}",
                    AppRuntimeInfo.ExecutablePath, approval as byte[]) == ClassicStartupState.Unknown) return false;

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

        private static bool TryGetClassicStartupState(out StartupTaskState state)
        {
            state = StartupTaskState.Disabled;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassicStartupRunKey, writable: false);
                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: false);
                var run = key?.GetValue(ClassicStartupValueName);
                var approval = approved?.GetValue(ClassicStartupValueName);
                if ((run != null && run is not string) || (approval != null && approval is not byte[]))
                    throw new IOException("Invalid startup registry value type.");
                state = ClassicStartupPolicy.Evaluate(run as string,
                    AppRuntimeInfo.ExecutablePath, approval as byte[]) switch
                {
                    ClassicStartupState.Enabled => StartupTaskState.Enabled,
                    ClassicStartupState.DisabledByUser => StartupTaskState.DisabledByUser,
                    ClassicStartupState.Disabled => StartupTaskState.Disabled,
                    _ => throw new IOException("Unknown StartupApproved state.")
                };
                if (run == null && ClassicStartupPolicy.Evaluate($"\"{AppRuntimeInfo.ExecutablePath}\" {ClassicStartupArgument}",
                    AppRuntimeInfo.ExecutablePath, approval as byte[]) == ClassicStartupState.DisabledByUser)
                    state = StartupTaskState.DisabledByUser;
                return true;
            }
            catch (Exception error)
            {
                LogService.LogError("Startup state could not be read.", nameof(StartupService), error);
                return false;
            }
        }
    }
}
