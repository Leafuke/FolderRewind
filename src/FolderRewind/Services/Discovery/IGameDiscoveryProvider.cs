using FolderRewind.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Discovery;

/// <summary>
/// Host 内置游戏清单发现器。它与 v3 插件 Discovery capability 是两条独立边界，
/// 不能再被外部程序集实现或通过 flat DLL 扫描注入。
/// </summary>
public interface IGameDiscoveryProvider
{
    DiscoveryProviderDescriptor Descriptor { get; }

    Task<DiscoveryProviderResult> DiscoverAsync(
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken);
}
