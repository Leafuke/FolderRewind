using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class NativeHistoryConfigLease
{
    internal static string Signature(BackupConfig config) => JsonSerializer.Serialize(new
    {
        config.Id, config.ConfigRevision, config.DestinationPath, config.IsEncrypted, config.Kind,
        config.Filters, config.BackupScope,
        Sources = config.SourceFolders.Select(f => new { f.Id, f.Path, f.SourceScope, f.ProviderStates }).OrderBy(f => f.Id)
    });
    internal static async ValueTask<IAsyncDisposable> EnterAsync(BackupConfig expected, string signature, CancellationToken token)
    {
        var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(expected.Id, token).ConfigureAwait(false);
        IDisposable? frozen = null;
        try
        {
            lock (ConfigMutationProtection.Sync)
            {
                var app = ConfigService.CurrentConfig;
                var config = app.BackupConfigs.SingleOrDefault(c => c.Id == expected.Id)
                    ?? throw new InvalidOperationException("Configuration is no longer registered.");
                var objects = new List<object> { app, app.BackupConfigs, config, config.Kind, config.SourceFolders,
                    config.Filters, config.Filters.Blacklist, config.Filters.BackupWhitelist, config.Filters.RestoreWhitelist,
                    config.BackupScope, config.BackupScope.Parameters };
                foreach (var folder in config.SourceFolders)
                {
                    objects.AddRange([folder, folder.SourceScope, folder.SourceScope.IncludePatterns, folder.ProviderStates]);
                    objects.AddRange(folder.ProviderStates.Values);
                }
                frozen = ConfigMutationProtection.Freeze(objects);
                if (Signature(config) != signature) throw new InvalidOperationException("Configuration changed before workspace mutation.");
            }
            return new Lease(operation, frozen);
        }
        catch { frozen?.Dispose(); await operation.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private sealed class Lease(IAsyncDisposable operation, IDisposable frozen) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { frozen.Dispose(); await operation.DisposeAsync().ConfigureAwait(false); }
    }
}
