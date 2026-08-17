using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public enum PluginUninstallTransactionPhase
{
    Prepared = 0,
    Quarantined = 1,
    ConfigCommitted = 2,
    Completed = 3,
    RecoveryRequired = 4
}

public enum PluginUninstallRecoveryAction
{
    None = 0,
    Rollback = 1,
    CompleteCleanup = 2
}

public enum PluginUninstallFaultPoint
{
    AfterPrepared = 0,
    AfterCodeQuarantined = 1,
    AfterDataQuarantined = 2,
    AfterConfigurationCommit = 3,
    AfterConfigCommitted = 4,
    BeforeFinalCleanup = 5
}

public sealed record PluginUninstallTransactionRequest(
    PluginId PluginId,
    string CodePath,
    string DataPath,
    JsonElement RollbackSnapshot);

public sealed record PluginUninstallTransactionJournal(
    int SchemaVersion,
    string TransactionId,
    PluginId PluginId,
    PluginUninstallTransactionPhase Phase,
    PluginUninstallRecoveryAction RecoveryAction,
    string CodePath,
    string DataPath,
    string CodeQuarantinePath,
    string DataQuarantinePath,
    JsonElement RollbackSnapshot,
    DateTimeOffset UpdatedAtUtc,
    string Diagnostic = "",
    bool WasRolledBack = false);

public sealed record PluginUninstallTransactionResult(
    OperationOutcome Outcome,
    PluginUninstallTransactionPhase Phase,
    string RecoveryPath,
    string Diagnostic = "");

public sealed record PluginUninstallTransactionCallbacks(
    Func<PluginUninstallTransactionJournal, CancellationToken, ValueTask> CommitConfigurationAsync,
    Func<PluginUninstallTransactionJournal, CancellationToken, ValueTask> RestoreConfigurationAsync);

public sealed class PluginUninstallTransactionException(
    string message,
    Exception innerException,
    bool recoveryRequired,
    string recoveryPath)
    : IOException(message, innerException)
{
    public bool RecoveryRequired { get; } = recoveryRequired;
    public string RecoveryPath { get; } = recoveryPath;
}

/// <summary>Test-only fault signal that models process termination without in-process rollback.</summary>
public sealed class PluginUninstallSimulatedCrashException(string message) : Exception(message);

public sealed class PluginUninstallTransactionCoordinator
{
    private const string JournalFileName = "journal.v1.json";
    private readonly string _transactionsRoot;
    private readonly PluginUninstallTransactionCallbacks _callbacks;
    private readonly Func<PluginUninstallFaultPoint, CancellationToken, ValueTask>? _faultInjector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PluginUninstallTransactionCoordinator(
        string transactionsRoot,
        PluginUninstallTransactionCallbacks callbacks,
        Func<PluginUninstallFaultPoint, CancellationToken, ValueTask>? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        _transactionsRoot = Path.GetFullPath(transactionsRoot);
        _callbacks = callbacks;
        _faultInjector = faultInjector;
        Directory.CreateDirectory(_transactionsRoot);
    }

