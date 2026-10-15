using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class MigrationWorkflowBackend : IMigrationWorkflowBackend
{
    private static Task<BackupConfig?> FindAsync(string id) => FindCoreAsync(id);
    private static async Task<BackupConfig?> FindCoreAsync(string id)
        => (await MigrationBatchRecoveryService.CaptureAsync().ConfigureAwait(false)).SingleOrDefault(c => new HistoryConfigId(id).Matches(c.Id));

    public async Task<bool> IsRecoveryCurrentAsync(MigrationWorkflowProject project, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var config = await FindAsync(project.ConfigId).ConfigureAwait(false);
        if (config is null || NativeHistoryConfigLease.Signature(config) != project.ConfigurationSignature) return false;
        var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, new(config.Id));
        if (state is not { RecoveryCompleted: true } || !DataMigrationStateStore.IsActive(ConfigService.ConfigDirectory, state)
            || state.CompletedConfigSignature != project.ConfigurationSignature || state.ConfigImportId != project.ConfigImportId) return false;
        if (project.RecoveryEvidence.Length == 0) return false;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config.Id, token).ConfigureAwait(false);
        // Recheck real encrypted payloads as well as local revisions before permission confirmation.
        if (config.IsEncrypted)
        {
            var password = EncryptionService.RetrievePassword(config.Id);
            if (password is null || !await VerifyCredentialAsync(await DataMigrationService.PrepareRecoveryAsync(config, token).ConfigureAwait(false),
                project, password, token).ConfigureAwait(false)) return false;
        }
        return project.RecoveryEvidence == await CaptureEvidenceAsync(runtime, token).ConfigureAwait(false);
    }

    private static async Task<string> CaptureEvidenceAsync(HistoryRuntime runtime, CancellationToken token)
    {
        var stamp = new StringBuilder();
        var paths = runtime.Repository.Paths;
        var files = Directory.EnumerateFiles(paths.PacksRoot, "*.frpack", SearchOption.AllDirectories).Concat(new[] { paths.DescriptorPath,
            Path.Combine(paths.LocalStateRoot, "workspace.json"), Path.Combine(paths.LocalStateRoot, "replicas.json") }).ToList();
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        foreach (var entry in catalog?.Entries ?? [])
            if (entry.Locator.Kind == FolderRewind.History.LocalState.LocalReplicaLocatorKind.ControlledAbsolutePath)
            {
                var path = entry.Locator.AbsolutePath;
                if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }));
                else files.Add(path);
            }
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested(); stamp.Append(path).Append(':');
            if (!File.Exists(path)) { stamp.Append("missing;"); continue; }
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            stamp.Append(Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false))).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
    }

    public async Task<MigrationProjectInspection> InspectAsync(MigrationWorkflowProject project, CancellationToken token)
    {
        var config = await FindAsync(project.ConfigId).ConfigureAwait(false);
        MigrationProjectInspection Block(MigrationProjectPhase phase, string reason) => new(project.ConfigId,
            config is null ? "" : NativeHistoryConfigLease.Signature(config), config is null ? "" : new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(config.Id) ?? "", [], [])
            { BlockingPhase = phase, Diagnostics = [reason], CloudEnabled = config?.Cloud.Enabled == true,
                CurrentPaths = config is null ? null : MigrationWorkflowCoordinator.PathsFor(config) };
        if (config is null) return Block(MigrationProjectPhase.AwaitingConfiguration, I18n.GetString("Migration_AwaitingConfig"));
        if (DataMigrationService.IsAwaitingHistory(config)) return Block(MigrationProjectPhase.AwaitingHistory, I18n.GetString("Migration_AwaitingHistory"));
        var pathErrors = MigrationProjectSetupService.Check(config, requireExecutionReady: false);
        if (pathErrors.Count > 0 || config.SourceFolders.Any(f => !Directory.Exists(f.Path)))
            return Block(MigrationProjectPhase.AwaitingPaths, string.Join(Environment.NewLine, pathErrors.Append(I18n.GetString("MigrationWizard_CheckPaths"))));
        var context = await DataMigrationService.PrepareRecoveryAsync(config, token).ConfigureAwait(false);
        foreach (var selected in project.ArchiveSelections.Where(s => !context.Replicas.Candidates.Any(c => c.RepresentationId.ToString() == s.RepresentationId && c.Path == s.Path)))
        {
            if (!File.Exists(selected.Path)) continue;
            var candidate = await MigrationWorkflowCoordinator.ReadCandidateAsync(project, selected.RepresentationId, selected.Path, token).ConfigureAwait(false);
            if (candidate.Sha256 != selected.Sha256 || candidate.Size != selected.Size) continue;
            context = context with { Replicas = context.Replicas with { Candidates = context.Replicas.Candidates.Add(new(new(Guid.Parse(selected.RepresentationId)), candidate.Path, candidate.Size, candidate.Sha256, candidate.IdentityVerified)) } };
        }
        var graph = await context.Runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var versions = await context.Runtime.Query.GetAllVersionsAsync(token).ConfigureAwait(false);
        if (versions.Count > 0 && !Directory.Exists(config.DestinationPath))
            return Block(MigrationProjectPhase.AwaitingPaths, I18n.GetString("MigrationWizard_CheckPaths"));
        var archives = ImmutableArray.CreateBuilder<MigrationArchiveChoice>();
        foreach (var representation in graph)
        {
            var version = versions.Single(v => v.VersionId == representation.VersionId);
            var source = config.SourceFolders.Single(f => new SourceId(Guid.Parse(f.Id)) == version.SourceId);
            var candidates = context.Replicas.Candidates.Where(c => c.RepresentationId == representation.RepresentationId).ToArray();
            var choices = ImmutableArray.CreateBuilder<MigrationArchiveCandidate>();
            foreach (var candidate in candidates)
            {
                bool verified = false;
                if (!candidate.IdentityVerified && !config.IsEncrypted)
                    verified = (await BackupPasswordPreparationService.CreateBackend(config).VerifyAsync(representation, candidate.Path, token).ConfigureAwait(false)).Success;
                choices.Add(new(candidate.Path, candidate.Size, candidate.StorageSha256, candidate.IdentityVerified, verified, File.GetLastWriteTimeUtc(candidate.Path)));
            }
            archives.Add(new(representation.RepresentationId.ToString(), source.DisplayName,
                representation.RepresentationSpecificMetadata.GetValueOrDefault("fileName")
                    ?? representation.RepresentationSpecificMetadata.GetValueOrDefault("legacyFileName")
                    ?? I18n.GetString("MigrationWizard_ArchiveFile"), choices.ToImmutable()));
        }
        var branches = context.Continuation.Sources.Select(s => new MigrationBranchChoice(s.SourceId.ToString(),
            config.SourceFolders.Single(f => Guid.Parse(f.Id) == s.SourceId.Value).DisplayName, s.HasHistory, s.RequiresReconciliation,
            s.SuggestedUpdateId?.ToString(), [.. s.Candidates.Select(c => new MigrationBranchCandidate(c.UpdateId.ToString(), c.Name, c.VersionId.ToString()))]));
        var result = new MigrationProjectInspection(project.ConfigId, NativeHistoryConfigLease.Signature(config), new ConfigImportTransactionStore(ConfigService.ConfigFilePath).PendingToken(config.Id) ?? "",
            archives.ToImmutable(), [.. branches]) { Context = context, CloudEnabled = config.Cloud.Enabled, CurrentPaths = MigrationWorkflowCoordinator.PathsFor(config) };
        if (config.IsEncrypted)
        {
            var stored = EncryptionService.RetrievePassword(config.Id);
            if (stored is null || !await VerifyCredentialAsync(context, project, stored, token).ConfigureAwait(false))
                result = result with { BlockingPhase = MigrationProjectPhase.AwaitingPassword, Diagnostics = [I18n.GetString("Migration_PasswordPending")] };
        }
        return result;
    }

    private static async Task<bool> VerifyCredentialAsync(DataMigrationRecoveryContext context, MigrationWorkflowProject project, string password, CancellationToken token)
    {
        var catalog = (await context.Runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var unique = context.Replicas.Candidates.GroupBy(c => c.RepresentationId).Select(g =>
            g.SingleOrDefault(c => project.ArchiveSelections.Any(s => s.RepresentationId == c.RepresentationId.ToString() && s.Path == c.Path && s.Sha256 == c.StorageSha256))
                ?? (g.Count() == 1 ? g.Single() : null)).Where(c => c is not null).Select(c => c!);
        var entries = (catalog?.Entries ?? []).Concat(unique.Select(c => new FolderRewind.History.LocalState.LocalReplicaCatalogEntry(c.RepresentationId,
            LocalReplicaId.New(), FolderRewind.History.LocalState.LocalReplicaLocator.ControlledAbsolute(c.Path), DateTimeOffset.UtcNow)));
        var graph = await context.Runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var backend = BackupPasswordPreparationService.CreateBackend(context.Snapshot, password);
        var engine = new RepresentationRuntime([new CoreArchiveRepresentationHandler(backend), new SmartDeltaRepresentationHandler(backend)]);
        var environment = new RepresentationEnvironment(entries, [], []);
        foreach (var version in await context.Runtime.Query.GetAllVersionsAsync(token).ConfigureAwait(false))
            if ((await engine.AssessVersionAsync(version.VersionId, graph, environment, AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false)).Readiness == HistoryReadiness.Ready) return true;
        return false;
    }

    public async Task<MigrationWorkflowRecovery> RecoverAsync(MigrationProjectInspection inspection, MigrationRecoveryDecision decision, CancellationToken token)
    {
        var context = (DataMigrationRecoveryContext)inspection.Context!;
        var candidates = decision.Archives.Select(a => context.Replicas.Candidates.Single(c => c.RepresentationId.ToString() == a.RepresentationId
            && c.Path == a.Path && c.Size == a.Size && c.StorageSha256 == a.Sha256)).ToArray();
        var selections = decision.Branches.Select(b => new HistoryContinuationSelection(new(Guid.Parse(b.SourceId)), b.UpdateId is null ? null : new BranchUpdateId(Guid.Parse(b.UpdateId)))).ToArray();
        var result = await DataMigrationService.CompleteRecoveryAsync(context, candidates, selections, token).ConfigureAwait(false);
        return new(result.Replicas.Versions.Count(v => v.Readiness == HistoryReadiness.Ready), result.Replicas.Versions.Length,
            result.Workspace.SourceBaselines.Count(s => s.RequiresFullCapture), result.CredentialReady,
            [.. result.Replicas.Failures.Select(f => f.Diagnostic)], await CaptureEvidenceAsync(context.Runtime, token).ConfigureAwait(false));
    }

    public async Task<MigrationWorkflowPermission> InspectPermissionsAsync(MigrationWorkflowProject project, CancellationToken token)
    {
        var config = await FindAsync(project.ConfigId).ConfigureAwait(false) ?? throw new InvalidOperationException(I18n.GetString("Migration_AwaitingConfig"));
        var assessment = await MigrationBatchRecoveryService.CheckProjectAsync(config, token).ConfigureAwait(false);
        return new(assessment.CanConfirmBackup, assessment.CanConfirmCloud, config.Cloud.Enabled,
            assessment.Backup == MigrationCapabilityStatus.Released, assessment.Cloud == MigrationCapabilityStatus.Released,
            [.. new[] { assessment.BackupReason }.Where(s => !string.IsNullOrWhiteSpace(s))]);
    }

    public async Task GrantAsync(MigrationWorkflowProject project, CancellationToken token)
    {
        var config = await FindAsync(project.ConfigId).ConfigureAwait(false) ?? throw new InvalidOperationException(I18n.GetString("Migration_AwaitingConfig"));
        if (NativeHistoryConfigLease.Signature(config) != project.ConfigurationSignature)
            throw new InvalidOperationException(I18n.GetString("Migration_CheckExpired"));
        var assessment = await MigrationBatchRecoveryService.CheckProjectAsync(config, token).ConfigureAwait(false);
        if (project.BackupSelected && assessment.Backup == MigrationCapabilityStatus.Blocked || project.CloudSelected && assessment.Cloud == MigrationCapabilityStatus.Blocked)
            throw new InvalidOperationException(string.Join(Environment.NewLine, new[] { assessment.BackupReason, assessment.CloudReason }.Where(s => !string.IsNullOrWhiteSpace(s))));
        var result = await MigrationBatchRecoveryService.ResumeAsync([new(assessment, project.BackupSelected, project.CloudSelected && config.Cloud.Enabled)], token).ConfigureAwait(false);
        var failure = result.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Diagnostic));
        if (failure is not null) throw new InvalidOperationException(failure.Diagnostic);
    }
}
