using Microsoft.Win32;
using System;

namespace FolderRewind.Services
{
    /// <summary>
    /// 通用注册表读取辅助方法
    /// </summary>
    internal static class RegistryHelper
    {
        /// <summary>
        /// 从 HKLM 读取指定注册表路径下的值。
        /// </summary>
        /// <param name="subKeyPath">注册表子键路径（不含 HKLM 前缀），如 @"SOFTWARE\Microsoft\..."</param>
        /// <param name="valueName">要读取的值名称；传 null 或空字符串表示读取默认值。</param>
        /// <returns>值的字符串形式；若键/值不存在或读取失败则返回 null。</returns>
        public static string? ReadLocalMachineString(string subKeyPath, string? valueName = null)
        {
            if (string.IsNullOrWhiteSpace(subKeyPath)) return null;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
                if (key == null) return null;

                return key.GetValue(string.IsNullOrEmpty(valueName) ? null : valueName) as string;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Registry read failed: HKLM\\{subKeyPath} [{valueName ?? "(default)"}] - {ex.Message}",
                    nameof(RegistryHelper));
                return null;
            }
        }

        /// <summary>
        /// 从 HKCU 读取指定注册表路径下的值。
        /// </summary>
        public static string? ReadCurrentUserString(string subKeyPath, string? valueName = null)
        {
            if (string.IsNullOrWhiteSpace(subKeyPath)) return null;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
                if (key == null) return null;

                return key.GetValue(string.IsNullOrEmpty(valueName) ? null : valueName) as string;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Registry read failed: HKCU\\{subKeyPath} [{valueName ?? "(default)"}] - {ex.Message}",
                    nameof(RegistryHelper));
                return null;
            }
        }
    }
}
