using FolderRewind.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins;

public interface IFolderRewindDiscoveryProvider
{
    DiscoveryProviderDescriptor Descriptor { get; }

    Task<DiscoveryProviderResult> DiscoverAsync(
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken);
}
