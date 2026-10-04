using CommunityToolkit.Mvvm.Input;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class HistoryPageViewModel
{
    private readonly IHistoryInteractionService _interactions;
    private readonly HistoryInteractionController _interactionController;
    private int _operationBusy;

    public HistoryPageViewModel()
        : this(new HistoryInteractionService(MainWindowService.GetXamlRoot))
    {
    }

    internal HistoryPageViewModel(IHistoryInteractionService interactions)
    {
        _interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
        _interactionController = new HistoryInteractionController(interactions);

        ChangeSelectionCommand = new AsyncRelayCommand<HistorySelectionRequest>(
            ChangeSelectionCommandAsync,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        ChangeViewModeCommand = new AsyncRelayCommand<HistoryViewMode>(
            ChangeViewModeCommandAsync,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        RetryCommand = new AsyncRelayCommand(RetryCommandAsync, CanExecuteOperation);
        ViewVersionCommand = new RelayCommand<NativeHistoryVersionViewItem>(ViewVersion);
        EditVersionCommentCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(EditVersionCommentCommandAsync, CanExecuteItemOperation);
        ToggleVersionImportantCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(ToggleVersionImportantCommandAsync, CanExecuteItemOperation);
        CreateBranchFromVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(CreateBranchFromVersionCommandAsync, CanExecuteItemOperation);
        UploadVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(UploadVersionCommandAsync, CanExecuteItemOperation);
        DownloadVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(DownloadVersionCommandAsync, CanExecuteItemOperation);
        RestoreVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(RestoreVersionCommandAsync, CanExecuteItemOperation);
        ExportVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>((item, token) => item is null ? Task.CompletedTask
            : ExecuteOperationAsync("version export", ct => ExportVersionCoreAsync(item, ct), token), CanExecuteItemOperation);
        DeleteVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(DeleteVersionCommandAsync, CanExecuteItemOperation);
        CancelOperationCommand = new RelayCommand(CancelCurrentOperationCommands, () => IsOperationBusy);

        EditRunCommentCommand = new AsyncRelayCommand<BackupRunViewItem>(EditRunCommentCommandAsync, CanExecuteItemOperation);
        ToggleRunImportantCommand = new AsyncRelayCommand<BackupRunViewItem>(ToggleRunImportantCommandAsync, CanExecuteItemOperation);
        CreateBranchFromRunSourceCommand = new AsyncRelayCommand<BackupRunSourceViewItem>(CreateBranchFromRunSourceCommandAsync, CanExecuteItemOperation);
        RestoreRunSourceCommand = new AsyncRelayCommand<BackupRunSourceViewItem>((item, token) => item is null ? Task.CompletedTask
            : ExecuteOperationAsync("run source restore", ct => RestoreRunSourceCoreAsync(item, ct), token), CanExecuteItemOperation);
        ShowRunSourceHistoryCommand = new AsyncRelayCommand<BackupRunSourceViewItem>(ShowRunSourceHistoryAsync, CanExecuteItemOperation);
        RestoreRunCommand = new AsyncRelayCommand<BackupRunViewItem>(RestoreRunCommandAsync, CanExecuteItemOperation);
        DeleteRunCommand = new AsyncRelayCommand<BackupRunViewItem>(DeleteRunCommandAsync, CanExecuteItemOperation);

        MergeBranchCommand = new AsyncRelayCommand(MergeBranchCommandAsync, () => CanExecuteOperation() && CanUsePerSourceActions);
        CheckoutBranchCommand = new AsyncRelayCommand(CheckoutBranchCommandAsync, () => CanExecuteOperation() && CanStartCheckoutSelectedBranch);
        ReconcileBranchCommand = new AsyncRelayCommand(ReconcileBranchCommandAsync, () => CanExecuteOperation() && CanReconcileSelectedBranch);
        RenameBranchCommand = new AsyncRelayCommand(RenameBranchCommandAsync, () => CanExecuteOperation() && CanRenameSelectedBranch);
        DeleteBranchCommand = new AsyncRelayCommand(DeleteBranchCommandAsync, () => CanExecuteOperation() && CanDeleteSelectedBranch);
        OpenCloudSyncCommand = new AsyncRelayCommand(OpenCloudSyncCommandAsync, () => CanExecuteOperation() && CanOpenConfigCloudSync);
        ManageSafetySnapshotsCommand = new AsyncRelayCommand(ManageSafetySnapshotsCommandAsync, CanExecuteOperation);
        ClearMissingCommand = new AsyncRelayCommand(ClearMissingCommandAsync, () => CanExecuteOperation() && HasMissing);
        ScanRecoverCommand = new AsyncRelayCommand(ScanRecoverCommandAsync, () => CanExecuteOperation() && CanUsePerSourceActions);
    }

    internal sealed record HistorySelectionRequest(
        BackupConfig Config,
        ManagedFolder? Folder,
        bool RefreshHistory,
        bool PersistSelection);

    internal IAsyncRelayCommand<HistorySelectionRequest> ChangeSelectionCommand { get; }
    internal IAsyncRelayCommand<HistoryViewMode> ChangeViewModeCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IRelayCommand<NativeHistoryVersionViewItem> ViewVersionCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> EditVersionCommentCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> ToggleVersionImportantCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> CreateBranchFromVersionCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> UploadVersionCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> DownloadVersionCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> RestoreVersionCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> ExportVersionCommand { get; }
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> DeleteVersionCommand { get; }
    public IRelayCommand CancelOperationCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> EditRunCommentCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> ToggleRunImportantCommand { get; }
    public IAsyncRelayCommand<BackupRunSourceViewItem> CreateBranchFromRunSourceCommand { get; }
    public IAsyncRelayCommand<BackupRunSourceViewItem> RestoreRunSourceCommand { get; }
    public IAsyncRelayCommand<BackupRunSourceViewItem> ShowRunSourceHistoryCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> RestoreRunCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> DeleteRunCommand { get; }
    public IAsyncRelayCommand MergeBranchCommand { get; }
    public IAsyncRelayCommand CheckoutBranchCommand { get; }
    public IAsyncRelayCommand ReconcileBranchCommand { get; }
    public IAsyncRelayCommand RenameBranchCommand { get; }
    public IAsyncRelayCommand DeleteBranchCommand { get; }
    public IAsyncRelayCommand OpenCloudSyncCommand { get; }
    public IAsyncRelayCommand ManageSafetySnapshotsCommand { get; }
    public IAsyncRelayCommand ClearMissingCommand { get; }
    public IAsyncRelayCommand ScanRecoverCommand { get; }

    public bool IsOperationBusy => Volatile.Read(ref _operationBusy) != 0;

    private bool CanExecuteOperation() => !IsOperationBusy;

    private bool CanExecuteItemOperation<T>(T? item) where T : class
        => item is not null && CanExecuteOperation();

    private async Task ChangeSelectionCommandAsync(
        HistorySelectionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return;
        }

        CancelCurrentOperationCommands();
        try
        {
            await SetCurrentSelectionAsync(
                request.Config,
                request.Folder,
                request.RefreshHistory,
                request.PersistSelection,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var message = I18n.Format(
                "History_NativeInitializationFailed",
                request.Config.Name,
                ex.Message);
            ReportLoadFailure(message);
            LogService.LogError(message, nameof(HistoryPageViewModel), ex);
            _interactions.Notify(HistoryNotificationKind.Error, message);
        }
    }

    private async Task ChangeViewModeCommandAsync(HistoryViewMode mode, CancellationToken cancellationToken)
    {
        try
        {
            await SetHistoryViewModeAsync(mode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportOperationFailure("view mode", ex);
            _interactions.Notify(HistoryNotificationKind.Error, ex.Message);
        }
    }

    private async Task RetryCommandAsync(CancellationToken cancellationToken)
        => await ExecuteOperationAsync(
            "retry",
            RefreshCurrentHistoryAsync,
            cancellationToken);

    private void ViewVersion(NativeHistoryVersionViewItem? item)
    {
        if (item is null || TryRevealBackupFile(item, out var errorMessage))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _interactions.Notify(HistoryNotificationKind.Warning, errorMessage);
        }
    }

    private Task EditVersionCommentCommandAsync(
        NativeHistoryVersionViewItem? item,
        CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "version comment",
                async token =>
                {
                    _ = await _interactionController.RequestTextAndExecuteAsync(
                        I18n.GetString("History_EditComment_Title"),
                        I18n.GetString("History_EditComment_Placeholder"),
                        item.Comment,
                        (comment, operationToken) => UpdateCommentAsync(item, comment, operationToken),
                        I18n.GetString("Common_Failed"),
                        token);
                },
                cancellationToken);

    private Task ToggleVersionImportantCommandAsync(
        NativeHistoryVersionViewItem? item,
        CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "version pin",
                async token => _ = await ToggleImportantAsync(item, token),
                cancellationToken);

    private Task CreateBranchFromVersionCommandAsync(
        NativeHistoryVersionViewItem? item,
        CancellationToken cancellationToken)
        => item?.BranchableCheckpointId is not { } checkpointId
            ? Task.CompletedTask
            : CreateBranchFromCheckpointCommandAsync(checkpointId, cancellationToken);

    private Task CreateBranchFromRunSourceCommandAsync(
        BackupRunSourceViewItem? item,
        CancellationToken cancellationToken)
        => item?.CheckpointId is not { } checkpointId
            ? Task.CompletedTask
            : CreateBranchFromCheckpointCommandAsync(checkpointId, cancellationToken);

    private Task CreateBranchFromCheckpointCommandAsync(
        CheckpointId checkpointId,
        CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            "branch create",
            async token =>
            {
                var created = await _interactionController.RequestTextAndExecuteAsync(
                    I18n.GetString("History_Branch_CreateFromHereTitle"),
                    I18n.GetString("History_Branch_CreateFromHereTitle"),
                    string.Empty,
                    (name, _) => CreateBranchAsync(checkpointId, name),
                    I18n.GetString("History_Branch_NoCheckpoint"),
                    token,
                    allowEmpty: false);
                if (created)
                {
                    await RefreshCurrentHistoryAsync(token);
                }
            },
            cancellationToken);

    private Task RenameBranchCommandAsync(CancellationToken cancellationToken)
    {
        var branch = SelectedBranch;
        return branch is null || !branch.CanRename
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "branch rename",
                async token =>
                {
                    var renamed = await _interactionController.RequestTextAndExecuteAsync(
                        I18n.GetString("History_Branch_RenameTitle"),
                        I18n.GetString("History_Branch_RenameTitle"),
                        branch.Name,
                        (name, _) => RenameBranchAsync(branch, name),
                        I18n.GetString("Common_Failed"),
                        token,
                        allowEmpty: false);
                    if (renamed)
                    {
                        await RefreshCurrentHistoryAsync(token);
                    }
                },
                cancellationToken);
    }

    private Task DeleteBranchCommandAsync(CancellationToken cancellationToken)
    {
        var branch = SelectedBranch;
        return branch is null || !branch.CanDelete
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "branch delete",
                async token =>
                {
                    if (await DeleteBranchAsync(branch))
                    {
                        await RefreshCurrentHistoryAsync(token);
                    }
                },
                cancellationToken);
    }

    private Task MergeBranchCommandAsync(CancellationToken cancellationToken)
    {
        OpenMergeWorkspace();
        return Task.CompletedTask;
    }

    private Task CheckoutBranchCommandAsync(CancellationToken cancellationToken)
    {
        var branch = SelectedBranch;
        return branch is null || !branch.CanStartCheckout
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "branch checkout",
                token => CheckoutBranchCoreAsync(branch, token),
                cancellationToken);
    }

    private async Task CheckoutBranchCoreAsync(BranchViewItem branch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = await PlanCheckoutBranchTipAsync(branch);
            if (plan is null)
            {
                return;
            }

            switch (plan.Readiness)
            {
                case HistoryCheckoutReadiness.Ready:
                case HistoryCheckoutReadiness.ProtectionRequired:
                    if (!await _interactions.ConfirmAsync(
                            I18n.GetString("History_Branch_CheckoutTitle"),
                            plan.RequiresProtection
                                ? I18n.GetString("History_Branch_CheckoutProtectionContent")
                                : I18n.GetString("History_Branch_CheckoutContent"),
                            I18n.GetString("History_Branch_CheckoutPrimary"),
                            true,
                            cancellationToken))
                    {
                        return;
                    }
                    var restore = await CheckoutBranchTipAsync(branch);
                    if (restore?.Succeeded == true)
                    {
                        await RefreshCurrentHistoryAsync(cancellationToken);
                        return;
                    }
                    ShowInteractionWarning(restore?.Diagnostic ?? plan.Diagnostic);
                    return;

                case HistoryCheckoutReadiness.PreparationRequired:
                    if (!await _interactions.ConfirmAsync(
                            I18n.GetString("History_Checkout_PrepareTitle"),
                            I18n.GetString("History_Checkout_PrepareContent"),
                            I18n.GetString("History_Checkout_PreparePrimary"),
                            true,
                            cancellationToken))
                    {
                        return;
                    }
                    var prepared = await PrepareCheckoutBranchTipAsync(branch);
                    if (prepared?.Readiness is not (HistoryCheckoutReadiness.Ready
                        or HistoryCheckoutReadiness.ProtectionRequired))
                    {
                        ShowInteractionWarning(prepared?.Diagnostic);
                        return;
                    }
                    continue;

                case HistoryCheckoutReadiness.ConfigurationMappingRequired:
                    if (!await RepairMissingSourcesCommandAsync(plan, cancellationToken))
                    {
                        return;
                    }
                    continue;

                case HistoryCheckoutReadiness.ConfigurationBoundaryChangeRequired:
                    if (!await RepairFirstBoundaryCommandAsync(plan, cancellationToken))
                    {
                        return;
                    }
                    continue;

                case HistoryCheckoutReadiness.StalePlan:
                    continue;

                default:
                    ShowInteractionWarning(plan.Diagnostic);
                    return;
            }
        }

        ShowInteractionWarning(I18n.GetString("History_CheckoutReadiness_StalePlan"));
    }

    private async Task<bool> RepairMissingSourcesCommandAsync(
        HistoryCheckoutPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var missing in plan.MissingHistoricalSources)
        {
            var expectedRevision = CurrentConfigRevision;
            if (expectedRevision is null)
            {
                return false;
            }

            var path = await _interactions.RequestTextAsync(
                I18n.GetString("History_Checkout_MissingSourceTitle"),
                I18n.Format(
                    "History_Checkout_MissingSourceDescription",
                    missing.Descriptor.DisplayName,
                    missing.SuggestedPath),
                missing.SuggestedPath,
                cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var result = RepairMissingSource(missing, path.Trim(), expectedRevision);
            if (!result.Succeeded)
            {
                ShowInteractionWarning(result.Diagnostic);
                return false;
            }
        }

        await RefreshCurrentHistoryAsync(cancellationToken);
        return true;
    }

    private async Task<bool> RepairFirstBoundaryCommandAsync(
        HistoryCheckoutPlan plan,
        CancellationToken cancellationToken)
    {
        var mismatch = plan.BoundaryMismatches.FirstOrDefault();
        var expectedRevision = CurrentConfigRevision;
        if (mismatch is null || expectedRevision is null)
        {
            return false;
        }

        var confirmed = await _interactions.ConfirmAsync(
            I18n.GetString("History_Checkout_BoundaryTitle"),
            I18n.Format(
                "History_Checkout_BoundaryDescription",
                _currentConfig?.SourceFolders.FirstOrDefault(folder => Guid.TryParse(folder.Id, out var source) && source == mismatch.SourceId.Value)?.DisplayName ?? I18n.GetString("History_SourceUnavailable"),
                FormatBoundary(mismatch.CurrentBoundary),
                FormatBoundary(mismatch.HistoricalBoundary)),
            I18n.GetString("History_Checkout_RepairBoundaryPrimary"),
            true,
            cancellationToken);
        if (!confirmed)
        {
            return false;
        }

        var result = RepairHistoricalBoundary(mismatch, expectedRevision);
        if (!result.Succeeded)
        {
            ShowInteractionWarning(result.Diagnostic);
            return false;
        }

        await RefreshCurrentHistoryAsync(cancellationToken);
        return true;
    }

    private Task ReconcileBranchCommandAsync(CancellationToken cancellationToken)
    {
        var branch = SelectedBranch;
        return branch is null || !branch.CanReconcile
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "branch reconcile",
                async token =>
                {
                    var result = await _interactions.ChooseAsync(
                        new HistoryChoiceRequest(
                            I18n.GetString("History_Branch_SelectTipTitle"),
                            string.Empty,
                            branch.Tips
                                .Select(tip => new HistoryChoiceOption(tip.UpdateId.ToString(), I18n.Format("History_BranchTipName", tip.Name, UserDisplayFormatter.LongDateTime(tip.CreatedAtUtc.ToLocalTime()))))
                                .ToArray(),
                            I18n.GetString("Common_Ok")),
                        token);
                    if (result.Outcome != HistoryInteractionOutcome.Primary
                        || result.Value is null
                        || !Guid.TryParse(result.Value, out var updateId))
                    {
                        return;
                    }

                    try
                    {
                        if (await ReconcileBranchAsync(branch, new BranchUpdateId(updateId)))
                        {
                            await RefreshCurrentHistoryAsync(token);
                        }
                    }
                    catch (HistoryBranchCommandException ex)
                    {
                        ShowInteractionWarning(ex.Message);
                        await RefreshCurrentHistoryAsync(token);
                    }
                },
                cancellationToken);
    }

    private Task EditRunCommentCommandAsync(BackupRunViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "run comment",
                async token =>
                {
                    _ = await _interactionController.RequestTextAndExecuteAsync(
                        I18n.GetString("History_EditComment_Title"),
                        I18n.GetString("History_EditComment_Placeholder"),
                        item.Comment,
                        (comment, operationToken) => UpdateRunCommentAsync(item, comment, operationToken),
                        I18n.GetString("Common_Failed"),
                        token);
                },
                cancellationToken);

    private Task ToggleRunImportantCommandAsync(BackupRunViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "run pin",
                async token => _ = await ToggleRunImportantAsync(item, token),
                cancellationToken);

    private Task RestoreVersionCommandAsync(NativeHistoryVersionViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync("version restore", token => RestoreVersionCoreAsync(item, token), cancellationToken);

    private async Task ExportVersionCoreAsync(NativeHistoryVersionViewItem item, CancellationToken token)
    {
        var config = _currentConfig;
        var folder = _currentFolder;
        if (config is null || folder is null || !await VerifyPasswordIfRequiredAsync(config, token)) return;
        var assessment = await NativeHistoryApplicationService.PreviewExportAsync(config, item.VersionId, token);
        if (assessment.Readiness == HistoryReadiness.PreparationRequired)
        {
            var preparation = await CloudSyncService.PreviewVersionForExportPreparationAsync(config, folder, item.VersionId, token);
            var text = CloudSetupViewModel.DescribeRecovery(preparation);
            if (!preparation.CanPrepare) { _interactions.Notify(HistoryNotificationKind.Warning, text); return; }
            if (!await _interactions.ConfirmAsync(I18n.GetString("Export_Title"), text, I18n.GetString("Common_Confirm"), cancellationToken: token)) return;
            await CloudSyncService.PrepareVersionForExportAsync(config, folder, item.VersionId, token, preparation.Assessment.Selected!.RepresentationId);
            assessment = await NativeHistoryApplicationService.PreviewExportAsync(config, item.VersionId, token);
        }
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
        {
            _interactions.Notify(HistoryNotificationKind.Warning, GetReadinessMessage(assessment.Readiness));
            return;
        }
        var parent = await _interactions.PickFolderAsync(token);
        if (string.IsNullOrWhiteSpace(parent)) return;
        var destination = System.IO.Path.Combine(parent, "FolderRewind-restored-" + Guid.NewGuid().ToString("N"));
        var choice = await _interactions.ChooseAsync(new HistoryChoiceRequest(
            I18n.GetString("Export_Title"),
            item.IsPartialBackup ? I18n.GetString("Export_PartialNotice") : I18n.GetString("Restore_NewLocationHelp"), [],
            I18n.GetString("Common_Confirm"), Fields: ReviewFields(folder.DisplayName, item.DateDisplay + " " + item.TimeDisplay, item.Comment, destination)), token);
        if (choice.Outcome != HistoryInteractionOutcome.Primary) return;
        SetOperationStatus("Restore_Progress");
        token.ThrowIfCancellationRequested();
        await NativeHistoryApplicationService.ExportVersionAsync(config, folder, item.VersionId, destination, token);
        _interactions.Notify(HistoryNotificationKind.Success, I18n.Format("Export_Completed", destination));
    }

    private async Task RestoreVersionCoreAsync(
        NativeHistoryVersionViewItem item,
        CancellationToken cancellationToken)
    {
        var config = _currentConfig;
        var folder = _currentFolder;
        if (config is null || folder is null
            || !await VerifyPasswordIfRequiredAsync(config, cancellationToken))
        {
            return;
        }

        var preview = await NativeHistoryApplicationService.PreviewVersionRestoreAsync(config, folder, item.VersionId, cancellationToken);
        if (preview.Assessment.Readiness != HistoryReadiness.Ready || preview.Assessment.Selected is null)
        {
            _interactions.Notify(HistoryNotificationKind.Warning, GetReadinessMessage(preview.Assessment.Readiness));
            return;
        }
        var boundary = preview.Version.EffectiveSourceBoundary;
        var currentBoundary = EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters);
        var risk = I18n.GetString(config.Archive.BackupBeforeRestore ? "Restore_SafetyEnabled" : "Restore_SafetyDisabled");
        if (boundary.Fingerprint != currentBoundary.Fingerprint) risk += "\n" + I18n.GetString("Restore_BoundaryDifference");
        var fields = ReviewFields(folder.DisplayName, item.DateDisplay + " " + item.TimeDisplay, item.Comment, folder.Path).ToList();
        var restrictedLegacy = preview.Version.BoundaryConfidence == HistoricalBoundaryConfidence.Unknown;
        fields.Add(new(I18n.GetString("Restore_FieldScope"), restrictedLegacy
            ? I18n.GetString("LegacyMigration_BoundaryUnknown") : FormatBoundary(boundary)));
        if (restrictedLegacy) risk += "\n" + I18n.GetString("LegacyMigration_Boundary");
        var mode = await ChooseRestoreModeAsync(item.IsPartialBackup || restrictedLegacy, false, risk, cancellationToken, fields);
        if (mode is null)
        {
            return;
        }

        SetOperationStatus("Restore_Progress");
        var result = await NativeHistoryApplicationService.RestoreVersionAsync(
            config,
            folder,
            item.VersionId,
            mode.Value,
            cancellationToken,
            expectedConfigSignature: preview.ConfigSignature);
        _interactions.NotifyRestoreCompleted(
            folder.DisplayName,
            result?.Succeeded == true,
            result?.Succeeded == true ? null : NormalizeDiagnostic(result?.Diagnostic));
    }

    private Task RestoreRunCommandAsync(BackupRunViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync("run restore", token => RestoreRunCoreAsync(item, token), cancellationToken);

    private async Task RestoreRunCoreAsync(BackupRunViewItem item, CancellationToken cancellationToken)
    {
        var config = _currentConfig;
        if (config is null || !await VerifyPasswordIfRequiredAsync(config, cancellationToken))
        {
            return;
        }

        var options = await NativeHistoryApplicationService.GetRunRestoreOptionsAsync(config, item.RunId, cancellationToken);
        var selected = await _interactions.SelectManyAsync(I18n.GetString("History_Run_SelectRestoreSources"),
            I18n.GetString("History_Run_SelectRestoreSourcesHint"), options.Select(option => new HistorySelectionOption(
                option.SourceId.ToString(), I18n.Format("History_Run_RestoreSourceReview", option.Name,
                    option.VersionId?.ToString()[..8] ?? "—", option.BranchName, option.Diagnostic), option.CanRestore)).ToArray(), cancellationToken);
        if (selected is null || selected.Count == 0) return;
        var selectedIds = selected.Select(SourceId.Parse).ToHashSet();
        var mode = await ChooseRestoreModeAsync(options.Any(option => selectedIds.Contains(option.SourceId) && option.IsPartial),
            isRun: true, I18n.GetString(config.Archive.BackupBeforeRestore ? "Restore_SafetyEnabled" : "Restore_SafetyDisabled"),
            cancellationToken, ReviewFields(config.Name, item.DateDisplay + " " + item.TimeDisplay, item.Comment,
                string.Join("\n", config.SourceFolders.Where(folder => selectedIds.Contains(new SourceId(Guid.Parse(folder.Id)))).Select(folder => folder.Path))));
        if (mode is null) return;

        SetOperationStatus("Restore_Progress");
        var result = await NativeHistoryApplicationService.RestoreRunAsync(config, item.RunId,
            selectedIds.ToArray(), mode.Value, cancellationToken);
        _interactions.NotifyRestoreCompleted(config.Name, result.Succeeded, NormalizeDiagnostic(result.Diagnostic));
        if (result.Succeeded) await RefreshCurrentHistoryAsync(cancellationToken);
    }

    private async Task RestoreRunSourceCoreAsync(BackupRunSourceViewItem item, CancellationToken token)
    {
        var config = _currentConfig;
        if (config is null || !item.CanRestore || !await VerifyPasswordIfRequiredAsync(config, token)) return;
        if (!await _interactions.ConfirmAsync(I18n.GetString("History_Run_RestoreSource"),
            I18n.Format("History_Run_RestoreSourceConfirm", item.Name, item.VersionDisplay, item.BranchName),
            I18n.GetString("Common_Ok"), true, token)) return;
        var result = await NativeHistoryApplicationService.RestoreRunAsync(config, item.RunId,
            [item.SourceId], BackupService.RestoreMode.Clean, token);
        _interactions.NotifyRestoreCompleted(item.Name, result.Succeeded, NormalizeDiagnostic(result.Diagnostic));
        if (result.Succeeded) await RefreshCurrentHistoryAsync(token);
    }

    private async Task ShowRunSourceHistoryAsync(BackupRunSourceViewItem? item, CancellationToken token)
    {
        if (item is null || _currentConfig is not { } config) return;
        var folder = config.SourceFolders.FirstOrDefault(f => Guid.TryParse(f.Id, out var id) && id == item.SourceId.Value);
        if (folder is null) return;
        _viewMode = HistoryViewMode.PerSource;
        if (Settings is not null) { Settings.LastHistoryViewMode = _viewMode; await ConfigService.SaveAsync(cancellationToken: token); }
        NotifyViewModeChanged();
        await SetCurrentSelectionAsync(config, folder, true, true, token);
    }

    private async Task<BackupService.RestoreMode?> ChooseRestoreModeAsync(
        bool partial, bool isRun, string risk, CancellationToken cancellationToken,
        IReadOnlyList<HistoryReviewField>? fields = null)
    {
        var message = I18n.GetString(partial ? "Restore_PartialHelp" : "Restore_ModesHelp") + "\n\n" + risk;
        var result = await _interactions.ChooseAsync(new HistoryChoiceRequest(
            I18n.GetString(partial ? "History_PartialRestore_Title" : isRun ? "History_Run_RestoreTitle" : "History_RestoreConfirm_Title"),
            message, [], I18n.GetString(partial ? "Restore_Overwrite" : "History_RestoreConfirm_Primary"),
            partial ? null : I18n.GetString("Restore_Overwrite"), IsDestructive: true, Fields: fields), cancellationToken);
        return result.Outcome switch
        {
            HistoryInteractionOutcome.Primary => partial ? BackupService.RestoreMode.Overwrite : BackupService.RestoreMode.Clean,
            HistoryInteractionOutcome.Secondary => BackupService.RestoreMode.Overwrite,
            _ => null
        };
    }

    private static IReadOnlyList<HistoryReviewField> ReviewFields(string content, string time, string note, string target)
    {
        var fields = new List<HistoryReviewField> { new(I18n.GetString("Restore_FieldContent"), content), new(I18n.GetString("Restore_FieldTime"), time) };
        if (!string.IsNullOrWhiteSpace(note)) fields.Add(new(I18n.GetString("Restore_FieldNote"), note));
        fields.Add(new(I18n.GetString("Restore_FieldTarget"), target));
        return fields;
    }

    private static string GetReadinessMessage(HistoryReadiness readiness) => I18n.GetString(readiness switch
    {
        HistoryReadiness.PreparationRequired => "History_NativeReadiness_PreparationRequired",
        _ => "Restore_NotReady"
    });

    private string _operationStatus = string.Empty;
    public string OperationStatus => _operationStatus;
    private void SetOperationStatus(string key) { _operationStatus = I18n.GetString(key); OnPropertyChanged(nameof(OperationStatus)); }

    private async Task<bool> VerifyPasswordIfRequiredAsync(
        BackupConfig config,
        CancellationToken cancellationToken)
    {
        if (!config.IsEncrypted)
        {
            return true;
        }

        var password = await _interactions.RequestTextAsync(
            I18n.GetString("Encryption_RestorePasswordTitle"),
            I18n.GetString("Encryption_EnterPasswordPlaceholder"),
            isPassword: true,
            cancellationToken: cancellationToken);
        if (password is not null && EncryptionService.VerifyPassword(config.Id, password))
        {
            return true;
        }

        if (password is not null)
        {
            _interactions.Notify(HistoryNotificationKind.Error, I18n.GetString("Encryption_WrongPasswordDesc"));
        }
        return false;
    }

    private Task DeleteRunCommandAsync(BackupRunViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "run delete",
                async token =>
                {
                    if (item.IsImportant && !await ConfirmDeleteImportantAsync(token))
                    {
                        return;
                    }

                    _ = await _interactionController.ConfirmAndExecuteAsync(
                        I18n.GetString("History_Run_DeleteTitle"),
                        I18n.GetString("History_Run_DeleteContent"),
                        I18n.GetString("Common_Delete"),
                        _ => DeleteRunAsync(item),
                        I18n.GetString("History_Run_DeleteFailed"),
                        true,
                        token);
                },
                cancellationToken);

    private Task DeleteVersionCommandAsync(NativeHistoryVersionViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync("version delete", token => DeleteVersionCoreAsync(item, token), cancellationToken);

    private async Task DeleteVersionCoreAsync(
        NativeHistoryVersionViewItem item,
        CancellationToken cancellationToken)
    {
        if (item.IsImportant && !await ConfirmDeleteImportantAsync(cancellationToken))
        {
            return;
        }

        var localDeletionBlocker = item.HasLocalFile
            ? await GetLocalDeletionBlockerAsync(item)
            : null;
        var options = new List<HistoryChoiceOption>
        {
            new(((int)BackupDeleteMode.RecordOnly).ToString(CultureInfo.InvariantCulture),
                I18n.GetString("History_DeleteMode_RecordOnly"))
        };
        if (item.HasLocalFile && string.IsNullOrWhiteSpace(localDeletionBlocker))
        {
            options.Add(new(
                ((int)BackupDeleteMode.LocalArchiveOnly).ToString(CultureInfo.InvariantCulture),
                I18n.GetString("History_DeleteMode_LocalOnly")));
            options.Add(new(
                ((int)BackupDeleteMode.LocalArchiveAndRecord).ToString(CultureInfo.InvariantCulture),
                I18n.GetString("History_DeleteMode_LocalAndRecord")));
        }

        var choice = await _interactions.ChooseAsync(
            new HistoryChoiceRequest(
                I18n.GetString("History_DeleteConfirm_Title"),
                string.IsNullOrWhiteSpace(localDeletionBlocker)
                    ? I18n.Format("History_DeleteConfirm_Content", item.FileName)
                    : $"{I18n.Format("History_DeleteConfirm_Content", item.FileName)}\n{localDeletionBlocker}",
                options,
                I18n.GetString("History_Rewrite_Continue"),
                InitialValue: item.HasLocalFile && string.IsNullOrWhiteSpace(localDeletionBlocker)
                    ? ((int)BackupDeleteMode.LocalArchiveAndRecord).ToString(CultureInfo.InvariantCulture)
                    : ((int)BackupDeleteMode.RecordOnly).ToString(CultureInfo.InvariantCulture),
                IsDestructive: true),
            cancellationToken);
        if (choice.Outcome != HistoryInteractionOutcome.Primary
            || !int.TryParse(choice.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawMode)
            || !Enum.IsDefined(typeof(BackupDeleteMode), rawMode))
        {
            return;
        }

        var progress = new Progress<FolderRewind.History.Retention.HistoryChainRewriteProgress>(update =>
            SetOperationStatus(update.Stage switch
            {
                "analyze" => "History_Rewrite_Analyzing",
                "verify" => "History_Rewrite_Verifying",
                "ready" => "History_Rewrite_Ready",
                _ => "History_Rewrite_Preparing"
            }));
        var result = await DeleteVersionAsync(item, (BackupDeleteMode)rawMode,
            async (prepared, token) => await _interactions.ConfirmAsync(
                I18n.GetString("History_DeleteConfirm_Title"),
                I18n.Format("History_Rewrite_Confirm", item.FileName, prepared.Mappings.Length,
                    (prepared.CreatedBytes / 1048576d).ToString("N2", CultureInfo.CurrentCulture),
                    (prepared.ReclaimedBytes / 1048576d).ToString("N2", CultureInfo.CurrentCulture),
                    (prepared.NetReleasedBytes / 1048576d).ToString("N2", CultureInfo.CurrentCulture)),
                I18n.GetString("Common_Ok"), true, token), progress, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(result.Message))
            _interactions.Notify(HistoryNotificationKind.Warning, result.Message);
        if (!result.Success)
        {
            _interactions.Notify(
                HistoryNotificationKind.Error,
                string.IsNullOrWhiteSpace(result.Message)
                    ? I18n.GetString("BackupService_Task_Failed")
                    : result.Message);
            return;
        }

        await RefreshCurrentHistoryAsync(cancellationToken);
    }

    private Task UploadVersionCommandAsync(NativeHistoryVersionViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "cloud upload",
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await UploadToCloudAsync(item);
                },
                cancellationToken);

    private Task DownloadVersionCommandAsync(NativeHistoryVersionViewItem? item, CancellationToken cancellationToken)
        => item is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "cloud download",
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await DownloadFromCloudAsync(item);
                },
                cancellationToken);

    private Task OpenCloudSyncCommandAsync(CancellationToken cancellationToken)
    {
        var config = _currentConfig;
        return config is null
            ? Task.CompletedTask
            : ExecuteOperationAsync(
                "cloud sync",
                async token =>
                {
                    await _interactions.OpenCloudSyncAsync(config.Id, token);
                    await RefreshCurrentHistoryAsync(token);
                },
                cancellationToken);
    }

    private Task ClearMissingCommandAsync(CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            "clear missing",
            async token =>
            {
                var count = GetMissingCount();
                if (count <= 0)
                {
                    return;
                }

                var cleared = await _interactionController.ConfirmAndExecuteAsync(
                    I18n.GetString("History_ClearMissingConfirm_Title"),
                    I18n.Format("History_ClearMissingConfirm_Content", count),
                    I18n.GetString("History_ClearMissingConfirm_Primary"),
                    async _ => await ClearMissingEntriesAsync() >= 0,
                    I18n.GetString("Common_Failed"),
                    true,
                    token);
                if (cleared)
                {
                    await RefreshCurrentHistoryAsync(token);
                }
            },
            cancellationToken);

    private Task ScanRecoverCommandAsync(CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            "scan recover",
            async token =>
            {
                if (_currentConfig is null || _currentFolder is null)
                {
                    _interactions.Notify(
                        HistoryNotificationKind.Warning,
                        I18n.GetString("History_ScanRecover_SelectFirst"));
                    return;
                }

                var path = await _interactions.PickFolderAsync(token);
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var recovered = await ScanAndRecoverHistoryAsync(path);
                if (recovered > 0)
                {
                    _interactions.Notify(
                        HistoryNotificationKind.Success,
                        I18n.Format("History_ScanRecover_ResultSuccess", recovered));
                    await RefreshCurrentHistoryAsync(token);
                }
                else
                {
                    _interactions.Notify(
                        HistoryNotificationKind.Info,
                        I18n.GetString("History_ScanRecover_ResultNone"));
                }
            },
            cancellationToken);

    private Task ManageSafetySnapshotsCommandAsync(CancellationToken cancellationToken)
        => ExecuteOperationAsync(
            "safety snapshot",
            async token =>
            {
                var config = _currentConfig;
                if (config is null)
                {
                    return;
                }
                if (ActiveSafetySnapshots.Count == 0)
                {
                    _interactions.Notify(
                        HistoryNotificationKind.Warning,
                        I18n.GetString("History_SafetySnapshot_None"));
                    return;
                }

                var result = await _interactions.ChooseAsync(
                    new HistoryChoiceRequest(
                        I18n.GetString("History_SafetySnapshot_Title"),
                        string.Empty,
                        ActiveSafetySnapshots
                            .Select(item => new HistoryChoiceOption(item.SnapshotId.ToString(), item.DisplayName))
                            .ToArray(),
                        I18n.GetString("History_SafetySnapshot_RestorePrimary"),
                        I18n.GetString("History_SafetySnapshot_ReleaseSecondary")),
                    token);
                var snapshot = ActiveSafetySnapshots.FirstOrDefault(
                    item => string.Equals(item.SnapshotId.ToString(), result.Value, StringComparison.Ordinal));
                if (snapshot is null)
                {
                    return;
                }

                if (result.Outcome == HistoryInteractionOutcome.Primary)
                {
                    if (!await VerifyPasswordIfRequiredAsync(config, token))
                    {
                        return;
                    }
                    var restore = await RestoreSafetySnapshotAsync(snapshot);
                    if (restore?.Succeeded != true)
                    {
                        ShowInteractionWarning(restore?.Diagnostic);
                        return;
                    }
                }
                else if (result.Outcome == HistoryInteractionOutcome.Secondary)
                {
                    if (!await ReleaseSafetySnapshotAsync(snapshot))
                    {
                        ShowInteractionWarning(I18n.GetString("History_SafetySnapshot_AlreadyReleased"));
                    }
                }
                else
                {
                    return;
                }

                await RefreshCurrentHistoryAsync(token);
            },
            cancellationToken);

    private Task<bool> ConfirmDeleteImportantAsync(CancellationToken cancellationToken)
        => _interactions.ConfirmAsync(
            I18n.GetString("History_DeleteImportant_Title"),
            I18n.GetString("History_DeleteImportant_Content"),
            I18n.GetString("History_DeleteImportant_Continue"),
            true,
            cancellationToken);

    private async Task ExecuteOperationAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationBusy, 1, 0) != 0)
        {
            return;
        }

        SetOperationStatus("History_OperationProgress");
        NotifyCommandStateChanged();
        OnPropertyChanged(nameof(IsOperationBusy));
        try
        {
            ErrorMessage = string.Empty;
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportOperationFailure(operationName, ex);
            _interactions.Notify(HistoryNotificationKind.Error, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _operationBusy, 0);
            OnPropertyChanged(nameof(IsOperationBusy));
            NotifyCommandStateChanged();
        }
    }

    private void NotifyCommandStateChanged()
    {
        CancelOperationCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        EditVersionCommentCommand.NotifyCanExecuteChanged();
        ToggleVersionImportantCommand.NotifyCanExecuteChanged();
        CreateBranchFromVersionCommand.NotifyCanExecuteChanged();
        UploadVersionCommand.NotifyCanExecuteChanged();
        DownloadVersionCommand.NotifyCanExecuteChanged();
        RestoreVersionCommand.NotifyCanExecuteChanged();
        ExportVersionCommand.NotifyCanExecuteChanged();
        DeleteVersionCommand.NotifyCanExecuteChanged();
        EditRunCommentCommand.NotifyCanExecuteChanged();
        ToggleRunImportantCommand.NotifyCanExecuteChanged();
        CreateBranchFromRunSourceCommand.NotifyCanExecuteChanged();
        RestoreRunCommand.NotifyCanExecuteChanged();
        RestoreRunSourceCommand.NotifyCanExecuteChanged();
        ShowRunSourceHistoryCommand.NotifyCanExecuteChanged();
        DeleteRunCommand.NotifyCanExecuteChanged();
        MergeBranchCommand.NotifyCanExecuteChanged();
        CheckoutBranchCommand.NotifyCanExecuteChanged();
        ReconcileBranchCommand.NotifyCanExecuteChanged();
        RenameBranchCommand.NotifyCanExecuteChanged();
        DeleteBranchCommand.NotifyCanExecuteChanged();
        OpenCloudSyncCommand.NotifyCanExecuteChanged();
        ManageSafetySnapshotsCommand.NotifyCanExecuteChanged();
        ClearMissingCommand.NotifyCanExecuteChanged();
        ScanRecoverCommand.NotifyCanExecuteChanged();
    }

    private void CancelHistoryCommands()
    {
        ChangeSelectionCommand.Cancel();
        ChangeViewModeCommand.Cancel();
        CancelCurrentOperationCommands();
    }

    private void CancelCurrentOperationCommands()
    {
        RetryCommand.Cancel();
        EditVersionCommentCommand.Cancel();
        ToggleVersionImportantCommand.Cancel();
        CreateBranchFromVersionCommand.Cancel();
        UploadVersionCommand.Cancel();
        DownloadVersionCommand.Cancel();
        RestoreVersionCommand.Cancel();
        ExportVersionCommand.Cancel();
        DeleteVersionCommand.Cancel();
        EditRunCommentCommand.Cancel();
        ToggleRunImportantCommand.Cancel();
        CreateBranchFromRunSourceCommand.Cancel();
        RestoreRunCommand.Cancel();
        RestoreRunSourceCommand.Cancel();
        ShowRunSourceHistoryCommand.Cancel();
        DeleteRunCommand.Cancel();
        MergeBranchCommand.Cancel();
        CheckoutBranchCommand.Cancel();
        ReconcileBranchCommand.Cancel();
        RenameBranchCommand.Cancel();
        DeleteBranchCommand.Cancel();
        OpenCloudSyncCommand.Cancel();
        ManageSafetySnapshotsCommand.Cancel();
        ClearMissingCommand.Cancel();
        ScanRecoverCommand.Cancel();
    }

    private static string FormatBoundary(EffectiveSourceBoundarySnapshot boundary)
        => boundary.ScopeMode == EffectiveBoundaryScopeMode.All && boundary.FilterRules.IsEmpty
            ? I18n.GetString("Restore_ScopeAll")
            : I18n.Format("Restore_ScopeRules", boundary.ScopeRules.Length, boundary.FilterRules.Length);

    private void ShowInteractionWarning(string? diagnostic)
        => _interactions.Notify(
            HistoryNotificationKind.Warning,
            NormalizeDiagnostic(diagnostic));

    private static string NormalizeDiagnostic(string? diagnostic)
        => string.IsNullOrWhiteSpace(diagnostic)
            ? I18n.GetString("History_NativeAction_NotAvailable")
            : diagnostic;
}
