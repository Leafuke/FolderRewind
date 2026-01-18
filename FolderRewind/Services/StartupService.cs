using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FolderRewind.Services
{
    public static class StartupService
    {
        private const string StartupTaskId = "FolderRewindStartupTask";

        // <param name="enable">True to enable startup, false to disable.</param>
        // <returns>True if the operation succeeded.</returns>
        public static bool SetStartup(bool enable)
        {
            try
            {
                var task = Task.Run(async () => await SetStartupAsync(enable)).GetAwaiter().GetResult();
                return task;
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
                var task = Task.Run(async () => await IsStartupEnabledAsync()).GetAwaiter().GetResult();
                return task;
            }
            catch
            {
                return false;
            }
        }
        public static async Task<bool> IsStartupEnabledAsync()
        {
            try
            {
                var startupTask = await StartupTask.GetAsync(StartupTaskId);
                return startupTask.State == StartupTaskState.Enabled || 
                       startupTask.State == StartupTaskState.EnabledByPolicy;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<StartupTaskState> GetStartupStateAsync()
        {
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
    }
}