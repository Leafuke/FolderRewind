using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System;

namespace FolderRewind.Services;

/// <summary>
/// 一次边界解析的结果。EffectiveConfig / EffectiveFolder 已经折进插件贡献，抓取、还原、切换分支都必须用这一份，
/// 不要回头去读用户配置里的原对象。
/// </summary>
internal sealed record HistorySourceBoundaryResolution(
    BackupConfig EffectiveConfig,
    ManagedFolder EffectiveFolder,
    EffectiveSourceBoundarySnapshot Boundary,
    bool IsBlocked,
    string BlockedErrorCode,
    string BlockedErrorMessage);

/// <summary>
/// 解析捕获、还原、切换分支、合并共用的权威受管理边界。
/// 由三层折成两层：核心层 <see cref="EffectiveSourceBoundaryFactory"/> + 插件过滤器贡献层。
/// </summary>
internal static class HistorySourceBoundaryResolver
{
    public static HistorySourceBoundaryResolution Resolve(
        BackupConfig originalConfig,
        ManagedFolder originalFolder)
    {
        ArgumentNullException.ThrowIfNull(originalConfig);
        ArgumentNullException.ThrowIfNull(originalFolder);

        // 插件贡献作用在配置过滤器的黑/白名单上，语义等价于 1.9.x 的 FilePolicy 层。
        // 该调用内部会深克隆配置，所以把贡献折进来不会写回用户配置。
        var contribution = PluginService.ResolveConfigWithBackupFilterContributions(originalConfig, originalFolder);
        if (!contribution.Success)
        {
            return Blocked(
                originalConfig,
                originalFolder,
                contribution.ErrorCode,
                contribution.ErrorMessage);
        }

        var effectiveConfig = contribution.EffectiveConfig ?? originalConfig;
        var effectiveFolder = MatchEffectiveFolder(effectiveConfig, originalConfig, originalFolder);
        return new(
            effectiveConfig,
            effectiveFolder,
            EffectiveSourceBoundaryFactory.Create(
                effectiveFolder.Path,
                effectiveFolder.SourceScope,
                effectiveConfig.Filters),
            IsBlocked: false,
            BlockedErrorCode: string.Empty,
            BlockedErrorMessage: string.Empty);
    }

    /// <summary>
    /// 插件贡献被判定为不可用时仍然给出边界，但把 IsBlocked 置位 —— 调用方必须据此中止，
    /// 不能拿这份「未折进贡献」的边界当作可用结果。
    /// </summary>
    private static HistorySourceBoundaryResolution Blocked(
        BackupConfig config,
        ManagedFolder folder,
        string errorCode,
        string errorMessage)
        => new(
            config,
            folder,
            EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters),
            IsBlocked: true,
            BlockedErrorCode: errorCode ?? string.Empty,
            BlockedErrorMessage: errorMessage ?? string.Empty);

    /// <summary>
    /// 插件贡献会深克隆整个配置，克隆体的 SourceFolders 与原对象不是同一批引用。
    /// 按位置取回同一个来源，避免下游同时握着两套对象、边界与配置对不上。
    /// </summary>
    private static ManagedFolder MatchEffectiveFolder(
        BackupConfig effectiveConfig,
        BackupConfig originalConfig,
        ManagedFolder originalFolder)
    {
        var index = originalConfig.SourceFolders.IndexOf(originalFolder);
        if (index < 0 || index >= effectiveConfig.SourceFolders.Count)
        {
            throw new InvalidOperationException(
                "The managed folder does not belong to the configuration it was resolved against.");
        }

        return effectiveConfig.SourceFolders[index];
    }
}
