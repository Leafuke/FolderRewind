using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace FolderRewind.Services
{
    /// <summary>
    /// 为打包应用创建指向 AppsFolder 的桌面快捷方式。
    /// AppsFolder 使用 AUMID 启动应用，因此不会依赖会随更新变化的 MSIX 安装目录。
    /// </summary>
    public static class DesktopShortcutService
    {
        private const string ShortcutFileName = "FolderRewind.lnk";

        public static bool TryCreateDesktopShortcut(out string errorMessage)
        {
            errorMessage = string.Empty;
            object? shell = null;
            object? shortcut = null;

            try
            {
                var packageFamilyName = Package.Current.Id.FamilyName;
                if (string.IsNullOrWhiteSpace(packageFamilyName))
                {
                    throw new InvalidOperationException("The package family name is unavailable.");
                }

                // Package.appxmanifest uses Application Id="App". Combining it with the
                // package family name creates the app's stable AUMID without relying on
                // the versioned MSIX installation path.
                var appUserModelId = $"{packageFamilyName}!App";

                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktopPath))
                {
                    throw new InvalidOperationException("The desktop directory is unavailable.");
                }

                var shortcutPath = Path.Combine(desktopPath, ShortcutFileName);
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("Windows Script Host is unavailable.");

                shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Windows Script Host could not be started.");

                dynamic scriptShell = shell;
                shortcut = scriptShell.CreateShortcut(shortcutPath);
                dynamic link = shortcut;
                // explorer.exe 位于 Windows 根目录而不是 System32。
                link.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                link.Arguments = $"shell:AppsFolder\\{appUserModelId}";
                link.WorkingDirectory = desktopPath;
                link.Description = "FolderRewind";
                link.IconLocation = $"{Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "logo.ico")},0";
                link.Save();

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Log(I18n.Format("DesktopShortcut_Log_CreateFailed", ex.Message));
                return false;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static void ReleaseComObject(object? comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
    }
}
