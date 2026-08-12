using FolderRewind.Plugin.Runtime.Activation;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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

    public static bool TryStartSafeModeInstance(out string error)
    {
        error = string.Empty;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                error = "The current executable path is unavailable.";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--safe-mode",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
