using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class BackupPasswordPreparationService
{
    internal static SevenZipArchiveProcessBackend CreateBackend(BackupConfig config, string? password = null)
    {
        var executable = SevenZipExecutableLocator.Resolve(ConfigService.CurrentConfig.GlobalSettings.SevenZipPath);
        var credential = password ?? EncryptionService.RetrievePassword(config.Id);
        return new(() => executable, () => credential, config.IsEncrypted, BackupService.InternalRestoreMarkerDirectoryName);
    }

    internal static async Task<bool> VerifyAndStoreAsync(BackupConfig snapshot, string password, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(snapshot.Id, token).ConfigureAwait(false);
        var recovery = await new HistoryReplicaRecoveryService(runtime).InspectAsync(snapshot.DestinationPath, token: token).ConfigureAwait(false);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        // Ambiguous, unregistered candidates require a file choice before a password can establish a binding.
        var unique = recovery.Candidates.GroupBy(c => c.RepresentationId).Where(g => g.Count() == 1).Select(g => g.Single());
        var entries = (catalog?.Entries ?? []).Concat(unique.Select(c => new LocalReplicaCatalogEntry(c.RepresentationId,
            LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(c.Path), DateTimeOffset.UtcNow)));
        var graph = await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var backend = CreateBackend(snapshot, password);
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
        var environment = new RepresentationEnvironment(entries, [], []);
        var verified = false;
        foreach (var version in await runtime.Query.GetAllVersionsAsync(token).ConfigureAwait(false))
        {
            var assessment = await engine.AssessVersionAsync(version.VersionId, graph, environment, AssessmentDepth.Deep,
                MaterializationFidelity.Exact, token).ConfigureAwait(false);
            verified |= assessment.Readiness == HistoryReadiness.Ready;
        }
        if (!verified) return false;
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(snapshot.Id, token).ConfigureAwait(false);
        var live = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => c.Id == snapshot.Id);
        if (live.ConfigRevision != snapshot.ConfigRevision || live.IsEncrypted != snapshot.IsEncrypted)
            throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        token.ThrowIfCancellationRequested();
        EncryptionService.StorePassword(snapshot.Id, password);
        return EncryptionService.VerifyPassword(snapshot.Id, password);
    }
}
