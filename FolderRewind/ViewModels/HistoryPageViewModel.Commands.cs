using CommunityToolkit.Mvvm.Input;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
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
        DeleteVersionCommand = new AsyncRelayCommand<NativeHistoryVersionViewItem>(DeleteVersionCommandAsync, CanExecuteItemOperation);

        EditRunCommentCommand = new AsyncRelayCommand<BackupRunViewItem>(EditRunCommentCommandAsync, CanExecuteItemOperation);
        ToggleRunImportantCommand = new AsyncRelayCommand<BackupRunViewItem>(ToggleRunImportantCommandAsync, CanExecuteItemOperation);
        CreateBranchFromRunCommand = new AsyncRelayCommand<BackupRunViewItem>(CreateBranchFromRunCommandAsync, CanExecuteItemOperation);
        RestoreRunCommand = new AsyncRelayCommand<BackupRunViewItem>(RestoreRunCommandAsync, CanExecuteItemOperation);
        DeleteRunCommand = new AsyncRelayCommand<BackupRunViewItem>(DeleteRunCommandAsync, CanExecuteItemOperation);

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
    public IAsyncRelayCommand<NativeHistoryVersionViewItem> DeleteVersionCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> EditRunCommentCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> ToggleRunImportantCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> CreateBranchFromRunCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> RestoreRunCommand { get; }
    public IAsyncRelayCommand<BackupRunViewItem> DeleteRunCommand { get; }
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

    private Task CreateBranchFromRunCommandAsync(
        BackupRunViewItem? item,
        CancellationToken cancellationToken)
        => item?.ResultCheckpointId is not { } checkpointId
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
                    missing.SourceId),
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
                mismatch.SourceId,
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
                                .Select(tip => new HistoryChoiceOption(tip.UpdateId.ToString(), tip.UpdateId.ToString()))
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

        var mode = await ChooseRestoreModeAsync(
            item.IsPartialBackup,
            isRun: false,
            I18n.Format("History_RestoreConfirm_Content", item.TimeDisplay, item.Comment),
            cancellationToken);
        if (mode is null)
        {
            return;
        }

        var result = await NativeHistoryApplicationService.RestoreVersionAsync(
            config,
            folder,
            item.VersionId,
            mode.Value);
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

        var mode = await ChooseRestoreModeAsync(
            item.HasPartialBackup,
            isRun: true,
            I18n.GetString("History_Run_RestoreContent"),
            cancellationToken);
        if (mode is null)
        {
            return;
        }

        if (item.ResultCheckpointId is not { } checkpointId)
        {
            return;
        }
        var result = await NativeHistoryApplicationService.RestoreCheckpointAsync(
            config,
            checkpointId,
            completeCheckpoint: !item.HasPartialBackup,
            mode.Value);
        if (result?.Succeeded != true)
        {
            _interactions.NotifyRestoreCompleted(config.Name, false, NormalizeDiagnostic(result?.Diagnostic));
            return;
        }

        var succeeded = result.AppliedSources.Count;
        var failed = Math.Max(0, item.Sources.Count - succeeded);
        _interactions.NotifyRestoreCompleted(
            config.Name,
            failed == 0,
            failed == 0 ? null : I18n.Format("History_Run_RestoreSummary", succeeded, failed));
    }

    private async Task<BackupService.RestoreMode?> ChooseRestoreModeAsync(
        bool partial,
        bool isRun,
        string message,
        CancellationToken cancellationToken)
    {
        var result = await _interactions.ChooseAsync(
            new HistoryChoiceRequest(
                partial
                    ? I18n.GetString("History_PartialRestore_Title")
                    : I18n.GetString(isRun
                        ? "History_Run_RestoreTitle"
                        : "History_RestoreConfirm_Title"),
                partial ? I18n.GetString("History_PartialRestore_Content") : message,
                [],
                partial
                    ? I18n.GetString("History_PartialRestore_Primary")
                    : I18n.GetString("History_RestoreConfirm_Primary"),
                partial ? null : I18n.GetString("History_RestoreConfirm_Secondary")),
            cancellationToken);
        return result.Outcome switch
        {
            HistoryInteractionOutcome.Primary => partial
                ? BackupService.RestoreMode.Overwrite
                : BackupService.RestoreMode.Clean,
            HistoryInteractionOutcome.Secondary => BackupService.RestoreMode.Overwrite,
            _ => null
        };
    }

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
                I18n.GetString("Common_Ok"),
                InitialValue: item.HasLocalFile && string.IsNullOrWhiteSpace(localDeletionBlocker)
                    ? ((int)BackupDeleteMode.LocalArchiveOnly).ToString(CultureInfo.InvariantCulture)
                    : ((int)BackupDeleteMode.RecordOnly).ToString(CultureInfo.InvariantCulture),
                IsDestructive: true),
            cancellationToken);
        if (choice.Outcome != HistoryInteractionOutcome.Primary
            || !int.TryParse(choice.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawMode)
            || !Enum.IsDefined(typeof(BackupDeleteMode), rawMode))
        {
            return;
        }

        var result = await DeleteVersionAsync(item, (BackupDeleteMode)rawMode);
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
        RetryCommand.NotifyCanExecuteChanged();
        EditVersionCommentCommand.NotifyCanExecuteChanged();
        ToggleVersionImportantCommand.NotifyCanExecuteChanged();
        CreateBranchFromVersionCommand.NotifyCanExecuteChanged();
        UploadVersionCommand.NotifyCanExecuteChanged();
        DownloadVersionCommand.NotifyCanExecuteChanged();
        RestoreVersionCommand.NotifyCanExecuteChanged();
        DeleteVersionCommand.NotifyCanExecuteChanged();
        EditRunCommentCommand.NotifyCanExecuteChanged();
        ToggleRunImportantCommand.NotifyCanExecuteChanged();
        CreateBranchFromRunCommand.NotifyCanExecuteChanged();
        RestoreRunCommand.NotifyCanExecuteChanged();
        DeleteRunCommand.NotifyCanExecuteChanged();
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
        DeleteVersionCommand.Cancel();
        EditRunCommentCommand.Cancel();
        ToggleRunImportantCommand.Cancel();
        CreateBranchFromRunCommand.Cancel();
        RestoreRunCommand.Cancel();
        DeleteRunCommand.Cancel();
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
        => $"Scope={boundary.ScopeMode} [{string.Join(", ", boundary.ScopeRules)}]; "
           + $"Filter={boundary.FilterMode} [{string.Join(", ", boundary.FilterRules)}]; "
           + $"Regex={boundary.UseRegex}; Fingerprint={boundary.Fingerprint}";

    private void ShowInteractionWarning(string? diagnostic)
        => _interactions.Notify(
            HistoryNotificationKind.Warning,
            NormalizeDiagnostic(diagnostic));

    private static string NormalizeDiagnostic(string? diagnostic)
        => string.IsNullOrWhiteSpace(diagnostic)
            ? I18n.GetString("History_NativeAction_NotAvailable")
            : diagnostic;
}
