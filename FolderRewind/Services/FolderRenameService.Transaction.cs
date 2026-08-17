using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public static partial class FolderRenameService
{
    public static async Task<FolderRenameResult> RenameAsync(
        ManagedFolder folder,
        string newLeafName,
        CancellationToken cancellationToken = default)
    {
        await RenameGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = PreviewRename(folder, newLeafName);
            if (!preview.IsValid)
            {
                return Failed(preview, preview.Message);
            }

            string sourcePath = NormalizePathForComparison(preview.OldPath);
            string destinationPath = NormalizePathForComparison(preview.NewPath);
            if (!Directory.Exists(sourcePath))
            {
                return Failed(preview, $"Source folder does not exist: {preview.OldPath}");
            }

            if (!TryBuildReferencePlans(preview, out var references, out string planError))
            {
                return Failed(preview, planError);
            }

            var operations = BuildMovePlan(references, sourcePath, destinationPath);
            var conflicts = ValidateMovePlan(operations);
            if (conflicts.Count > 0)
            {
                return Failed(preview, conflicts[0], references.Count, conflicts);
            }

            var memorySnapshot = CaptureMemorySnapshot(
                references,
                ConfigService.CurrentConfig?.GlobalSettings);
            var runtimeState = CaptureRuntimeState(references, preview.OldPath);
            CloseRenameDependents(preview.OldPath);

            var moveExecution = await ExecuteMovePlanCore(operations, cancellationToken);
            if (!moveExecution.Result.Success)
            {
                RestoreRuntimeState(runtimeState);
                return new FolderRenameResult
                {
                    Success = false,
                    Message = moveExecution.Result.Message,
                    OldPath = preview.OldPath,
                    NewPath = preview.NewPath,
                    AffectedConfigCount = references.Count,
                    AffectedHistoryCount = preview.AffectedHistoryCount,
                    Conflicts = moveExecution.Result.Conflicts,
                    RollbackSucceeded = moveExecution.Result.RollbackSucceeded,
                    RollbackErrors = moveExecution.Result.RollbackErrors
                };
            }

            var historyUpdate = new HistoryFolderIdentityUpdate();
            try
            {
                ApplyReferenceUpdates(references);
                UpdateGlobalPathReferences(
                    ConfigService.CurrentConfig?.GlobalSettings,
                    preview.OldPath,
                    preview.NewPath);
                historyUpdate = HistoryService.UpdateFolderIdentities(references);

                cancellationToken.ThrowIfCancellationRequested();
                var historySave = await HistoryService.SaveNowAsync(
                    publishChangedEvent: false,
                    cancellationToken);
                if (!historySave.Success)
                {
                    return await RollbackTransactionAsync(
                        preview,
                        references.Count,
                        historyUpdate,
                        memorySnapshot,
                        moveExecution.CompletedOperations,
                        runtimeState,
                        $"Failed to save history: {historySave.ErrorMessage}",
                        moveExecution.Result);
                }

                var configSave = ConfigService.SaveWithResult(publishSavedEvent: false);
                if (!configSave.Success)
                {
                    return await RollbackTransactionAsync(
                        preview,
                        references.Count,
                        historyUpdate,
                        memorySnapshot,
                        moveExecution.CompletedOperations,
                        runtimeState,
                        $"Failed to save config: {configSave.ErrorMessage}",
                        moveExecution.Result);
                }

                RestoreRuntimeState(runtimeState);
                ConfigService.PublishSaved();
                HistoryService.PublishChanged();
                return new FolderRenameResult
                {
                    Success = true,
                    Message = "Folder renamed successfully.",
                    OldPath = preview.OldPath,
                    NewPath = preview.NewPath,
                    AffectedConfigCount = references.Count,
                    AffectedHistoryCount = historyUpdate.UpdatedCount
                };
            }
            catch (Exception ex)
            {
                return await RollbackTransactionAsync(
                    preview,
                    references.Count,
                    historyUpdate,
                    memorySnapshot,
                    moveExecution.CompletedOperations,
                    runtimeState,
                    ex.Message,
                    moveExecution.Result);
            }
        }
        finally
        {
            RenameGate.Release();
        }
    }


    private static void ApplyReferenceUpdates(IReadOnlyList<FolderRenameReferencePlan> references)
    {
        foreach (var reference in references)
        {
            reference.Folder.Path = reference.NewPath;
            reference.Folder.DisplayName = reference.NewDisplayName;
        }

        foreach (var configReferences in references.GroupBy(reference => reference.Config))
        {
            var config = configReferences.Key;
            var firstReference = configReferences.First();
            if (config.Automation != null
                && AreSamePath(
                    config.Automation.TargetFolderPath,
                    firstReference.OldPath))
            {
                config.Automation.TargetFolderPath = firstReference.NewPath;
            }
        }
    }

    private static void UpdateGlobalPathReferences(
        GlobalSettings? settings,
        string oldPath,
        string newPath)
    {
        if (settings == null)
        {
            return;
        }

        if (AreSamePath(settings.LastManagerFolderPath, oldPath))
        {
            settings.LastManagerFolderPath = newPath;
        }

        if (AreSamePath(settings.LastHistoryFolderPath, oldPath))
        {
            settings.LastHistoryFolderPath = newPath;
        }
    }

    private static RenameMemorySnapshot CaptureMemorySnapshot(
        IReadOnlyList<FolderRenameReferencePlan> references,
        GlobalSettings? settings)
    {
        var automationTargets = references
            .Select(reference => reference.Config)
            .Distinct()
            .Select(config => new AutomationTargetSnapshot(
                config,
                config.Automation?.TargetFolderPath ?? string.Empty))
            .ToArray();
        return new RenameMemorySnapshot(
            references,
            automationTargets,
            settings,
            settings?.LastManagerFolderPath ?? string.Empty,
            settings?.LastHistoryFolderPath ?? string.Empty);
    }

    private static void RestoreMemorySnapshot(RenameMemorySnapshot snapshot)
    {
        foreach (var reference in snapshot.References)
        {
            reference.Folder.Path = reference.OldPath;
            reference.Folder.DisplayName = reference.OldDisplayName;
        }

        foreach (var automation in snapshot.AutomationTargets)
        {
            if (automation.Config.Automation != null)
            {
                automation.Config.Automation.TargetFolderPath = automation.TargetFolderPath;
            }
        }

        if (snapshot.Settings != null)
        {
            snapshot.Settings.LastManagerFolderPath = snapshot.LastManagerFolderPath;
            snapshot.Settings.LastHistoryFolderPath = snapshot.LastHistoryFolderPath;
        }
    }

    private static async Task<FolderRenameResult> RollbackTransactionAsync(
        FolderRenamePreview preview,
        int affectedReferenceCount,
        HistoryFolderIdentityUpdate historyUpdate,
        RenameMemorySnapshot memorySnapshot,
        IReadOnlyList<FolderMoveOperation> completedOperations,
        RenameRuntimeState runtimeState,
        string failureMessage,
        FolderRenameResult moveResult)
    {
        var rollbackErrors = new List<string>();
        try
        {
            RestoreMemorySnapshot(memorySnapshot);
        }
        catch (Exception ex)
        {
            rollbackErrors.Add($"Restore config memory: {ex.Message}");
        }

        try
        {
            HistoryService.RestoreFolderIdentities(historyUpdate.Snapshots);
        }
        catch (Exception ex)
        {
            rollbackErrors.Add($"Restore history memory: {ex.Message}");
        }

        rollbackErrors.AddRange(RollbackMoves(completedOperations));

        try
        {
            var historyRollbackSave = await HistoryService.SaveNowAsync(
                publishChangedEvent: false,
                CancellationToken.None);
            if (!historyRollbackSave.Success)
            {
                rollbackErrors.Add(
                    $"Save rolled-back history: {historyRollbackSave.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            rollbackErrors.Add($"Save rolled-back history: {ex.Message}");
        }

        var configRollbackSave = ConfigService.SaveWithResult(publishSavedEvent: false);
        if (!configRollbackSave.Success)
        {
            rollbackErrors.Add(
                $"Save rolled-back config: {configRollbackSave.ErrorMessage}");
        }

        try
        {
            RestoreRuntimeState(runtimeState);
        }
        catch (Exception ex)
        {
            rollbackErrors.Add($"Restore runtime state: {ex.Message}");
        }

        string message = rollbackErrors.Count == 0
            ? $"{failureMessage} Changes were rolled back."
            : $"{failureMessage} Rollback errors: {string.Join(" | ", rollbackErrors)}";
        return new FolderRenameResult
        {
            Success = false,
            Message = message,
            OldPath = preview.OldPath,
            NewPath = preview.NewPath,
            AffectedConfigCount = affectedReferenceCount,
            AffectedHistoryCount = historyUpdate.UpdatedCount,
            RollbackSucceeded = rollbackErrors.Count == 0,
            RollbackErrors = rollbackErrors
        };
    }

    private static RenameRuntimeState CaptureRuntimeState(
        IReadOnlyList<FolderRenameReferencePlan> references,
        string oldPath)
    {
        bool miniWindowOpen = GetPathCandidates(oldPath).Any(MiniWindowService.IsOpen);
        bool watcherRunning = GetPathCandidates(oldPath).Any(FolderWatcherService.IsWatching);
        bool watcherHadChanges = GetPathCandidates(oldPath).Any(FolderWatcherService.HasChanges);
        return new RenameRuntimeState(
            references[0].Config,
            references[0].Folder,
            miniWindowOpen,
            watcherRunning,
            watcherHadChanges);
    }

    private static void CloseRenameDependents(string oldPath)
    {
        foreach (string candidate in GetPathCandidates(oldPath))
        {
            if (MiniWindowService.IsOpen(candidate))
            {
                MiniWindowService.Close(candidate);
            }

            FolderWatcherService.StopWatching(candidate);
        }
    }

    private static void RestoreRuntimeState(RenameRuntimeState state)
    {
        if (state.MiniWindowOpen)
        {
            MiniWindowService.Open(state.Config, state.Folder);
        }
        else if (state.WatcherRunning)
        {
            FolderWatcherService.StartWatching(state.Folder.Path, state.WatcherHadChanges);
        }

        if (state.WatcherHadChanges)
        {
            FolderWatcherService.MarkChanged(state.Folder.Path);
        }
    }

    private static IReadOnlyList<string> RollbackMoves(
        IReadOnlyList<FolderMoveOperation> completed)
    {
        var errors = new List<string>();
        for (int index = completed.Count - 1; index >= 0; index--)
        {
            var operation = completed[index];
            try
            {
                if (!Directory.Exists(operation.DestinationPath))
                {
                    errors.Add($"Rollback source is missing: {operation.DestinationPath}");
                    continue;
                }

                if (Directory.Exists(operation.SourcePath) || File.Exists(operation.SourcePath))
                {
                    errors.Add($"Rollback destination is occupied: {operation.SourcePath}");
                    continue;
                }

                Directory.Move(operation.DestinationPath, operation.SourcePath);
            }
            catch (Exception ex)
            {
                errors.Add($"{operation.DestinationPath} -> {operation.SourcePath}: {ex.Message}");
            }
        }

        return errors;
    }


    private sealed record RenameRuntimeState(
        BackupConfig Config,
        ManagedFolder Folder,
        bool MiniWindowOpen,
        bool WatcherRunning,
        bool WatcherHadChanges);

    private sealed record AutomationTargetSnapshot(
        BackupConfig Config,
        string TargetFolderPath);

    private sealed record RenameMemorySnapshot(
        IReadOnlyList<FolderRenameReferencePlan> References,
        IReadOnlyList<AutomationTargetSnapshot> AutomationTargets,
        GlobalSettings? Settings,
        string LastManagerFolderPath,
        string LastHistoryFolderPath);

    private sealed record MoveExecutionResult(
        FolderRenameResult Result,
        IReadOnlyList<FolderMoveOperation> CompletedOperations);

}
