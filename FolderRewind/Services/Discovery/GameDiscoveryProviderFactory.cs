using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;

namespace FolderRewind.Services.Discovery;

public sealed class GameDiscoveryProviderComposition
{
    public IReadOnlyList<IGameDiscoveryProvider> Providers { get; init; }
        = Array.Empty<IGameDiscoveryProvider>();
    public IReadOnlyList<DiscoveryDiagnostic> Diagnostics { get; init; }
        = Array.Empty<DiscoveryDiagnostic>();
}

public static class GameDiscoveryProviderFactory
{
    public static GameDiscoveryProviderComposition Create(LudusaviManifestCacheService cacheService)
    {
        ArgumentNullException.ThrowIfNull(cacheService);
        var providers = new List<IGameDiscoveryProvider>
        {
            new LudusaviDiscoveryProvider(cacheService)
        };
        var diagnostics = new List<DiscoveryDiagnostic>();
        foreach (var pluginId in PluginV3RuntimeService.GetActivePlugins())
        {
            using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IDiscoveryCapability>(pluginId, default);
            if (lease == null)
            {
                continue;
            }
            if (lease.Capability is not IDiscoveryDefinitionCatalog)
            {
                diagnostics.Add(new DiscoveryDiagnostic
                {
                    Severity = DiscoveryDiagnosticSeverity.Warning,
                    Code = "definition-catalog-unavailable",
                    Message = $"Plugin '{pluginId.Value}' must be updated before its discovery provider can be used by game discovery.",
                    ProviderId = lease.Capability.ProviderId.Value,
                    Category = "plugin-discovery"
                });
                continue;
            }

            var manifest = PluginV3RuntimeService.FindManifest(pluginId);
            providers.Add(new PluginGameDiscoveryProvider(
                pluginId,
                lease.Capability.ProviderId,
                manifest?.Name.Default ?? pluginId.Value,
                manifest?.Version ?? "unknown"));
        }
        return new GameDiscoveryProviderComposition
        {
            Providers = providers,
            Diagnostics = diagnostics
        };
    }
}
