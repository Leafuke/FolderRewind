using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum HistoryInteractionOutcome
{
    Cancelled,
    Primary,
    Secondary
}

internal enum HistoryNotificationKind
{
    Info,
    Success,
    Warning,
    Error
}

internal sealed record HistoryChoiceOption(string Value, string DisplayName);

internal sealed record HistoryChoiceRequest(
    string Title,
    string Message,
    IReadOnlyList<HistoryChoiceOption> Options,
    string PrimaryButtonText,
    string? SecondaryButtonText = null,
    string? InitialValue = null,
    bool IsDestructive = false);

internal sealed record HistoryInteractionResult(
    HistoryInteractionOutcome Outcome,
    string? Value = null);

internal interface IHistoryInteractionService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        bool isDestructive = false,
        CancellationToken cancellationToken = default);

    Task<string?> RequestTextAsync(
        string title,
        string message,
        string initialValue = "",
        bool isPassword = false,
        CancellationToken cancellationToken = default);

    Task<HistoryInteractionResult> ChooseAsync(
        HistoryChoiceRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);

    Task OpenCloudSyncAsync(string configId, CancellationToken cancellationToken = default);

    void Notify(HistoryNotificationKind kind, string message);

    void NotifyRestoreCompleted(string targetName, bool success, string? detail = null);
}

internal sealed class HistoryInteractionController(IHistoryInteractionService interactionService)
{
    public async Task<bool> ConfirmAndExecuteAsync(
        string title,
        string message,
        string primaryButtonText,
        Func<CancellationToken, Task<bool>> operation,
        string failureMessage,
        bool isDestructive = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await interactionService.ConfirmAsync(
                title,
                message,
                primaryButtonText,
                isDestructive,
                cancellationToken))
        {
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (await operation(cancellationToken))
            {
                return true;
            }

            interactionService.Notify(HistoryNotificationKind.Error, failureMessage);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            interactionService.Notify(
                HistoryNotificationKind.Error,
                string.IsNullOrWhiteSpace(ex.Message) ? failureMessage : ex.Message);
            return false;
        }
    }

    public async Task<bool> RequestTextAndExecuteAsync(
        string title,
        string message,
        string initialValue,
        Func<string, CancellationToken, Task<bool>> operation,
        string failureMessage,
        CancellationToken cancellationToken = default,
        bool allowEmpty = true)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await interactionService.RequestTextAsync(
            title,
            message,
            initialValue,
            cancellationToken: cancellationToken);
        if (value is null)
        {
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            if (await operation(value, cancellationToken))
            {
                return true;
            }

            interactionService.Notify(HistoryNotificationKind.Error, failureMessage);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            interactionService.Notify(
                HistoryNotificationKind.Error,
                string.IsNullOrWhiteSpace(ex.Message) ? failureMessage : ex.Message);
            return false;
        }
    }
}
