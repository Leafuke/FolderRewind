using FolderRewind.History.LocalState;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

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
}
