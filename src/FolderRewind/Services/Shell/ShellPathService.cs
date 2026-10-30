using System;
using System.Diagnostics;
using System.IO;

namespace FolderRewind.Services
{
    internal static class ShellPathService
    {
        public static bool TryOpenPath(string path, out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = I18n.GetString("Common_Failed");
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool TryRevealPathInExplorer(string path, out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = I18n.GetString("Common_Failed");
                return false;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    return TryOpenPath(path, out errorMessage);
                }

                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                    return true;
                }

                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    return TryOpenPath(parent, out errorMessage);
                }

                errorMessage = I18n.GetString("Common_Failed");
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool TryOpenCommandPromptAt(string directoryPath, out string? errorMessage)
        {
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                errorMessage = I18n.GetString("Common_Failed");
                return false;
            }

            try
            {
                // 云预设会把用户带到已解压工具目录，后续只需输入 rclone config / openlist server。
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/K cd /d \"{directoryPath}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
