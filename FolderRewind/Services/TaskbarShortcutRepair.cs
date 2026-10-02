using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FolderRewind.Services;

[SupportedOSPlatform("windows")]
internal static class TaskbarShortcutRepair
{
    internal static int RepairOwnedPins(string directory, string executable, Action<Exception>? onError = null)
    {
        if (!Directory.Exists(directory) || !File.Exists(executable)) return 0;
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        var shell = Activator.CreateInstance(shellType)!;
        var repaired = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.lnk"))
            {
                object link = ((dynamic)shell).CreateShortcut(path);
                try
                {
                    string target = ((dynamic)link).TargetPath;
                    if (!Path.IsPathFullyQualified(target)) continue;
                    // Do not take over Store pins, another installation, or a
                    // similarly named application; preserve arguments and order.
                    if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) continue;
                    string icon = ((dynamic)link).IconLocation;
                    if (string.Equals(icon, executable + ",0", StringComparison.OrdinalIgnoreCase) &&
                        ShellShortcutIdentity.GetAppUserModelId(path) == ShellShortcutIdentity.MsiAppId &&
                        ShellShortcutIdentity.GetShortcutProperty(path, 3) == ShellShortcutIdentity.GetRelaunchIconResource(executable)) continue;
                    ((dynamic)link).IconLocation = executable + ",0";
                    ((dynamic)link).Save();
                    ShellShortcutIdentity.SetShortcutIdentity(path, ShellShortcutIdentity.MsiAppId, executable);
                    SHChangeNotify(0x2000, 0x2005, path, IntPtr.Zero); // UPDATEITEM, PATHW, FLUSHNOWAIT
                    repaired++;
                }
                catch (Exception error) { onError?.Invoke(error); }
                finally { Marshal.FinalReleaseComObject(link); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return repaired;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string path, IntPtr second);
}
