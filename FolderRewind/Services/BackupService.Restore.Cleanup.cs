using System;
using System.IO;
using System.Linq;
using FolderRewind.Models;

namespace FolderRewind.Services
{
    public static partial class BackupService
    {
        private static void ClearReadonlyAttributes(string directory)
        {
            try
            {
                ClearReadonlyAttribute(directory);
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    ClearReadonlyAttribute(file);
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                             .OrderByDescending(path => path.Length))
                    ClearReadonlyAttribute(child);
            }
            catch (Exception ex)
            {
                Log($"Failed to clear readonly attributes: {ex.Message}", LogLevel.Debug);
            }
        }

        private static void ClearReadonlyAttribute(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            catch
            {
            }
        }
    }
}
