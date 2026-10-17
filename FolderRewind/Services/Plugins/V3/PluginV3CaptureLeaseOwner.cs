using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>
/// 统一持有一次捕获中的 Host runtime lease 与插件 consistency lease，
/// 使成对资源的所有权转移和结束操作对捕获会话呈现为一个原子边界。
/// </summary>
internal sealed class PluginV3CaptureLeaseOwner
{
    private CaptureLeaseOwnership? _ownership;

    public string? SourcePath => _ownership?.Consistency.SourcePath;
    public string? StableSourcePath => _ownership?.Consistency.IsStableSourceView == true
        ? _ownership.Consistency.SourcePath
        : null;

    public async ValueTask AcquireAsync(
        PluginRuntimeManager runtime,
        PluginId pluginId,
        ConfigSnapshot config,
        FolderSnapshot folder,
        ConsistencyIntent intent,
        List<PluginDiagnostic> diagnostics,
        CancellationToken cancellationToken = default,
        BackupSourceViewLimits? sourceViewLimits = null,
        PluginOperationLease? operation = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var capabilityLease = operation is null ? runtime.TryAcquire<IBackupConsistencyCapability>(
            pluginId, capability => capability.Kind == config.Kind, cancellationToken) : null;
        var capability = operation?.Find<IBackupConsistencyCapability>(c => c.Kind == config.Kind) ?? capabilityLease?.Capability;
        var context = operation?.Context ?? capabilityLease?.Context;
        IDisposable ownershipLease = capabilityLease is not null ? capabilityLease : new NoopLease();
        if (capability is null)
        {
            if (intent == ConsistencyIntent.Require)
                throw new InvalidOperationException("The required consistency capability became unavailable.");

            diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_consistency_fallback",
                DiagnosticSeverity.Warning,
                "BackupConsistency",
                pluginId.Value,
                new Dictionary<string, string>()));
            return;
        }

        IConsistencyLease? acquiredConsistency = null;
        try
        {
            if (sourceViewLimits is not null && !capability.SupportsBoundedSourceView)
                throw new InvalidOperationException("The consistency provider does not support bounded source views.");
            using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
            acquiredConsistency = await capability.AcquireAsync(
                new BackupConsistencyRequest(config, folder, intent) { SourceViewLimits = sourceViewLimits },
                context!).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(acquiredConsistency);
            ArgumentNullException.ThrowIfNull(acquiredConsistency.Diagnostics);
            var consistencyDiagnostics = acquiredConsistency.Diagnostics.ToArray();
            // 发布成对 holder 前先验证并复制插件拥有的数据，避免暴露半初始化所有权。
            diagnostics.AddRange(consistencyDiagnostics);

            var ownership = new CaptureLeaseOwnership(acquiredConsistency, ownershipLease);
            if (Interlocked.CompareExchange(ref _ownership, ownership, null) is not null)
            {
                try
                {
                    await acquiredConsistency.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    ownershipLease.Dispose();
                    acquiredConsistency = null;
                }
                throw new InvalidOperationException("Backup consistency ownership was already established.");
            }

            acquiredConsistency = null;
        }
        catch (OperationCanceledException) when (
            intent == ConsistencyIntent.Prefer
            && !cancellationToken.IsCancellationRequested)
        {
            await ReleaseUnpublishedAsync(acquiredConsistency, ownershipLease).ConfigureAwait(false);
            diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_consistency_fallback",
                DiagnosticSeverity.Warning,
                "BackupConsistency",
                pluginId.Value,
                new Dictionary<string, string>()));
        }
        catch (Exception) when (intent == ConsistencyIntent.Prefer && !cancellationToken.IsCancellationRequested)
        {
            await ReleaseUnpublishedAsync(acquiredConsistency, ownershipLease).ConfigureAwait(false);
            diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_consistency_fallback",
                DiagnosticSeverity.Warning,
                "BackupConsistency",
                pluginId.Value,
                new Dictionary<string, string>()));
        }
        catch
        {
            await ReleaseUnpublishedAsync(acquiredConsistency, ownershipLease).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask CompleteAsync()
    {
        var ownership = Interlocked.Exchange(ref _ownership, null);
        if (ownership is null) return;

        try
        {
            using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
            await ownership.Consistency.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ownership.Capability.Dispose();
        }
    }

    private static async ValueTask ReleaseUnpublishedAsync(
        IConsistencyLease? consistency,
        IDisposable capability)
    {
        try
        {
            using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
            if (consistency is not null)
                await consistency.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 保留原始获取失败或回退决策；Host 运行时租约仍会在 finally 中释放。
        }
        finally
        {
            capability.Dispose();
        }
    }

    private sealed record CaptureLeaseOwnership(
        IConsistencyLease Consistency,
        IDisposable Capability);

    private sealed class NoopLease : IDisposable { public void Dispose() { } }
}
