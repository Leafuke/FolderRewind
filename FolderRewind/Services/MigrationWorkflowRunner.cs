using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Persists each project decision; durable progress never replaces backend evidence checks.</summary>
internal sealed class MigrationWorkflowRunner(MigrationWorkflowStore store, IMigrationWorkflowBackend backend)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Executions = new(StringComparer.OrdinalIgnoreCase);
    private SemaphoreSlim Execution => Executions.GetOrAdd(store.DirectoryKey, _ => new(1, 1));
    internal async Task<MigrationWorkflowDocument> RunAsync(string id, CancellationToken token = default)
    {
        if (!await Execution.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Migration workflow is already running.");
        MigrationWorkflowDocument? document = null;
        try
        {
            document = store.Load(id) ?? throw new InvalidOperationException("Migration workflow was not found.");
            if (document.Stage == MigrationWorkflowStage.AwaitingRestart) return document;
            document = document with { Stage = MigrationWorkflowStage.Processing }; store.Save(document);
            for (var index = 0; index < document.Projects.Length; index++)
            {
                token.ThrowIfCancellationRequested(); var project = document.Projects[index];
                try
                {
                    if (project.ImportPending) continue;
                    if (project.RecoveryVerified && !await backend.IsRecoveryCurrentAsync(project, token).ConfigureAwait(false))
                        project = project with { RecoveryVerified = false, Phase = MigrationProjectPhase.Pending, RetainMissingArchives = false, ArchiveSelections = [], BranchSelections = [] };
                    if (!project.RecoveryVerified)
                    {
                        var inspection = await backend.InspectAsync(project, token).ConfigureAwait(false);
                        var decision = MigrationWorkflowPolicy.Decide(inspection, project);
                        project = project with { ConfigurationSignature = inspection.ConfigurationSignature, ConfigImportId = inspection.ConfigImportId,
                            Paths = inspection.CurrentPaths ?? project.Paths, CloudEnabled = inspection.CloudEnabled, Diagnostics = inspection.Diagnostics };
                        if (decision.Attention is { } attention) project = project with { Phase = attention };
                        else
                        {
                            var result = await backend.RecoverAsync(inspection, decision, token).ConfigureAwait(false);
                            project = project with { RecoveryVerified = result.CredentialReady, ReadyVersions = result.ReadyVersions,
                                RecoveryEvidence = result.Evidence,
                                TotalVersions = result.TotalVersions, FullCaptureSources = result.FullCaptureSources,
                                ArchiveSelections = decision.Archives, BranchSelections = decision.Branches, Diagnostics = result.Diagnostics,
                                Phase = result.CredentialReady ? MigrationProjectPhase.Verified : MigrationProjectPhase.AwaitingPassword };
                        }
                    }
                    // Recovery may have committed catalog/Workspace facts before cancellation is observed.
                    document = document with { Projects = document.Projects.SetItem(index, project) }; store.Save(document);
                    if (project.RecoveryVerified)
                    {
                        var permission = await backend.InspectPermissionsAsync(project, token).ConfigureAwait(false);
                        project = project with { CanConfirmBackup = permission.CanBackup, CanConfirmCloud = permission.CanCloud && permission.CloudEnabled,
                            CloudEnabled = permission.CloudEnabled };
                        if (project.ReadyVersions < project.TotalVersions && !project.RetainMissingArchives)
                            project = project with { Phase = MigrationProjectPhase.AwaitingArchiveResolution };
                        else if (!permission.CanBackup && !permission.BackupReleased)
                            project = project with { Phase = MigrationProjectPhase.Blocked, Diagnostics = permission.Diagnostics };
                        else if (permission.BackupReleased && (!project.CloudSelected || permission.CloudReleased))
                            project = project with { Phase = MigrationProjectPhase.Completed };
                        else
                            project = project with { Phase = MigrationProjectPhase.Verified, Diagnostics = project.Diagnostics.AddRange(permission.Diagnostics) };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { project = project with { Phase = MigrationProjectPhase.Blocked, Diagnostics = [error.Message] }; }
                document = document with { Projects = document.Projects.SetItem(index, project) }; store.Save(document);
            }
            document = document with { Stage = MigrationWorkflowPolicy.StageFor(document.Projects) }; store.Save(document); return document;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (document is null) throw; document = document with { Stage = MigrationWorkflowStage.Paused }; store.Save(document); return document; }
        finally { Execution.Release(); }
    }

    internal async Task<MigrationWorkflowDocument> ConfirmAsync(string id, CancellationToken token = default)
    {
        if (!await Execution.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Migration workflow is already running.");
        MigrationWorkflowDocument? document = null;
        try
        {
            document = store.Load(id) ?? throw new InvalidOperationException("Migration workflow was not found.");
            for (int index = 0; index < document.Projects.Length; index++)
            {
                token.ThrowIfCancellationRequested(); var project = document.Projects[index];
                if (project.Phase != MigrationProjectPhase.Verified) continue;
                try
                {
                    if (!await backend.IsRecoveryCurrentAsync(project, token).ConfigureAwait(false))
                        project = project with { RecoveryVerified = false, Phase = MigrationProjectPhase.Pending };
                    else
                    {
                        await backend.GrantAsync(project, token).ConfigureAwait(false);
                        var permission = await backend.InspectPermissionsAsync(project, token).ConfigureAwait(false);
                        project = project with { Phase = permission.BackupReleased && (!project.CloudSelected || permission.CloudReleased)
                            ? MigrationProjectPhase.Completed : MigrationProjectPhase.Verified,
                            CanConfirmBackup = permission.CanBackup, CanConfirmCloud = permission.CanCloud && permission.CloudEnabled };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { project = project with { Phase = MigrationProjectPhase.Blocked, Diagnostics = [error.Message] }; }
                document = document with { Projects = document.Projects.SetItem(index, project) }; store.Save(document);
            }
            document = document with { Stage = MigrationWorkflowPolicy.StageFor(document.Projects) }; store.Save(document); return document;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (document is null) throw; document = document with { Stage = MigrationWorkflowStage.Paused }; store.Save(document); return document; }
        finally { Execution.Release(); }
    }
}
