#if FOLDERREWIND_MSI
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
        string identity = "Leafuke.FolderRewind.Msi." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
        try
        {
            using var instance = SingleInstanceCoordinator.TryAcquire(identity);
            if (instance == null)
            {
                bool startup = args.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
                bool accepted = SingleInstanceCoordinator.NotifyAsync(identity,
                    startup,
                    allowForeground: process => AllowSetForegroundWindow(process)).GetAwaiter().GetResult();
                if (!accepted && !startup) MessageBox(IntPtr.Zero, "现有实例正在启动或退出，请稍后重试。\nThe running instance is starting or closing. Please try again shortly.", "FolderRewind", 0x30);
                return accepted ? 0 : 2;
            }
            Instance = instance;
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
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
            try { using var child = Process.Start(new ProcessStartInfo(executable) { Arguments = "--safe-mode", UseShellExecute = false }); }
            catch (Exception error) { MessageBox(IntPtr.Zero, "重新启动失败 / Restart failed:\n" + error.Message, "FolderRewind", 0x10); return 1; }
        }
        return 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")] private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
#endif
