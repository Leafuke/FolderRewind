using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryRepositoryBindingService
{
    private readonly HistoryRepositoryBindingCoordinator _coordinator;

    public HistoryRepositoryBindingService(string configDirectory)
        => _coordinator = new HistoryRepositoryBindingCoordinator(configDirectory);

    public Task<HistoryRepositoryBindingResult> EnsureAsync(
        BackupConfig config,
        Func<CancellationToken, Task> persistConfigAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(persistConfigAsync);
        return _coordinator.EnsureAsync(
            new HistoryConfigId(config.Id),
            config.HistoryRepositoryBinding?.FormatVersion,
            async (formatVersion, token) =>
            {
                config.HistoryRepositoryBinding = new HistoryRepositoryBinding
                {
                    FormatVersion = formatVersion
                };
                await persistConfigAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }
}

