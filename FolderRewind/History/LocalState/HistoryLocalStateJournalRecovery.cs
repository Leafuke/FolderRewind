using FolderRewind.History.Storage;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.LocalState;

public sealed class HistoryLocalStateJournalRecovery
{
    public const string WorkspaceStateKind = "workspace";
    public const string LocalReplicaCatalogStateKind = "localReplicaCatalog";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HistoryWorkspaceStore _workspaceStore;
    private readonly LocalReplicaCatalogStore _catalogStore;

    public HistoryLocalStateJournalRecovery(
        HistoryWorkspaceStore workspaceStore,
        LocalReplicaCatalogStore catalogStore)
    {
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
    }

    public static HistoryLocalStateIntent CreateWorkspaceIntent(
        HistoryWorkspace workspace,
        long expectedRevision)
        => new(
            WorkspaceStateKind,
            expectedRevision,
            JsonSerializer.SerializeToElement(workspace, JsonOptions));

    public static HistoryLocalStateIntent CreateCatalogIntent(
        LocalReplicaCatalog catalog,
        long expectedRevision)
        => new(
            LocalReplicaCatalogStateKind,
            expectedRevision,
            JsonSerializer.SerializeToElement(catalog, JsonOptions));

    public async Task ApplyCommittedStateAsync(
        HistoryTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        foreach (var intent in journal.LocalStateIntents)
        {
            switch (intent.StateKind)
            {
                case WorkspaceStateKind:
                    var workspace = intent.Delta.Deserialize<HistoryWorkspace>(JsonOptions)
                        ?? throw new HistoryRepositoryValidationException("Workspace journal delta is null.");
                    await _workspaceStore.SaveAsync(workspace, intent.ExpectedRevision, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case LocalReplicaCatalogStateKind:
                    var catalog = intent.Delta.Deserialize<LocalReplicaCatalog>(JsonOptions)
                        ?? throw new HistoryRepositoryValidationException("Catalog journal delta is null.");
                    await _catalogStore.SaveAsync(catalog, intent.ExpectedRevision, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new HistoryPackCompatibilityException(
                        $"Unknown local-state journal intent '{intent.StateKind}'.");
            }
        }
    }
}

