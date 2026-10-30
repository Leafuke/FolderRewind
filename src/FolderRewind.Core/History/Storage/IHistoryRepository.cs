using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Storage;

public interface IHistoryRepository
{
    HistoryConfigId ConfigId { get; }
    HistoryRepositoryPaths Paths { get; }

    Task<HistoryRepositoryDescriptor> InitializeAsync(CancellationToken cancellationToken = default);

    Task<HistoryPackInstallResult> CommitAsync(
        HistoryCommitPack pack,
        HistoryTransactionJournal? journal = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryPackInstallResult>> ImportAsync(
        IEnumerable<ReadOnlyMemory<byte>> packBytes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoryPackReadResult>> ReadAllPacksAsync(
        CancellationToken cancellationToken = default);
}
