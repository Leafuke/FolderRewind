using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Configuration;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Plugins;

public static partial class PluginService
{
    /// <summary>
    /// Config Kind 的唯一来源是 Core 声明和已安装 v3 Manifest。
    /// 显示名称只用于 UI，持久化始终使用 OwnerId + KindId。
    /// </summary>
    public static IReadOnlyList<PluginConfigKindOption> GetAllSupportedConfigKinds(bool includeEncrypted = false)
    {
        var result = CreateCoreOptions(includeEncrypted);
        var stableKeys = new HashSet<string>(
            result.Select(option => option.StableKey),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (pluginId, declaration) in PluginV3PackageService.GetInstalledConfigKinds())
        {
            var option = new PluginConfigKindOption
            {
                DisplayName = I18n.PickBest(
                    declaration.DisplayName.Translations,
                    declaration.DisplayName.Default) ?? declaration.Kind.KindId,
                Description = I18n.PickBest(
                    declaration.Description.Translations,
                    declaration.Description.Default) ?? string.Empty,
                Kind = new ConfigKindReference
                {
                    OwnerId = declaration.Kind.OwnerId.Value,
                    KindId = declaration.Kind.KindId
                },
                RequiredPluginId = pluginId.Value
            };
            if (stableKeys.Add(option.StableKey)) result.Add(option);
        }

        return result;
    }

    public static PluginConfigKindOption ResolveConfigKindOption(
        BackupConfig config,
        bool includeEncrypted = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        var option = GetAllSupportedConfigKinds(includeEncrypted).FirstOrDefault(candidate =>
            string.Equals(candidate.Kind.OwnerId, config.Kind.OwnerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Kind.KindId, config.Kind.KindId, StringComparison.OrdinalIgnoreCase)
            && candidate.IsEncrypted == config.IsEncrypted);
        if (option is not null) return option;

        return new PluginConfigKindOption
        {
            DisplayName = I18n.GetString("ConfigKind_Unknown_Name"),
            Description = I18n.GetString("ConfigKind_Unknown_Description"),
            Kind = new ConfigKindReference
            {
                OwnerId = config.Kind.OwnerId,
                KindId = config.Kind.KindId
            },
            RequiredPluginId = config.RequiredPluginId,
            IsEncrypted = config.IsEncrypted
        };
    }

    public static void ApplyConfigKind(
        BackupConfig config,
        PluginConfigKindOption option,
        bool applyEncryption = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(option);
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
                }
            }
        };
        if (includeEncrypted)
        {
            result.Add(new PluginConfigKindOption
            {
                DisplayName = I18n.GetString("ConfigKind_Encrypted_Name"),
                Description = I18n.GetString("ConfigKind_Encrypted_Description"),
                Kind = new ConfigKindReference
                {
                    OwnerId = ConfigSchema.CoreOwnerId,
                    KindId = ConfigSchema.CoreDefaultKindId
                },
                IsEncrypted = true
            });
        }

        return result;
    }
}
