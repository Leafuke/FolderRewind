using FolderRewind.Plugin.Runtime.Activation;
using System.Collections.Generic;

namespace FolderRewind.Services;

public static class PluginRuntimeModeService
{
    public static bool IsSafeMode { get; private set; }

    public static void Initialize(IEnumerable<string> arguments)
    {
        if (SafeModePolicy.IsRequested(arguments))
        {
            IsSafeMode = true;
        }
    }

    public static System.Threading.Tasks.Task RestartSafeModeAsync() => App.ExitApplicationAsync(restartSafeMode: true);
}
