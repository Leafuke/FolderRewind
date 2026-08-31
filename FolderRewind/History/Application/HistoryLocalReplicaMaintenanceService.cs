using FolderRewind.History.LocalState;
using FolderRewind.History.Domain;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryTargetedReplicaDeletionResult(
    bool PayloadExisted,
    bool PayloadDeleted,
    int RemovedRegistrationCount);

public sealed class HistoryLocalReplicaMaintenanceService
{
    private readonly HistoryRuntime _runtime;

    public HistoryLocalReplicaMaintenanceService(HistoryRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<int> RemoveMissingControlledReplicasAsync(
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var load = await _runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new DeviceLocalStateConflictException("Local Replica Catalog requires recovery.");
        if (load.Value is null)
            return 0;

        var retained = load.Value.Entries.Where(item =>
            item.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath
            || File.Exists(item.Locator.AbsolutePath)
            || Directory.Exists(item.Locator.AbsolutePath)).ToArray();
        var removed = load.Value.Entries.Length - retained.Length;
        if (removed == 0)
            return 0;

        var updated = new LocalReplicaCatalog(
            _runtime.ConfigId,
            checked(load.Value.CatalogRevision + 1),
            retained);
        await _runtime.LocalReplicaCatalogStore.SaveAsync(
            updated,
            load.Value.CatalogRevision,
            cancellationToken).ConfigureAwait(false);
        await _runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        _runtime.ChangeFeed.Publish(_runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
        return removed;
    }

    public async Task<HistoryTargetedReplicaDeletionResult> DeleteControlledReplicaAsync(
        VersionId versionId,
        RepresentationId representationId,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathFullyQualified(localPath))
            throw new ArgumentException("Targeted replica deletion requires a fully qualified path.", nameof(localPath));

        var targetPath = Path.GetFullPath(localPath);
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);

        var representations = await _runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var representation = representations.SingleOrDefault(item => item.RepresentationId == representationId)
            ?? throw new InvalidOperationException("The selected Version Representation no longer exists.");
        if (representation.VersionId != versionId)
            throw new InvalidOperationException("The selected Version Representation belongs to another Version.");

        var load = await _runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new DeviceLocalStateConflictException("Local Replica Catalog requires recovery.");
        var catalog = load.Value
            ?? throw new InvalidOperationException("The selected local backup is no longer registered.");
        var targets = catalog.Entries.Where(entry =>
                entry.RepresentationId == representationId
                && entry.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                && StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(entry.Locator.AbsolutePath),
                    targetPath))
            .ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("The selected local backup is no longer registered.");

        // 同一路径若被其他 Representation 注册，直接删除会让未选中的版本静默失效。
        if (catalog.Entries.Any(entry =>
                entry.RepresentationId != representationId
                && entry.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                && StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(entry.Locator.AbsolutePath),
                    targetPath)))
        {
            throw new InvalidOperationException(
                "The selected local backup path is shared by another Version Representation.");
        }

        var targetIds = targets.Select(item => item.LocalReplicaId).ToHashSet();
        var remainingEntries = catalog.Entries.Where(item => !targetIds.Contains(item.LocalReplicaId)).ToArray();
        var hasRemainingReplica = remainingEntries.Any(item =>
            item.RepresentationId == representationId
            && (item.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath
                || File.Exists(item.Locator.AbsolutePath)
                || Directory.Exists(item.Locator.AbsolutePath)));
        // 任意传递依赖都必然包含一条直接指向目标的边；检查直接反向边即可阻止整条链失效。
        if (!hasRemainingReplica && representations.Any(item =>
                item.RepresentationId != representationId
                && item.DependencyRepresentationIds.Contains(representationId)))
        {
            throw new InvalidOperationException(
                "The selected local backup is required by another Version Representation.");
        }

        var updated = new LocalReplicaCatalog(
            _runtime.ConfigId,
            checked(catalog.CatalogRevision + 1),
            remainingEntries);
        await _runtime.LocalReplicaCatalogStore.SaveAsync(
            updated,
            catalog.CatalogRevision,
            cancellationToken).ConfigureAwait(false);

        var payloadExisted = File.Exists(targetPath);
        try
        {
            if (Directory.Exists(targetPath))
                throw new InvalidOperationException("Targeted replica deletion never recursively deletes a directory.");
            if (payloadExisted)
            {
                File.SetAttributes(targetPath, FileAttributes.Normal);
                File.Delete(targetPath);
            }
        }
        catch (Exception deleteError)
        {
            try
            {
                var rollbackLoad = await _runtime.LocalReplicaCatalogStore.LoadAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (rollbackLoad.Status != DeviceLocalStateStatus.Valid
                    || rollbackLoad.Value?.CatalogRevision != updated.CatalogRevision)
                {
                    throw new DeviceLocalStateConflictException(
                        "Local Replica Catalog changed before targeted deletion could roll back.");
                }
                var restored = new LocalReplicaCatalog(
                    _runtime.ConfigId,
                    checked(updated.CatalogRevision + 1),
                    rollbackLoad.Value.Entries.Concat(targets));
                await _runtime.LocalReplicaCatalogStore.SaveAsync(
                    restored,
                    updated.CatalogRevision,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Local backup deletion failed and its Catalog registration could not be restored.",
                    deleteError,
                    rollbackError);
            }
            throw;
        }

        try
        {
            await _runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 删除与 Catalog 更新已经提交；健康状态可由下次查询重新计算，不能反向报告删除失败。
        }
        _runtime.ChangeFeed.Publish(
            _runtime.ConfigId,
            HistoryChangeKind.LocalStateChanged,
            [representationId.ToString()]);
        return new HistoryTargetedReplicaDeletionResult(
            payloadExisted,
            payloadExisted && !File.Exists(targetPath),
            targets.Length);
    }

}
