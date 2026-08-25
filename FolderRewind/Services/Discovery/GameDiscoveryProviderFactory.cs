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

public sealed class PluginBatchProviderAvailability
{
    public bool IsAvailable { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PluginDisplayName { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
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

    public static PluginBatchProviderAvailability GetPluginBatchAvailability(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return Unavailable(
                "plugin-kind-required",
                I18n.GetString("HomePage_PluginBatchCreateUnavailableCore"),
                string.Empty);
        }

        try
        {
            var id = new PluginId(pluginId.Trim());
            var manifest = PluginV3RuntimeService.FindManifest(id);
            var displayName = manifest?.Name.Default ?? id.Value;
            if (!PluginV3RuntimeService.IsActive(id))
            {
                return Unavailable(
                    "provider-inactive",
                    I18n.Format("HomePage_PluginBatchCreateUnavailableInactive", displayName),
                    displayName);
            }

            using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IDiscoveryCapability>(id, default);
            if (lease == null)
            {
                return Unavailable(
                    "discovery-unavailable",
                    I18n.Format("HomePage_PluginBatchCreateUnavailableDiscovery", displayName),
                    displayName);
            }
            if (lease.Capability is not IDiscoveryDefinitionCatalog)
            {
                return Unavailable(
                    "definition-catalog-unavailable",
                    I18n.Format("HomePage_PluginBatchCreateUnavailableCatalog", displayName),
                    displayName,
                    lease.Capability.ProviderId.Value);
            }

            return new PluginBatchProviderAvailability
            {
                IsAvailable = true,
                PluginDisplayName = displayName,
                ProviderId = lease.Capability.ProviderId.Value,
                Message = I18n.Format("HomePage_PluginBatchCreateAvailable", displayName)
            };
        }
        catch (Exception ex)
        {
            LogService.LogWarning(
                $"Plugin batch discovery availability failed for '{pluginId}': {ex.Message}",
                "GameDiscovery");
            return Unavailable(
                "provider-unavailable",
                I18n.GetString("HomePage_PluginBatchCreateUnavailable"),
                pluginId.Trim());
        }
    }

    public static GameDiscoveryProviderComposition CreateForPlugin(string? pluginId)
    {
        var availability = GetPluginBatchAvailability(pluginId);
        if (!availability.IsAvailable || string.IsNullOrWhiteSpace(pluginId))
        {
            return FailureComposition(availability);
        }

        try
        {
            var id = new PluginId(pluginId.Trim());
            using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IDiscoveryCapability>(id, default);
            var manifest = PluginV3RuntimeService.FindManifest(id);
            var displayName = manifest?.Name.Default ?? id.Value;
            if (lease == null)
            {
                return FailureComposition(Unavailable(
                    "discovery-unavailable",
                    I18n.Format("HomePage_PluginBatchCreateUnavailableDiscovery", displayName),
                    displayName));
            }
            if (lease.Capability is not IDiscoveryDefinitionCatalog)
            {
                return FailureComposition(Unavailable(
                    "definition-catalog-unavailable",
                    I18n.Format("HomePage_PluginBatchCreateUnavailableCatalog", displayName),
                    displayName,
                    lease.Capability.ProviderId.Value));
            }

            return new GameDiscoveryProviderComposition
            {
                Providers =
                [
                    new PluginGameDiscoveryProvider(
                        id,
                        lease.Capability.ProviderId,
                        manifest?.Name.Default ?? id.Value,
                        manifest?.Version ?? "unknown")
                ]
            };
        }
        catch (Exception ex)
        {
            return FailureComposition(Unavailable(
                "provider-unavailable",
                I18n.Format("GameDiscovery_PluginBatch_ProviderUnavailable", ex.Message),
                pluginId.Trim()));
        }
    }

    private static GameDiscoveryProviderComposition FailureComposition(
        PluginBatchProviderAvailability availability) => new()
    {
        Diagnostics =
        [
            new DiscoveryDiagnostic
            {
                Severity = DiscoveryDiagnosticSeverity.Error,
                Code = string.IsNullOrWhiteSpace(availability.Code)
                    ? "provider-unavailable"
                    : availability.Code,
                Message = availability.Message,
                ProviderId = availability.ProviderId,
                Category = "plugin-batch"
            }
        ]
    };

    private static PluginBatchProviderAvailability Unavailable(
        string code,
        string message,
        string pluginDisplayName,
        string providerId = "") => new()
    {
        Code = code,
        Message = message,
        PluginDisplayName = pluginDisplayName,
        ProviderId = providerId
    };
}
