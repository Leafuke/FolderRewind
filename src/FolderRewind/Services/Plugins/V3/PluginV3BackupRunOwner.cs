using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Operations;
using FolderRewind.Services.KnotLink;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>Owns the provider runtime lease through terminal dispatch, outside the configuration lock.</summary>
internal sealed class PluginV3BackupRunOwner : IAsyncDisposable
{
    private static readonly AsyncLocal<PluginV3BackupRunOwner?> Ambient = new();
    private static int _activeOwners;
    internal static bool HasActiveRuns => Volatile.Read(ref _activeOwners) != 0;
    internal static PluginV3BackupRunOwner? Current => Ambient.Value;
    private readonly PluginCapabilityLease<IBackupRunCoordinationCapability> _lease;
    private readonly Dictionary<Guid, BackupCoordinationSource> _sources;
    private readonly Dictionary<Guid, (string Path, string Boundary)> _logicalBindings;
    private readonly CancellationTokenSource _lifetimeCapture;
    private readonly HashSet<Guid> _sealedSources = [];
    private readonly object _sourceState = new();
    private CoordinationSignalGate.Admission? _channel;
    private bool _completed;
    private bool _sessionDisposed;
    private int _disposed;
    internal Guid OperationId { get; } = Guid.NewGuid();
    internal Guid SignalOwner { get; }
    internal string ConfigId { get; }
    internal IBackupRunCoordinationSession Session { get; }
    internal CancellationToken CaptureCancellation => _lifetimeCapture.Token;
    internal CancellationToken ChannelInvalidation => _channel?.Invalidated ?? CancellationToken.None;
    internal string CancellationMessageKey => Session.CaptureCancellation.IsCancellationRequested
        ? "OperationDiagnostic_CoordinationWindowExpired" : "OperationDiagnostic_CoordinationDisconnected";
    internal IReadOnlyList<PluginDiagnostic> Diagnostics => _channel?.Invalidated.IsCancellationRequested == true
        ? Session.Diagnostics.Append(new PluginDiagnostic("minerewind.coordination_disconnected", DiagnosticSeverity.Warning,
            "BackupConsistency", _lease.Context.PluginId.Value, new Dictionary<string, string>())).ToArray() : Session.Diagnostics;

    private PluginV3BackupRunOwner(string configId, PluginCapabilityLease<IBackupRunCoordinationCapability> lease,
        IBackupRunCoordinationSession session, Dictionary<Guid, BackupCoordinationSource> sources,
        Dictionary<Guid, (string Path, string Boundary)> logicalBindings, Guid id)
    {
        ConfigId = configId; _lease = lease; Session = session; _sources = sources; OperationId = id;
        SignalOwner = KnotLinkCoordinationChannel.Owner ?? id;
        _logicalBindings = logicalBindings;
        _lifetimeCapture = CancellationTokenSource.CreateLinkedTokenSource(lease.Context.PluginLifetime, CancellationToken.None);
        Interlocked.Increment(ref _activeOwners);
    }

