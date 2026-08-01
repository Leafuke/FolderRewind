using FolderRewind.Models;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    public static partial class KnotLinkService
    {
        #region 辅助方法

        private static string? GetConfigOption(KnotLinkCommandRequest request)
            => request.GetString("config_id");

        private static string? GetFolderOption(KnotLinkCommandRequest request)
            => request.GetString("folder");

        private static bool TryResolveConfig(KnotLinkCommandRequest request, out BackupConfig? config, out string error)
        {
            config = null;
            error = string.Empty;

            var configId = GetConfigOption(request);
            if (string.IsNullOrWhiteSpace(configId))
            {
                error = "ERROR:" + I18n.GetString("KnotLink_Error_MissingConfigId");
                return false;
            }

            config = FindConfigById(configId);
            if (config == null)
            {
                error = $"ERROR:Config not found: {configId}";
                return false;
            }

            return true;
        }

        private static bool TryResolveFolder(KnotLinkCommandRequest request, BackupConfig config, out ManagedFolder? folder, out string error)
        {
            folder = null;
            error = string.Empty;

            var folderArg = GetFolderOption(request);
            if (string.IsNullOrWhiteSpace(folderArg))
            {
                error = "ERROR:" + I18n.GetString("KnotLink_Error_MissingFolderName");
                return false;
            }

            folder = FindFolderByIndexOrName(config, folderArg);
            if (folder == null)
            {
                error = $"ERROR:Folder not found: {folderArg}";
                return false;
            }

            return true;
        }

        private static bool TryGetBoolOption(KnotLinkCommandRequest request, string key, bool defaultValue, out bool value, out string error)
        {
            error = string.Empty;
            value = defaultValue;

            if (!request.HasOption(key))
            {
                return true;
            }

            var parsed = request.GetBool(key);
            if (parsed == null)
            {
                error = $"ERROR:{I18n.Format("KnotLink_Error_InvalidBool", key, request.GetString(key) ?? string.Empty)}";
                return false;
            }

            value = parsed.Value;
            return true;
        }

        private static bool TryResolveRestoreMode(KnotLinkCommandRequest request, out BackupService.RestoreMode mode, out string error)
        {
            mode = BackupService.RestoreMode.Overwrite;
            error = string.Empty;

            var modeText = request.GetString("mode");
            if (string.IsNullOrWhiteSpace(modeText))
            {
                return true;
            }

            if (string.Equals(modeText, "clean", StringComparison.OrdinalIgnoreCase))
            {
                mode = BackupService.RestoreMode.Clean;
                return true;
            }

            if (string.Equals(modeText, "overwrite", StringComparison.OrdinalIgnoreCase))
            {
                mode = BackupService.RestoreMode.Overwrite;
                return true;
            }

            error = $"ERROR:{I18n.Format("KnotLink_Error_InvalidRestoreMode", modeText)}";
            return false;
        }

        private static BackupConfig CreateConfigWithOneShotOverrides(
            BackupConfig source,
            IReadOnlyList<string> backupBlacklist,
            IReadOnlyList<string> backupWhitelist,
            IReadOnlyList<string> restoreWhitelist,
            string? backupScopeId = null,
            IReadOnlyDictionary<string, string>? backupScopeParameters = null)
        {
            var needsClone = (backupBlacklist?.Count ?? 0) > 0
                || (backupWhitelist?.Count ?? 0) > 0
                || (restoreWhitelist?.Count ?? 0) > 0
                || !string.IsNullOrWhiteSpace(backupScopeId)
                || (backupScopeParameters?.Count ?? 0) > 0;
            if (!needsClone)
            {
                return source;
            }

            var clone = BackupConfigCloneService.CloneForRuntimeMutation(
                source,
                I18n.GetString("KnotLink_Error_ConfigCloneFailed"));

            if (backupBlacklist != null)
            {
                foreach (var rule in backupBlacklist.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    clone.Filters.Blacklist.Add(rule.Trim());
                }
            }

            if (backupWhitelist != null && backupWhitelist.Count > 0)
            {
                clone.Filters.BackupFilterMode = BackupFilterMode.Whitelist;
                foreach (var rule in backupWhitelist.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    BackupFilterRulePolicy.AddDistinct(clone.Filters.BackupWhitelist, rule);
                }
            }

            if (restoreWhitelist != null)
            {
                foreach (var rule in restoreWhitelist.Where(rule => !string.IsNullOrWhiteSpace(rule)))
                {
                    BackupFilterRulePolicy.AddDistinct(clone.Filters.RestoreWhitelist, rule);
                }
            }

            if (!string.IsNullOrWhiteSpace(backupScopeId))
            {
                clone.BackupScope.PluginScopeId = IsFullScopeAlias(backupScopeId)
                    ? string.Empty
                    : backupScopeId.Trim();
            }

            if (backupScopeParameters != null && backupScopeParameters.Count > 0)
            {
                clone.BackupScope.Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in backupScopeParameters)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    clone.BackupScope.Parameters[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            return clone;
        }

        private static IReadOnlyDictionary<string, string> GetScopeParameters(KnotLinkCommandRequest request)
        {
            const string prefix = "scope_";
            return request.Options
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                               && pair.Key.Length > prefix.Length)
                .ToDictionary(
                    pair => pair.Key[prefix.Length..],
                    pair => pair.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsFullScopeAlias(string value)
        {
            return string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> GetBackupWhitelistOptions(KnotLinkCommandRequest request)
        {
            return request.GetList("backup_whitelist");
        }

        private static bool IsPartialBackup(BackupConfig config, ManagedFolder folder, string backupFile)
        {
            return HistoryService.TryGetEntry(config.Id, folder.Path, backupFile)?.IsPartialBackup == true;
        }

        private static ManagedFolder ResolveEquivalentFolder(BackupConfig effectiveConfig, ManagedFolder originalFolder)
        {
            if (effectiveConfig.SourceFolders.Count == 0)
            {
                return originalFolder;
            }

            return effectiveConfig.SourceFolders.FirstOrDefault(folder =>
                    string.Equals(folder.Path, originalFolder.Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(folder.DisplayName, originalFolder.DisplayName, StringComparison.OrdinalIgnoreCase))
                ?? originalFolder;
        }

        /// <summary>
        /// 根据 ID 查找配置
        /// </summary>
        private static BackupConfig? FindConfigById(string idOrName)
        {
            var configs = ConfigService.CurrentConfig?.BackupConfigs;
            if (configs == null) return null;

            // 先按 ID 查找
            var config = configs.FirstOrDefault(c =>
                string.Equals(c.Id, idOrName, StringComparison.OrdinalIgnoreCase));

            // 如果找不到，按名称查找
            if (config == null)
            {
                config = configs.FirstOrDefault(c =>
                    string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            }

            // 如果还找不到，尝试按索引查找
            if (config == null && int.TryParse(idOrName, out var index) && index >= 0 && index < configs.Count)
            {
                config = configs[index];
            }

            return config;
        }

        /// <summary>
        /// 根据索引或名称查找文件夹
        /// </summary>
        private static ManagedFolder? FindFolderByIndexOrName(BackupConfig config, string indexOrName)
        {
            // 先尝试按索引查找
            if (int.TryParse(indexOrName, out var index) && index >= 0 && index < config.SourceFolders.Count)
            {
                return config.SourceFolders[index];
            }

            // 按显示名称查找
            return config.SourceFolders.FirstOrDefault(f =>
                string.Equals(f.DisplayName, indexOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Path, indexOrName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 获取应用版本
        /// </summary>
        private static string GetAppVersion()
        {
            var version = AppRuntimeInfo.GetApplicationVersion();
            return version == null
                ? "1.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        #endregion
    }
}
