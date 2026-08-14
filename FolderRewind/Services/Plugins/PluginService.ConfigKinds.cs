using System;
using System.Collections.Generic;
using System.Linq;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Configuration;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services.Plugins;

public static partial class PluginService
{
    /// <summary>
    /// 获取可供 UI 选择的配置类型。v3 以已安装包的 Manifest Config Kind 为真相；
    /// v2 字符串类型仅作为 M6 clean break 前的兼容桥。
    /// </summary>
    public static IReadOnlyList<PluginConfigKindOption> GetAllSupportedConfigKinds(
        bool includeEncrypted = false)
    {
        var result = CreateCoreOptions(includeEncrypted);
        var stableKeys = new HashSet<string>(
            result.Select(option => option.StableKey),
            StringComparer.OrdinalIgnoreCase);

        AddInstalledV3Kinds(result, stableKeys);
        AddLoadedV2Kinds(result, stableKeys);
        return result;
    }

    public static IReadOnlyList<string> GetAllSupportedConfigTypes()
        => GetAllSupportedConfigKinds()
            .Select(option => option.LegacyConfigType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static PluginConfigKindOption ResolveConfigKindOption(
        BackupConfig config,
        bool includeEncrypted = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = GetAllSupportedConfigKinds(includeEncrypted);
        var hasLegacySpecialType = !string.IsNullOrWhiteSpace(config.ConfigType)
                                   && !string.Equals(config.ConfigType, "Default", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(config.ConfigType, "Encrypted", StringComparison.OrdinalIgnoreCase);
        if (hasLegacySpecialType)
        {
            var byLegacy = options.FirstOrDefault(option =>
                string.Equals(option.LegacyConfigType, config.ConfigType, StringComparison.OrdinalIgnoreCase));
            if (byLegacy is not null) return byLegacy;
        }

        var byKind = options.FirstOrDefault(option =>
            string.Equals(option.Kind.OwnerId, config.Kind?.OwnerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(option.Kind.KindId, config.Kind?.KindId, StringComparison.OrdinalIgnoreCase)
            && (!option.IsEncrypted || config.IsEncrypted));
        if (byKind is not null) return byKind;

        var byLegacyFallback = options.FirstOrDefault(option =>
            string.Equals(option.LegacyConfigType, config.ConfigType, StringComparison.OrdinalIgnoreCase));
        if (byLegacyFallback is not null) return byLegacyFallback;

        return new PluginConfigKindOption
        {
            DisplayName = string.IsNullOrWhiteSpace(config.ConfigType)
                ? I18n.GetString("ConfigKind_Unknown_Name")
                : config.ConfigType,
            Description = I18n.GetString("ConfigKind_Unknown_Description"),
            Kind = new ConfigKindReference
            {
                OwnerId = config.Kind?.OwnerId ?? ConfigSchema.CoreOwnerId,
                KindId = config.Kind?.KindId ?? ConfigSchema.CoreDefaultKindId
            },
            LegacyConfigType = string.IsNullOrWhiteSpace(config.ConfigType) ? "Default" : config.ConfigType,
            RequiredPluginId = config.RequiredPluginId
        };
    }

    public static void ApplyConfigKind(
        BackupConfig config,
        PluginConfigKindOption option,
        bool applyEncryption = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(option);
        config.ConfigType = option.LegacyConfigType;
        config.Kind = option.CreateReference();
        config.RequiredPluginId = option.RequiredPluginId;
        if (applyEncryption) config.IsEncrypted = option.IsEncrypted;
        config.ConfigRevision = Guid.NewGuid().ToString("N");
    }

    private static List<PluginConfigKindOption> CreateCoreOptions(bool includeEncrypted)
    {
        var result = new List<PluginConfigKindOption>
        {
            new()
            {
                DisplayName = I18n.GetString("ConfigKind_CoreDefault_Name"),
                Description = I18n.GetString("ConfigKind_CoreDefault_Description"),
                Kind = new ConfigKindReference
                {
                    OwnerId = ConfigSchema.CoreOwnerId,
                    KindId = ConfigSchema.CoreDefaultKindId
                },
                LegacyConfigType = "Default"
            }
        };
        if (!includeEncrypted) return result;

        result.Add(new PluginConfigKindOption
        {
            DisplayName = I18n.GetString("ConfigKind_Encrypted_Name"),
            Description = I18n.GetString("ConfigKind_Encrypted_Description"),
            Kind = new ConfigKindReference
            {
                OwnerId = ConfigSchema.CoreOwnerId,
                KindId = ConfigSchema.CoreDefaultKindId
            },
            LegacyConfigType = "Default",
            IsEncrypted = true
        });
        return result;
    }

    private static void AddInstalledV3Kinds(
        ICollection<PluginConfigKindOption> result,
        ISet<string> stableKeys)
    {
        foreach (var (pluginId, declaration) in PluginV3PackageService.GetInstalledConfigKinds())
        {
            var legacyType = IsMineRewindKind(declaration.Kind)
                ? "Minecraft Saves"
                : $"{declaration.Kind.OwnerId.Value}/{declaration.Kind.KindId}";
            var option = new PluginConfigKindOption
            {
                DisplayName = I18n.PickBest(
                    declaration.DisplayName.Translations,
                    declaration.DisplayName.Default) ?? legacyType,
                Description = I18n.PickBest(
                    declaration.Description.Translations,
                    declaration.Description.Default) ?? string.Empty,
                Kind = new ConfigKindReference
                {
                    OwnerId = declaration.Kind.OwnerId.Value,
                    KindId = declaration.Kind.KindId
                },
                LegacyConfigType = legacyType,
                RequiredPluginId = pluginId.Value
            };
            if (stableKeys.Add(option.StableKey)) result.Add(option);
        }
    }

    private static void AddLoadedV2Kinds(
        ICollection<PluginConfigKindOption> result,
        ISet<string> stableKeys)
    {
        if (!IsPluginSystemEnabled()) return;
        foreach (var plugin in GetEnabledLoadedPluginsSnapshot())
        {
            try
            {
                foreach (var type in plugin.GetSupportedConfigTypes() ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(type)) continue;
                    var option = CreateLegacyOption(plugin.Manifest.Id, type.Trim());
                    if (stableKeys.Add(option.StableKey)) result.Add(option);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    I18n.Format("PluginService_GetSupportedConfigTypesFailed", plugin.Manifest.Id, ex.Message),
                    "PluginService",
                    ex);
            }
        }
    }

    private static PluginConfigKindOption CreateLegacyOption(string pluginId, string type)
        => new()
        {
            DisplayName = type,
            Kind = string.Equals(type, "Minecraft Saves", StringComparison.OrdinalIgnoreCase)
                ? new ConfigKindReference
                {
                    OwnerId = ConfigSchema.MineRewindPluginId,
                    KindId = ConfigSchema.MineRewindKindId
                }
                : new ConfigKindReference
                {
                    OwnerId = "folderrewind.legacy",
                    KindId = type
                },
            LegacyConfigType = type,
            RequiredPluginId = pluginId,
            SupportsLegacyBatchCreation = true
        };

    private static bool IsMineRewindKind(ConfigKindRef kind)
        => string.Equals(kind.OwnerId.Value, ConfigSchema.MineRewindPluginId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(kind.KindId, ConfigSchema.MineRewindKindId, StringComparison.OrdinalIgnoreCase);
}