    internal static async Task<PluginV3BackupRunOwner?> PrepareAsync(BackupConfig config,
        IReadOnlyList<ManagedFolder> folders, BackupInvocationOptions options, CancellationToken token)
    {
        if (folders.Count == 0) return null;
        var kind = PluginV3ModelMapper.ToKind(config);
        var lease = PluginV3RuntimeService.Runtime.TryAcquire<IBackupRunCoordinationCapability>(new(kind.OwnerId.Value), c => c.Kind == kind, token);
        if (lease is null) return null;
        try
        {
            var sources = new Dictionary<Guid, BackupCoordinationSource>();
            var logicalBindings = new Dictionary<Guid, (string, string)>();
            foreach (var folder in folders)
            {
                var resolution = await PluginV3BackupSourceResolver.ResolveAsync(config, folder, token).ConfigureAwait(false);
                var id = new SourceId(Guid.Parse(folder.Id));
                var physical = options.PhysicalSourceBindings?.GetValueOrDefault(id);
                var snapshot = resolution.FolderSnapshot ?? PluginV3ModelMapper.ToSnapshot(config.Id, folder);
                if (physical is not null) snapshot = snapshot with { Path = physical.TargetDirectory };
                var binding = physical ?? new HistoryRestoreSourceBinding(id, snapshot.Path, resolution.EffectiveBoundary);
                sources.Add(snapshot.FolderId, new(snapshot, new Boundary(binding)) { LogicalPath = folder.Path });
                logicalBindings.Add(snapshot.FolderId, (folder.Path, resolution.EffectiveBoundary.Fingerprint));
            }
            var request = new BackupRunCoordinationRequest(Guid.NewGuid(), PluginV3ModelMapper.ToSnapshot(config), sources.Values.ToArray(),
                config.ConsistencyIntent == PersistedConsistencyIntent.Require ? ConsistencyIntent.Require : ConsistencyIntent.Prefer);
            using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
            var session = await lease.Capability.CreateSessionAsync(request, lease.Context).ConfigureAwait(false);
            return new(config.Id, lease, session, sources, logicalBindings, request.OperationId);
        }
        catch { lease.Dispose(); throw; }
    }

    internal async Task AdmitAsync(CancellationToken token, bool configurationAlreadyHeld = false)
    {
        if (Session.RequiresExclusiveKnotLinkSession)
        {
            if (configurationAlreadyHeld)
            {
                token.ThrowIfCancellationRequested();
                _channel = KnotLinkCoordinationChannel.Gate.TryBorrow(SignalOwner)
                    ?? throw new RestoreCoordinationChangedException();
                return;
            }
            try { _channel = await KnotLinkCoordinationChannel.Gate.EnterAsync(SignalOwner, token).ConfigureAwait(false); }
            // The world may have exited while waiting. Re-observe under the configuration lock;
            // the provider still rejects an online Require capture when the channel is unavailable.
            catch (InvalidOperationException) when (KnotLinkCoordinationChannel.Gate.NeedsReset) { return; }
        }
    }

    internal IDisposable EnterScope() => new Scope(this);
    internal IEnumerable<PluginDiagnostic> DiagnosticsForSource(Guid source) => Diagnostics.Where(d =>
        !d.Arguments.TryGetValue("folderId", out var id) || id.Equals(source.ToString("D"), StringComparison.OrdinalIgnoreCase));
    internal void ReportSourceReadCompleted(Guid source)
    {
        lock (_sourceState) _sealedSources.Add(source);
        using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
        Session.ReportSourceReadCompleted(source);
    }
    internal bool IsSourceReadCompleted(Guid source) { lock (_sourceState) return _sealedSources.Contains(source); }

