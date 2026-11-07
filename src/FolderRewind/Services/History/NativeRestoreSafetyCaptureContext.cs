using FolderRewind.History.Application;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class RestoreCoordinationChangedException(string? detail = null)
    : IOException(I18n.GetString("OperationDiagnostic_RestoreCoordinationChanged")
        + (string.IsNullOrWhiteSpace(detail) ? "" : Environment.NewLine + detail));

/// <summary>A safety capture borrows the validated restore, never its protocol completion or channel admission.</summary>
internal sealed class NativeRestoreSafetyCaptureContext : IDisposable
{
    private static readonly AsyncLocal<NativeRestoreSafetyCaptureContext?> Ambient = new();
    private readonly NativeRestoreSafetyCaptureContext? _previous;
    private readonly NativeHistoryRestoreOrchestrator.Operation _restore;
    private readonly NativeHistoryConfigurationOperationGate.Lease _operation;
    private readonly HashSet<Guid> _sources;
    private bool _disposed;
    internal static NativeRestoreSafetyCaptureContext? Current => Ambient.Value;

    private NativeRestoreSafetyCaptureContext(NativeHistoryRestoreOrchestrator.Operation restore,
        NativeHistoryConfigurationOperationGate.Lease operation, IEnumerable<Guid> sources)
    {
        _restore = restore; _operation = operation; _sources = sources.ToHashSet();
        _previous = Ambient.Value; Ambient.Value = this;
    }

    internal static NativeRestoreSafetyCaptureContext? TryEnter(BackupConfig config, IReadOnlyList<ManagedFolder> folders,
        BackupInvocationOptions options, HistorySafetySnapshotIntent? intent, NativeHistoryConfigurationOperationGate.Lease? operation)
    {
        if (intent is null || options.Source != BackupInvocationSource.Internal || operation is null
            || NativeHistoryRestoreOrchestrator.Current is not { MutationSafetyCheck: not null } restore) return null;
        operation.Require(new(config.Id));
        if (!restore.ContinuationActive || restore.ConfigId != config.Id || folders.Count == 0
            || folders.Any(f => !restore.SourcePaths.TryGetValue(Guid.Parse(f.Id), out var path)
                || !StringComparer.OrdinalIgnoreCase.Equals(path, Path.GetFullPath(f.Path))))
            throw new RestoreCoordinationChangedException();
        return new(restore, operation, folders.Select(f => Guid.Parse(f.Id)));
    }

    internal async Task ValidateAsync(CancellationToken token, string? configId = null, Guid? source = null)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(NativeHistoryRestoreOrchestrator.Current, _restore)
            || !_restore.ContinuationActive || _restore.MutationSafetyCheck is null
            || configId is not null && configId != _restore.ConfigId || source is { } id && !_sources.Contains(id))
            throw new RestoreCoordinationChangedException();
        _operation.Require(new(_restore.ConfigId));
        await NativeHistoryRestoreOrchestrator.RevalidateMutationSafetyAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Ambient.Value = _previous;
    }
}