    public async ValueTask<PluginUninstallTransactionResult> ExecuteAsync(
        PluginUninstallTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = CreateJournal(request);
            var journalPath = GetJournalPath(journal.TransactionId);
            await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            try
            {
                await InjectAsync(PluginUninstallFaultPoint.AfterPrepared, cancellationToken).ConfigureAwait(false);
                MoveToQuarantine(journal.CodePath, journal.CodeQuarantinePath);
                await InjectAsync(PluginUninstallFaultPoint.AfterCodeQuarantined, cancellationToken).ConfigureAwait(false);
                MoveToQuarantine(journal.DataPath, journal.DataQuarantinePath);
                await InjectAsync(PluginUninstallFaultPoint.AfterDataQuarantined, cancellationToken).ConfigureAwait(false);

                journal = journal with
                {
                    Phase = PluginUninstallTransactionPhase.Quarantined,
                    RecoveryAction = PluginUninstallRecoveryAction.Rollback,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

                await _callbacks.CommitConfigurationAsync(journal, cancellationToken).ConfigureAwait(false);
                await InjectAsync(PluginUninstallFaultPoint.AfterConfigurationCommit, cancellationToken).ConfigureAwait(false);
                journal = journal with
                {
                    Phase = PluginUninstallTransactionPhase.ConfigCommitted,
                    RecoveryAction = PluginUninstallRecoveryAction.CompleteCleanup,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
                await InjectAsync(PluginUninstallFaultPoint.AfterConfigCommitted, cancellationToken).ConfigureAwait(false);
                return await CompleteCleanupAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            }
            catch (PluginUninstallSimulatedCrashException)
            {
                throw;
            }
            catch (Exception ex) when (journal.Phase < PluginUninstallTransactionPhase.ConfigCommitted)
            {
                return await RollBackOrThrowAsync(journalPath, journal, ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await MarkCleanupRecoveryRequiredAsync(journalPath, journal, ex).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<PluginUninstallTransactionResult>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<PluginUninstallTransactionResult>();
            foreach (var journalPath in Directory.EnumerateFiles(
                         _transactionsRoot,
                         JournalFileName,
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var journal = await ReadJournalAsync(journalPath, cancellationToken).ConfigureAwait(false);
                if (journal.Phase == PluginUninstallTransactionPhase.Completed) continue;
                if (journal.Phase == PluginUninstallTransactionPhase.ConfigCommitted
                    || journal.RecoveryAction == PluginUninstallRecoveryAction.CompleteCleanup)
                {
                    results.Add(await CompleteCleanupAsync(journalPath, journal, cancellationToken).ConfigureAwait(false));
                    continue;
                }

                try
                {
                    await RollBackAsync(journal, cancellationToken).ConfigureAwait(false);
                    var completed = journal with
                    {
                        Phase = PluginUninstallTransactionPhase.Completed,
                        RecoveryAction = PluginUninstallRecoveryAction.None,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Diagnostic = string.Empty,
                        WasRolledBack = true
                    };
                    await WriteJournalAsync(journalPath, completed, cancellationToken).ConfigureAwait(false);
                    results.Add(new PluginUninstallTransactionResult(
                        OperationOutcome.SuccessWithWarnings,
                        completed.Phase,
                        Path.GetDirectoryName(journalPath)!,
                        "An interrupted uninstall was rolled back."));
                }
                catch (Exception ex)
                {
                    await MarkRecoveryRequiredAsync(
                        journalPath,
                        journal,
                        PluginUninstallRecoveryAction.Rollback,
                        ex,
                        cancellationToken).ConfigureAwait(false);
                    results.Add(new PluginUninstallTransactionResult(
                        OperationOutcome.Failed,
                        PluginUninstallTransactionPhase.RecoveryRequired,
                        Path.GetDirectoryName(journalPath)!,
                        ex.Message));
                }
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private PluginUninstallTransactionJournal CreateJournal(PluginUninstallTransactionRequest request)
    {
        var codePath = Path.GetFullPath(request.CodePath);
        var dataPath = Path.GetFullPath(request.DataPath);
        EnsureSameVolume(codePath, dataPath, _transactionsRoot);
        if (!StringComparer.Ordinal.Equals(Path.GetFileName(codePath), request.PluginId.Value)
            || !StringComparer.Ordinal.Equals(Path.GetFileName(dataPath), request.PluginId.Value))
            throw new ArgumentException("Plugin code and data paths must end with the exact PluginId.", nameof(request));
        if (PathEquals(codePath, dataPath)
            || IsWithin(codePath, dataPath)
            || IsWithin(dataPath, codePath)
            || IsWithin(_transactionsRoot, codePath)
            || IsWithin(_transactionsRoot, dataPath)
            || IsWithin(codePath, _transactionsRoot)
            || IsWithin(dataPath, _transactionsRoot))
            throw new ArgumentException("Plugin uninstall paths must be separate, non-nested directories.", nameof(request));

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionRoot = Path.Combine(_transactionsRoot, transactionId);
        return new PluginUninstallTransactionJournal(
            1,
            transactionId,
            request.PluginId,
            PluginUninstallTransactionPhase.Prepared,
            PluginUninstallRecoveryAction.Rollback,
            codePath,
            dataPath,
            Path.Combine(transactionRoot, "quarantine", "code"),
            Path.Combine(transactionRoot, "quarantine", "data"),
            request.RollbackSnapshot.Clone(),
            DateTimeOffset.UtcNow);
    }

    private async ValueTask<PluginUninstallTransactionResult> CompleteCleanupAsync(
        string journalPath,
        PluginUninstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        try
        {
            await InjectAsync(PluginUninstallFaultPoint.BeforeFinalCleanup, cancellationToken).ConfigureAwait(false);
            DeleteQuarantine(journal.CodeQuarantinePath);
            DeleteQuarantine(journal.DataQuarantinePath);
            var completed = journal with
            {
                Phase = PluginUninstallTransactionPhase.Completed,
                RecoveryAction = PluginUninstallRecoveryAction.None,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Diagnostic = string.Empty
            };
            await WriteJournalAsync(journalPath, completed, cancellationToken).ConfigureAwait(false);
            return new PluginUninstallTransactionResult(
                OperationOutcome.Success,
                completed.Phase,
                Path.GetDirectoryName(journalPath)!);
        }
        catch (Exception ex)
        {
            return await MarkCleanupRecoveryRequiredAsync(journalPath, journal, ex).ConfigureAwait(false);
        }
    }

    private async ValueTask<PluginUninstallTransactionResult> MarkCleanupRecoveryRequiredAsync(
        string journalPath,
        PluginUninstallTransactionJournal journal,
        Exception exception)
    {
        await MarkRecoveryRequiredAsync(
            journalPath,
            journal,
            PluginUninstallRecoveryAction.CompleteCleanup,
            exception,
            CancellationToken.None).ConfigureAwait(false);
        return new PluginUninstallTransactionResult(
            OperationOutcome.SuccessWithWarnings,
            PluginUninstallTransactionPhase.RecoveryRequired,
            Path.GetDirectoryName(journalPath)!,
            exception.Message);
    }

    private async ValueTask<PluginUninstallTransactionResult> RollBackOrThrowAsync(
        string journalPath,
        PluginUninstallTransactionJournal journal,
        Exception originalException)
    {
        try
        {
            await RollBackAsync(journal, CancellationToken.None).ConfigureAwait(false);
            var completed = journal with
            {
                Phase = PluginUninstallTransactionPhase.Completed,
                RecoveryAction = PluginUninstallRecoveryAction.None,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Diagnostic = originalException.Message,
                WasRolledBack = true
            };
            await WriteJournalAsync(journalPath, completed, CancellationToken.None).ConfigureAwait(false);
            throw new PluginUninstallTransactionException(
                "Plugin uninstall failed and was rolled back.",
                originalException,
                recoveryRequired: false,
                Path.GetDirectoryName(journalPath)!);
        }
        catch (PluginUninstallTransactionException)
        {
            throw;
        }
        catch (Exception rollbackException)
        {
            await MarkRecoveryRequiredAsync(
                journalPath,
                journal,
                PluginUninstallRecoveryAction.Rollback,
                rollbackException,
                CancellationToken.None).ConfigureAwait(false);
            throw new PluginUninstallTransactionException(
                "Plugin uninstall failed and automatic rollback also failed.",
                new AggregateException(originalException, rollbackException),
                recoveryRequired: true,
                Path.GetDirectoryName(journalPath)!);
        }
    }

    private async ValueTask RollBackAsync(
        PluginUninstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        RestoreFromQuarantine(journal.CodeQuarantinePath, journal.CodePath);
        RestoreFromQuarantine(journal.DataQuarantinePath, journal.DataPath);
        await _callbacks.RestoreConfigurationAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask MarkRecoveryRequiredAsync(
        string journalPath,
        PluginUninstallTransactionJournal journal,
        PluginUninstallRecoveryAction action,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await WriteJournalAsync(journalPath, journal with
        {
            Phase = PluginUninstallTransactionPhase.RecoveryRequired,
            RecoveryAction = action,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Diagnostic = exception.Message
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InjectAsync(
        PluginUninstallFaultPoint point,
        CancellationToken cancellationToken)
    {
        if (_faultInjector is not null)
            await _faultInjector(point, cancellationToken).ConfigureAwait(false);
    }

    private static void MoveToQuarantine(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        if (Directory.Exists(destination))
            throw new IOException($"Uninstall quarantine already exists: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }

    private static void RestoreFromQuarantine(string quarantine, string destination)
    {
        if (!Directory.Exists(quarantine)) return;
        if (Directory.Exists(destination))
            throw new IOException($"Cannot restore uninstall quarantine because the target exists: {destination}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(quarantine, destination);
    }

    private static void DeleteQuarantine(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void EnsureSameVolume(params string[] paths)
    {
        var roots = paths.Select(path => Path.GetPathRoot(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length != 1)
            throw new IOException("Plugin uninstall quarantine requires code, data, and journal paths on the same volume.");
    }

    private static bool PathEquals(string left, string right)
        => StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right));

    private static bool IsWithin(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return normalizedCandidate.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private string GetJournalPath(string transactionId)
        => Path.Combine(_transactionsRoot, transactionId, JournalFileName);

    private async ValueTask<PluginUninstallTransactionJournal> ReadJournalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PluginUninstallTransactionJournal>(
                   stream,
                   _json,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Plugin uninstall journal is empty.");
    }

    private async ValueTask WriteJournalAsync(
        string path,
        PluginUninstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }
}
