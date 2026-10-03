using FolderRewind.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FolderRewind;

internal static class Program
{
    internal const string AppUserModelId = "Leafuke.FolderRewind.Msi";
    internal static SingleInstanceCoordinator? Instance { get; private set; }
    internal static bool RestartSafeModeOnExit { get; set; }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            using var userIdentity = WindowsIdentity.GetCurrent();
            // Share the guard across builds and installation paths for this user.
            string identity = "Leafuke.FolderRewind." + (userIdentity.User?.Value ?? Environment.UserName);
            using var instance = SingleInstanceCoordinator.TryAcquire(identity);
            if (instance == null)
            {
                bool startup = IsStartupLaunch(args);
                bool accepted = SingleInstanceCoordinator.NotifyAsync(identity,
                    startup,
                    allowForeground: process => AllowSetForegroundWindow(process)).GetAwaiter().GetResult();
                if (!accepted && !startup) MessageBox(IntPtr.Zero, "现有实例正在启动或退出，请稍后重试。\nThe running instance is starting or closing. Please try again shortly.", "FolderRewind", 0x30);
                return accepted ? 0 : 2;
            }
            Instance = instance;
#if FOLDERREWIND_MSI
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
#endif
            XamlGeneratedProgram.XamlGeneratedMain();
        }
        catch (Exception error)
        {
            MessageBox(IntPtr.Zero, error.Message, "FolderRewind", 0x10);
            return 1;
        }
        finally { Instance = null; }
        if (RestartSafeModeOnExit && Environment.ProcessPath is { } executable)
        {
            try { using var child = Process.Start(new ProcessStartInfo(executable) { Arguments = "--safe-mode", UseShellExecute = !AppRuntimeInfo.IsMsiDistribution }); }
            catch (Exception error) { MessageBox(IntPtr.Zero, "重新启动失败 / Restart failed:\n" + error.Message, "FolderRewind", 0x10); return 1; }
        }
        return 0;
    }

    private static bool IsStartupLaunch(string[] args)
    {
        if (args.Any(value => string.Equals(value, StartupService.ClassicStartupArgument, StringComparison.OrdinalIgnoreCase)))
            return true;
#if !FOLDERREWIND_MSI
        try
        {
            // Secondary processes do not run the generated XAML entry point.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind
                == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch { }
#endif
        return false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")] private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
