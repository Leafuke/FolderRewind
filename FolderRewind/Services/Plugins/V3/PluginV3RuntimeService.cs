using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Services.Plugins.V3;

public static class PluginV3RuntimeService
{
    private static readonly ConcurrentDictionary<PluginId, PluginManifestContract> Manifests = new();
    private static PluginRuntimeManager _runtime = new(PluginRuntimeModeService.IsSafeMode);

    public static PluginRuntimeManager Runtime => Volatile.Read(ref _runtime);

    public static void Reset(bool safeMode)
    {
        Manifests.Clear();
        Volatile.Write(ref _runtime, new PluginRuntimeManager(safeMode));
    }

    public static async ValueTask<PluginRuntimeTransitionResult> ActivateAsync(
        PluginActivationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var result = await Runtime.ActivateAsync(candidate, cancellationToken).ConfigureAwait(false);
        var manifest = candidate.Manifest;
        if (result.Success && manifest is not null)
        {
            Manifests[candidate.PluginId] = manifest;
            TryRegisterHotkeys(candidate.PluginId, manifest.Name.Default);
        }
        return result;
    }

    public static async ValueTask<PluginRuntimeTransitionResult> ReplaceAsync(
        PluginActivationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var result = await Runtime.ReplaceAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (result.Success && candidate.Manifest is not null)
        {
            Manifests[candidate.PluginId] = candidate.Manifest;
            TryUnregisterHotkeys(candidate.PluginId);
            TryRegisterHotkeys(candidate.PluginId, candidate.Manifest.Name.Default);
        }
        return result;
    }

    public static async ValueTask<PluginRuntimeTransitionResult> DeactivateAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        var result = await Runtime.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            Manifests.TryRemove(pluginId, out _);
            TryUnregisterHotkeys(pluginId);
        }
        return result;
    }

    public static PluginManifestContract? FindManifest(PluginId pluginId)
        => Manifests.TryGetValue(pluginId, out var manifest) ? manifest : null;

    public static ConfigKindDeclaration? FindKind(ConfigKindRef kind)
        => Manifests.Values.SelectMany(manifest => manifest.ConfigKinds)
            .SingleOrDefault(declaration => declaration.Kind == kind);

    public static bool IsActive(PluginId pluginId)
        => Runtime.GetSnapshot(pluginId).State == PluginRuntimeState.Active;

    public static IReadOnlyList<PluginId> GetCompletionObservers()
        => Manifests
            .Where(pair => pair.Value.HasBackupCompletionObserver && IsActive(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();

    public static IReadOnlyList<PluginId> GetActivePlugins()
        => Manifests.Keys.Where(IsActive).OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();

    private static void TryRegisterHotkeys(PluginId pluginId, string pluginName)
    {
        try
        {
            PluginV3CommandService.RegisterHotkeys(pluginId, pluginName);
        }
        catch (Exception ex)
        {
            LogService.LogWarning(
                $"Plugin '{pluginId}' is active, but its hotkeys could not be registered: {ex.Message}",
                "PluginV3");
        }
    }

    private static void TryUnregisterHotkeys(PluginId pluginId)
    {
        try
        {
            PluginV3CommandService.UnregisterHotkeys(pluginId);
        }
        catch (Exception ex)
        {
            LogService.LogWarning(
                $"Plugin '{pluginId}' was transitioned, but its hotkeys could not be removed: {ex.Message}",
                "PluginV3");
        }
    }
}
