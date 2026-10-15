using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
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

    internal static async Task<bool> VerifyAndStoreAsync(BackupConfig snapshot, string password, CancellationToken token = default,
        IReadOnlyCollection<HistoryReplicaRecoveryCandidate>? selected = null)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(snapshot.Id, token).ConfigureAwait(false);
        var recovery = await new HistoryReplicaRecoveryService(runtime).InspectAsync(snapshot.DestinationPath, token: token).ConfigureAwait(false);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        // Ambiguous, unregistered candidates require a file choice before a password can establish a binding.
        foreach (var candidate in selected ?? [])
        {
            HistoryReplicaRecoveryService.RequireRegularPath(snapshot.DestinationPath, candidate.Path);
            var facts = await HistoryReplicaRecoveryService.ReadFileFactsAsync(candidate.Path, token).ConfigureAwait(false);
            if (facts.Size != candidate.Size || facts.Hash != candidate.StorageSha256) return false;
        }
        var unique = recovery.Candidates.GroupBy(c => c.RepresentationId).Select(g => selected?.SingleOrDefault(c => c.RepresentationId == g.Key)
            ?? (g.Count() == 1 ? g.Single() : null)).Where(c => c is not null).Select(c => c!).ToList();
        unique.AddRange((selected ?? []).Where(c => unique.All(u => u.RepresentationId != c.RepresentationId)));
        var entries = (catalog?.Entries ?? []).Concat(unique.Select(c => new LocalReplicaCatalogEntry(c.RepresentationId,
            LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(c.Path), DateTimeOffset.UtcNow)));
        var graph = await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var environment = new RepresentationEnvironment(entries, [], []);
        var versions = await runtime.Query.GetAllVersionsAsync(token).ConfigureAwait(false);
        async Task<bool> VerifiesAsync(string input)
        {
            var backend = CreateBackend(snapshot, input);
            var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
            var ready = false;
            foreach (var version in versions)
            {
                var assessment = await engine.AssessVersionAsync(version.VersionId, graph, environment, AssessmentDepth.Deep,
                    MaterializationFidelity.Exact, token).ConfigureAwait(false);
                ready |= assessment.Readiness == HistoryReadiness.Ready;
            }
            return ready;
        }
        if (!await VerifiesAsync(password).ConfigureAwait(false)) return false;
        var existing = EncryptionService.RetrievePassword(snapshot.Id);
        if (existing is not null && existing != password && await VerifiesAsync(existing).ConfigureAwait(false)) return false;
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(snapshot.Id, token).ConfigureAwait(false);
        var live = (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).Single(c => c.Id == snapshot.Id);
        if (NativeHistoryConfigLease.Signature(live) != NativeHistoryConfigLease.Signature(snapshot))
            throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        token.ThrowIfCancellationRequested();
        EncryptionService.StorePassword(snapshot.Id, password);
        return EncryptionService.VerifyPassword(snapshot.Id, password);
    }
}