    internal async ValueTask<IConsistencyLease> AcquireAsync(PluginV3BackupSourceResolution resolution, CancellationToken token)
    {
        if (resolution.ConfigSnapshot is null || resolution.FolderSnapshot is null || !_sources.TryGetValue(resolution.FolderSnapshot.FolderId, out var source))
            throw new InvalidOperationException("Backup Source does not belong to this run.");
        var expected = _logicalBindings[source.Folder.FolderId];
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(resolution.FolderSnapshot.Path), Path.GetFullPath(expected.Path))
            || resolution.EffectiveBoundary.Fingerprint != expected.Boundary) throw new BackupCoordinationAdmissionRequiredException();
        lock (_sourceState) _sealedSources.Remove(source.Folder.FolderId);
        using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lease.Context.PluginLifetime);
        var lease = await Session.AcquireSourceAsync(new(resolution.ConfigSnapshot, source.Folder, resolution.Intent)
            { CoordinationOnly = true, RunChannelAdmitted = KnotLinkCoordinationChannel.Gate.IsOwnedBy(SignalOwner) },
            _lease.Context with { OperationCancellation = lifetime.Token }).ConfigureAwait(false);
        if (lease.IsStableSourceView || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(lease.SourcePath), Path.GetFullPath(source.Folder.Path)))
        { await lease.DisposeAsync(); throw new InvalidDataException("Run coordinator replaced its Source."); }
        return lease;
    }

    internal async Task<bool> CompleteAsync(BackupService.BackupTransactionExecutionResult result)
    {
        if (_completed) return true;
        _completed = true;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var dispatch = BackupTerminalDispatch.NotRequired;
        var finalOutcome = result.ToPluginResult().Outcome;
        if (SignalOwner == OperationId)
        {
            var success = result.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings or OperationOutcome.NoChanges
                && !result.HistoryRecoveryRequired;
            var fields = new BackupOperationResult(finalOutcome, result.CreatedNewArchive, result.HistoryRecoveryRequired).ToProtocolFields();
            fields["config"] = ConfigId; fields["operation_id"] = OperationId.ToString("D");
            var queued = await KnotLinkService.TryBroadcastEventAsync(null, success ? "backup_success" : "backup_failed", fields, cleanup.Token).ConfigureAwait(false);
            if (_channel?.OwnsChannel == true)
                dispatch = queued && await KnotLinkCoordinationChannel.Gate.FlushOwnerAsync(SignalOwner, cleanup.Token).ConfigureAwait(false)
                    ? BackupTerminalDispatch.Submitted : BackupTerminalDispatch.Failed;
        }
        var released = true;
        try
        {
            using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
            var captured = Diagnostics.Select(OperationDiagnosticFormatter.Identity).ToHashSet(StringComparer.Ordinal);
            var diagnostics = await Session.CompleteAsync(new(finalOutcome,
                _sources.Keys.Select(id =>
                {
                    var source = result.SourceOutcomes.SingleOrDefault(o => o.FolderId == id);
                    var outcome = source?.OperationOutcome ?? finalOutcome;
                    if (result.CommittedBatch is null && !result.HistoryRecoveryRequired
                        && finalOutcome is OperationOutcome.Failed or OperationOutcome.Canceled or OperationOutcome.Blocked) outcome = finalOutcome;
                    return new BackupSourceCoordinationCompletion(id, outcome, result.CreatedNewArchive && source?.CreatedNewArchive == true);
                }).ToArray(), dispatch), cleanup.Token);
            OperationDiagnosticFormatter.Log(diagnostics, "Backup run coordination " + OperationId, "Backup");
            result.CompletionDiagnostics = OperationDiagnosticFormatter.Snapshot(diagnostics.Where(d => !captured.Contains(OperationDiagnosticFormatter.Identity(d))));
            await Session.DisposeAsync().ConfigureAwait(false);
            _sessionDisposed = true;
        }
        finally
        {
            if (_channel is not null) released = await _channel.CompleteAsync(cleanup.Token).ConfigureAwait(false);
        }
        return dispatch != BackupTerminalDispatch.Failed && released;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_sessionDisposed)
            {
                using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
                await Session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (_channel?.OwnsChannel == true) KnotLinkCoordinationChannel.Gate.MarkUncertain();
            LogService.LogWarning(error.ToString(), "Backup coordination disposal");
        }
        finally
        {
            try { if (_channel is not null) await _channel.DisposeAsync().ConfigureAwait(false); }
            finally { _lifetimeCapture.Dispose(); _lease.Dispose(); Interlocked.Decrement(ref _activeOwners); }
        }
    }

    private sealed class Boundary(HistoryRestoreSourceBinding binding) : IBackupSourceBoundaryView
    {
        private readonly HistoryRestoreTreeScope _scope = new(binding);
        public bool ContainsFile(string relativePath) => relativePath != "." && _scope.Includes(relativePath.Replace('\\', '/'));
        public bool MayIntersectDirectory(string relativePath) => relativePath == "." || _scope.MayDescend(relativePath.Replace('\\', '/'));
    }

    private sealed class Scope : IDisposable
    {
        private readonly PluginV3BackupRunOwner? _previous = Ambient.Value;
        private readonly IDisposable _signals;
        internal Scope(PluginV3BackupRunOwner owner) { Ambient.Value = owner; _signals = KnotLinkCoordinationChannel.EnterScope(owner.SignalOwner); }
        public void Dispose() { _signals.Dispose(); Ambient.Value = _previous; }
    }
}
