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
        var result = await Runtime.ActivateAsync(WithArtifactGuard(candidate), cancellationToken).ConfigureAwait(false);
        LogTransition("activate", candidate.PluginId, result);
        var manifest = candidate.Manifest;
        if (result.Success && manifest is not null)
        {
            Manifests[candidate.PluginId] = manifest;
            TryRegisterHotkeys(candidate.PluginId, Resolve(manifest.Name));
        }
        return result;
    }

    public static async ValueTask<PluginRuntimeTransitionResult> ReplaceAsync(
        PluginActivationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var result = await Runtime.ReplaceAsync(WithArtifactGuard(candidate), cancellationToken).ConfigureAwait(false);
        LogTransition("replace", candidate.PluginId, result);
        if (result.Success && candidate.Manifest is not null)
        {
            Manifests[candidate.PluginId] = candidate.Manifest;
            TryUnregisterHotkeys(candidate.PluginId);
            TryRegisterHotkeys(candidate.PluginId, Resolve(candidate.Manifest.Name));
        }
        return result;
    }

    public static async ValueTask<PluginRuntimeTransitionResult> DeactivateAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        var result = await Runtime.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        LogTransition("deactivate", pluginId, result);
        if (result.Success)
        {
            Manifests.TryRemove(pluginId, out _);
            TryUnregisterHotkeys(pluginId);
        }
        return result;
    }

    public static PluginManifestContract? FindManifest(PluginId pluginId)
        => Manifests.TryGetValue(pluginId, out var manifest) ? manifest : null;

    private static void LogTransition(string transition, PluginId pluginId, PluginRuntimeTransitionResult result)
    {
        using var scope = new NotificationOperationScope(new(Models.NotificationTargetKind.Configuration),
            context: new("", "PluginLifecycle", PluginId: pluginId.Value, Capability: transition));
        LogService.Log($"event=plugin.transition; action={transition}; plugin={pluginId}; outcome={result.Outcome}; state={result.State}; cleanupOutcome={result.CleanupOutcome}; requiresRestart={result.RequiresRestart}; "
            + "diagnosticCodes=" + string.Join(",", result.Diagnostics.Select(d => d.Code)),
            result.Success && !result.RequiresRestart ? Models.LogLevel.Info : Models.LogLevel.Warning, "Plugin");
    }

    private static PluginActivationCandidate WithArtifactGuard(PluginActivationCandidate candidate)
        => candidate.Manifest is null ? candidate : candidate with
        { CommitGuard = token => NativeArtifactHostService.AcquireCompatibilityGuardAsync(candidate.Manifest, token) };

    public static ConfigKindDeclaration? FindKind(ConfigKindRef kind)
        => Manifests.Values.SelectMany(manifest => manifest.ConfigKinds)
            .SingleOrDefault(declaration => declaration.Kind == kind);

    public static bool IsActive(PluginId pluginId)
        => Runtime.GetSnapshot(pluginId).State == PluginRuntimeState.Active;

    public static IReadOnlyList<PluginId> GetActivePlugins()
        => Manifests.Keys.Where(IsActive).OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();

    private static string Resolve(LocalizedText text)
        => I18n.PickBest(text.Translations, text.Default) ?? text.Default;

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
