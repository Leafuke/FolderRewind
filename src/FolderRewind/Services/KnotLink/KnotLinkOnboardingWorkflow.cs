using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record KnotLinkInstallation(string? ExecutablePath, string? DisplayVersion, Version? Version)
{
    internal bool Compatible => !string.IsNullOrWhiteSpace(ExecutablePath) && Version is not null && Version >= KnotLinkOnboardingWorkflow.MinimumSupportedVersion;
}

internal enum IntegrationPreparationState { Ready, Blocked, Failed }
internal sealed record IntegrationPreparationResult(IntegrationPreparationState State, string MessageKey);

internal static class KnotLinkOnboardingWorkflow
{
    internal static readonly Version MinimumSupportedVersion = new(3, 0, 0, 0);
    internal static bool IsLocalHost(string? host)
    {
        var value = host?.Trim().Trim('[', ']');
        return string.IsNullOrEmpty(value) || value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }

    internal static async Task<IntegrationPreparationResult> EnsureInstalledAsync(
        Func<KnotLinkInstallation> inspect, Func<CancellationToken, Task<bool>> confirmDownload,
        Func<CancellationToken, Task<string>> download, Func<string, CancellationToken, Task<bool>> confirmLaunch,
        Func<string, TimeSpan, CancellationToken, Task<bool>> launchAndWait, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (inspect().Compatible) return new(IntegrationPreparationState.Ready, "Onboarding_ComponentReused");
        if (!await confirmDownload(token).ConfigureAwait(false))
            return new(IntegrationPreparationState.Blocked, "PluginPreset_ExternalDownloadDeclined");
        var installer = await download(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!await confirmLaunch(installer, token).ConfigureAwait(false))
            return new(IntegrationPreparationState.Blocked, "PluginPreset_ExternalLaunchDeclined");
        token.ThrowIfCancellationRequested();
        if (!await launchAndWait(installer, TimeSpan.FromMinutes(5), token).ConfigureAwait(false))
            return new(IntegrationPreparationState.Blocked, "PluginPreset_KnotLinkInstallPending");
        token.ThrowIfCancellationRequested();
        return inspect().Compatible
            ? new(IntegrationPreparationState.Ready, "PluginPreset_KnotLinkInstalled")
            : new(IntegrationPreparationState.Failed, "PluginPreset_KnotLinkInstallUnconfirmed");
    }

    internal static async Task<IntegrationPreparationResult> ConnectAsync(bool local,
        Func<bool> isRunning, Func<bool> start, Func<CancellationToken, Task<bool>> waitReady,
        Func<CancellationToken, Task<bool>> connect, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (local && !isRunning() && !start())
            return new(IntegrationPreparationState.Failed, "PluginPreset_KnotLinkStartFailed");
        var ready = await waitReady(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!ready)
            return new(IntegrationPreparationState.Blocked, "PluginPreset_KnotLinkNotReady");
        token.ThrowIfCancellationRequested();
        return await connect(token).ConfigureAwait(false)
            ? new(IntegrationPreparationState.Ready, "PluginPreset_KnotLinkConnected")
            : new(IntegrationPreparationState.Failed, "PluginPreset_KnotLinkConnectionFailed");
    }
}
