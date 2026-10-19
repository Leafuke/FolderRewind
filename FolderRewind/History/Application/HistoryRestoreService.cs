using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed partial class HistoryRestoreService
{
    internal Services.NativeHistoryConfigurationOperationGate.Lease? ExistingOperation { get; set; }
    private Services.NativeHistoryConfigurationOperationGate.Lease? _recoveredOperation;
    internal Func<Services.NativeHistoryConfigurationOperationGate.Lease, IReadOnlyList<HistoryRestoreSourceBinding>, CancellationToken, Task<HistoryWorkspace>>? ProtectCurrentWorkInsideOperation { get; set; }
    internal bool RequiresCurrentInput { get; init; }
    private readonly HistoryRuntime _history;
    private readonly RepresentationRuntime _representations;
    private readonly Func<CancellationToken, Task<IRepresentationEnvironment>> _environmentFactory;
    private readonly IHistoryRestoreMutationBackend _mutation;
    internal Func<HistoryCommitPack, CancellationToken, Task>? PackPublisher { get; init; }
    private readonly HistoryRestoreTransactionJournalStore _journals;
    private readonly Func<HistoryRestoreSourceBinding, string, CancellationToken, Task<bool>>? _prepareRestore;
    private readonly Func<CancellationToken, ValueTask<IAsyncDisposable>>? _finalGuard;
    internal Func<Services.NativeHistoryConfigurationOperationGate.Lease, CancellationToken, ValueTask<IAsyncDisposable>>? FinalGuardInsideOperation { get; init; }
    internal Func<SourceVersion, CancellationToken, Task>? ValidateVersionTarget { get; init; }
    internal ArchiveRestoreVerificationContext? VerificationContext { get; set; }
    internal bool UseTargetVolumeWorkspaces { get; init; }
    internal bool AllowDirectorySwap { get; init; } = true;
    internal bool HoldCurrentReadProtection { get; set; }
    internal Action? ValidateCurrentInputs { get; set; }
    internal IReadOnlySet<SourceId> UnchangedTargetSources { get; set; } = new HashSet<SourceId>();
    internal IReadOnlyList<string> ProtectedRestoreRoots { get; init; } = [];
    internal string? RestoreConfigurationSignature { get; init; }
    internal HistoryRestorePreparationSession? PreparationSession { get; set; }
    internal PreparedRestoreSource? ReusablePreparation { get; set; }
    internal Func<Services.NativeHistoryConfigurationOperationGate.Lease, CancellationToken, Task>? RecoverNativeInsideOperation { get; init; }

    public HistoryRestoreService(
        HistoryRuntime history,
        RepresentationRuntime representations,
        Func<CancellationToken, Task<IRepresentationEnvironment>> environmentFactory,
        IHistoryRestoreMutationBackend mutation,
        Func<HistoryRestoreSourceBinding, string, CancellationToken, Task<bool>>? prepareRestore = null,
        Func<CancellationToken, ValueTask<IAsyncDisposable>>? finalGuard = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _representations = representations ?? throw new ArgumentNullException(nameof(representations));
        _environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
        _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        _journals = new HistoryRestoreTransactionJournalStore(history, mutation);
        _prepareRestore = prepareRestore;
        _finalGuard = finalGuard;
    }

    public async Task RecoverIncompleteAsync(CancellationToken cancellationToken = default)
    {
        if (ExistingOperation is { } existing)
        {
            await RecoverInsideConfigurationAsync(existing, cancellationToken).ConfigureAwait(false);
            return;
        }
        await using (var operation = await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false))
            await RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        await _history.DrainRestoreCleanupAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RecoverInsideConfigurationAsync(Services.NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken cancellationToken = default)
    {
        operation.Require(_history.ConfigId);
        if (ReferenceEquals(_recoveredOperation, operation)) return;
        await _history.RecoverInsideOperationAsync(operation, _mutation, cancellationToken, RecoverNativeInsideOperation).ConfigureAwait(false);
        _recoveredOperation = operation;
    }

    public async Task<HistoryRestoreResult> RestoreCheckpointAsync(
        CheckpointId checkpointId, IReadOnlyList<HistoryRestoreSourceBinding> mappedSources,
        HistoryWorkspace expectedWorkspace, HistoryCheckpointRestoreScope scope,
        HistoryRestoreApplyMode requestedMode, CancellationToken cancellationToken = default)
    {
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await _history.Query.GetCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return Blocked("Checkpoint does not exist.");
        return await RestoreVersionsAsync(new Dictionary<SourceId, VersionId> { [checkpoint.SourceId] = checkpoint.VersionId },
            mappedSources, expectedWorkspace, requestedMode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Materialize all selections before one journaled mutation; preserve every Source's Branch state.</summary>
    public async Task<HistoryRestoreResult> RestoreVersionsAsync(
        IReadOnlyDictionary<SourceId, VersionId> selections, IReadOnlyList<HistoryRestoreSourceBinding> mappedSources,
        HistoryWorkspace expectedWorkspace, HistoryRestoreApplyMode requestedMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        using var inputs = EnterInputScope();
        using var diagnostics = Services.HistoryOperationDiagnostics.Current is null ? Services.HistoryOperationDiagnostics.Begin("restore-versions") : null;
        ArgumentNullException.ThrowIfNull(mappedSources);
        await using var ownedOperation = ExistingOperation is null
            ? await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false) : null;
        var operation = ExistingOperation ?? ownedOperation!;
        await RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (selections.Count == 0 || mappedSources.Count != selections.Count
            || mappedSources.Select(b => b.SourceId).Distinct().Count() != mappedSources.Count
            || !mappedSources.Select(b => b.SourceId).ToHashSet().SetEquals(selections.Keys)
            || mappedSources.Select(b => Path.GetFullPath(b.TargetDirectory)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mappedSources.Count)
            return Blocked("Restore selections require unique Sources and target directories.");
        var prepared = new List<PreparedRestoreSource>();
        string? preparationError = null;
        using var ownedPreparation = PreparationSession is null ? TryCreatePreparationSession(mappedSources, expectedWorkspace, out preparationError) : null;
        if (preparationError is not null) return Blocked(preparationError);
        var session = PreparationSession ?? ownedPreparation;
        using var currentInputs = RequiresCurrentInput ? EnterCurrentInputScope(mappedSources) : null;
        try
        {
            var chosen = new List<(SourceVersion Version, HistoryRestoreSourceBinding Binding, HistoryRestoreApplyMode Mode)>();
            foreach (var binding in mappedSources)
            {
                var version = await _history.Query.GetVersionAsync(selections[binding.SourceId], cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Selected SourceVersion is missing.");
                if (version.ConfigId != _history.ConfigId || version.SourceId != binding.SourceId)
                    throw new InvalidOperationException("Restore selection crosses Source identity.");
                var effectiveMode = version.CaptureScope == CaptureScope.PartialSource ? HistoryRestoreApplyMode.Overwrite : requestedMode;
                chosen.Add((version, binding, effectiveMode));
            }
            await ReservePreparationSpaceAsync(chosen.Select(s => (s.Version, s.Binding, RequiredFidelity(s.Mode))), session, cancellationToken).ConfigureAwait(false);
            foreach (var (version, binding, effectiveMode) in chosen)
            {
                prepared.Add(await PrepareSourceAsync(
                    version,
                    binding,
                    RequiredFidelity(effectiveMode),
                    effectiveMode,
                    cancellationToken, session).ConfigureAwait(false));
            }
            for (int i = 0; i < prepared.Count; i++)
                prepared[i] = await PrepareOrdinaryRestoreAsync(prepared[i], cancellationToken).ConfigureAwait(false);
            if (ProtectCurrentWorkInsideOperation is { } protect)
            {
                session?.ValidateBindings();
                expectedWorkspace = await protect(operation, prepared.Select(p => p.Binding).ToArray(), cancellationToken).ConfigureAwait(false);
            }
            session?.RebindExpected(expectedWorkspace);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }

        try
        {
            await using var guard = await EnterFinalGuardAsync(cancellationToken, operation).ConfigureAwait(false);
            await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var current = await RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            var restoredIds = prepared.Select(item => item.Binding.SourceId).ToHashSet();
            var baselines = current.SourceBaselines
                .Where(item => !restoredIds.Contains(item.SourceId))
                .Concat(prepared.Select(item => new WorkspaceSourceBaseline(
                    item.Binding.SourceId,
                    item.Version.VersionId,
                    item.Fidelity == MaterializationFidelity.Exact
                        && item.ApplyMode == HistoryRestoreApplyMode.Clean
                        ? WorkspaceBaselineRelation.Exact
                        : WorkspaceBaselineRelation.Derived) { ContentRealizationId = Guid.NewGuid() }));
            var desired = current.WithContentBaselines(baselines);
            return await ExecuteMutationAsync(prepared, current, desired, cancellationToken, preparationSession: session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Select(item => item.StagingDirectory));
            return Blocked(ex.Message);
        }
    }

    public async Task<HistoryRestoreResult> RestoreVersionAsync(
        VersionId versionId,
        HistoryRestoreSourceBinding source,
        HistoryWorkspace expectedWorkspace,
        HistoryRestoreApplyMode requestedMode,
        CancellationToken cancellationToken = default)
    {
        using var diagnostics = Services.HistoryOperationDiagnostics.Current is null ? Services.HistoryOperationDiagnostics.Begin("restore-version") : null;
        using var inputs = EnterInputScope();
        await using var ownedOperation = ExistingOperation is null
            ? await Services.NativeHistoryConfigurationOperationGate.EnterHistoryAsync(_history.ConfigId, cancellationToken).ConfigureAwait(false) : null;
        var operation = ExistingOperation ?? ownedOperation!;
        await RecoverInsideConfigurationAsync(operation, cancellationToken).ConfigureAwait(false);
        var version = await _history.Query.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (version is null || version.SourceId != source.SourceId)
            return Blocked("Version does not exist or belongs to another Source.");

        PreparedRestoreSource prepared;
        string? preparationError = null;
        using var ownedPreparation = PreparationSession is null ? TryCreatePreparationSession([source], expectedWorkspace, out preparationError) : null;
        if (preparationError is not null) return Blocked(preparationError);
        var session = PreparationSession ?? ownedPreparation;
        using var currentInputs = RequiresCurrentInput && ReusablePreparation is null ? EnterCurrentInputScope([source]) : null;
        try
        {
            var effectiveMode = version.CaptureScope == CaptureScope.PartialSource
                ? HistoryRestoreApplyMode.Overwrite : requestedMode;
            var boundSource = session?.BindSource(source) ?? source;
            if (ReusablePreparation is null) await ReservePreparationSpaceAsync([(version, source, RequiredFidelity(effectiveMode))], session, cancellationToken).ConfigureAwait(false);
            prepared = ReusablePreparation is { } reused && reused.Version.VersionId == versionId
                && reused.Binding.SourceId == source.SourceId && reused.ApplyMode == effectiveMode
                && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(reused.Binding.TargetDirectory), Path.GetFullPath(boundSource.TargetDirectory))
                ? reused : await PrepareSourceAsync(
                version,
                source,
                RequiredFidelity(effectiveMode),
                effectiveMode,
                cancellationToken, session).ConfigureAwait(false);
            prepared = await PrepareOrdinaryRestoreAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (ProtectCurrentWorkInsideOperation is { } protect && ReusablePreparation is null)
            {
                session?.ValidateBindings();
                expectedWorkspace = await protect(operation, [prepared.Binding], cancellationToken).ConfigureAwait(false);
            }
            session?.RebindExpected(expectedWorkspace);
        }
        catch (Exception ex)
        {
            return Blocked(ex.Message);
        }

        try
        {
            await using var guard = await EnterFinalGuardAsync(cancellationToken, operation).ConfigureAwait(false);
            await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var current = await RequireExpectedWorkspaceAsync(expectedWorkspace, cancellationToken).ConfigureAwait(false);
            if (ValidateVersionTarget is not null)
                await ValidateVersionTarget(version, cancellationToken).ConfigureAwait(false);
            var relation = prepared.Fidelity == MaterializationFidelity.Exact
                && prepared.ApplyMode == HistoryRestoreApplyMode.Clean
                ? WorkspaceBaselineRelation.Exact
                : WorkspaceBaselineRelation.Derived;
            var baselines = current.SourceBaselines
                .Where(item => item.SourceId != source.SourceId)
                .Append(new WorkspaceSourceBaseline(source.SourceId, versionId, relation) { ContentRealizationId = Guid.NewGuid() });
            var desired = current.WithContentBaselines(baselines);
            return await ExecuteMutationAsync(
                [prepared], current, desired, cancellationToken, preparationSession: session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging([prepared.StagingDirectory]);
            return Blocked(ex.Message);
        }
    }

    internal async Task<HistoryWorkingStateComparison> CompareReadOnlyAsync(SourceVersion version, HistoryRestoreSourceBinding binding, CancellationToken token)
    {
        using var evidence = await CompareReadOnlyWithEvidenceAsync(version, binding, token).ConfigureAwait(false);
        return evidence.Comparison;
    }

    internal async Task<HistoryWorkingStateEvidence> CompareReadOnlyWithEvidenceAsync(SourceVersion version, HistoryRestoreSourceBinding binding, CancellationToken token)
    {
        var graph = await _history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var environment = await _environmentFactory(token).ConfigureAwait(false);
        var staging = Path.Combine(Path.GetTempPath(), "FolderRewind-comparison", Guid.NewGuid().ToString("N"));
        try
        {
            using var inputs = await _representations.LockExactVersionAsync(version.VersionId, graph, environment, token, VerificationContext).ConfigureAwait(false);
            await _representations.MaterializeAsync(inputs.RepresentationId, graph, environment, MaterializationFidelity.Exact, staging, token, inputs.VerificationContext).ConfigureAwait(false);
            try { _ = File.GetAttributes(binding.TargetDirectory); }
            catch (FileNotFoundException) { return new(new(HistoryWorkingStateComparisonStatus.Different, "TargetMissing")); }
            catch (DirectoryNotFoundException) { return new(new(HistoryWorkingStateComparisonStatus.Different, "TargetMissing")); }
            var prepared = new PreparedRestoreSource(binding, version, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, staging);
            return await HistoryExactWorkingStateProbe.ComparePreparedWithEvidenceAsync(prepared, binding, token).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _history.ReportMaintenance("Comparison cleanup: " + ex.Message); }
        }
    }

    internal async Task<PreparedRestoreSource> PrepareSourceAsync(
        SourceVersion version,
        HistoryRestoreSourceBinding source,
        MaterializationFidelity requiredFidelity,
        HistoryRestoreApplyMode applyMode,
        CancellationToken cancellationToken,
        HistoryRestorePreparationSession? session = null)
    {
        using var phase = Services.HistoryOperationDiagnostics.Current?.Measure("prepare-source");
        source = session?.BindSource(source) ?? source;
        if (applyMode == HistoryRestoreApplyMode.Clean)
            LegacyRecoveryPolicy.RequireKnownBoundary(version);
        var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        var assessment = await _representations.AssessVersionAsync(
            version.VersionId,
            allRepresentations,
            environment,
            AssessmentDepth.Deep,
            requiredFidelity,
            cancellationToken, VerificationContext).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            throw new InvalidOperationException(HistoryQuickRestoreResolver.AssessmentDiagnostic(assessment,
                $"Version {version.VersionId} is not Ready with {requiredFidelity} fidelity."));

        var staging = session?.CreateStaging(source.SourceId) ?? Path.Combine(
            _history.Repository.Paths.TransactionsRoot,
            "restore-staging",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var legacyRead = version.BoundaryConfidence == HistoricalBoundaryConfidence.Unknown
                ? await _representations.LockVersionAsync(version.VersionId, allRepresentations, environment,
                    requiredFidelity, cancellationToken, VerificationContext).ConfigureAwait(false) : null;
            Services.HistoryOperationDiagnostics.Current?.Count("target-source-materializations");
            await _representations.MaterializeAsync(
                legacyRead?.RepresentationId ?? assessment.Selected.RepresentationId,
                allRepresentations,
                environment,
                requiredFidelity,
                staging,
                cancellationToken, legacyRead?.VerificationContext ?? VerificationContext).ConfigureAwait(false);
            session?.RecordPrepared(source.SourceId);
            return new PreparedRestoreSource(
                source with { EffectiveSourceBoundary = version.EffectiveSourceBoundary },
                version,
                assessment.Selected.Fidelity,
                applyMode,
                staging);
        }
        catch
        {
            HistoryRestoreTransactionJournalStore.CleanupStaging([staging]);
            throw;
        }
        finally { VerificationContext?.ReleaseMaterializationSpace(staging); }
    }

    internal async Task<PreparedRestoreSource> PrepareOrdinaryRestoreAsync(PreparedRestoreSource source, CancellationToken token)
    {
        if (source.UserPreparationApplied) return source;
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("restore-patch");
        bool changed = false;
        if (_prepareRestore is not null)
        {
            Services.HistoryOperationDiagnostics.Current?.Count("restore-patch-calls");
            changed = await _prepareRestore(source.Binding, source.StagingDirectory, token).ConfigureAwait(false);
        }
        return source with { Fidelity = changed ? MaterializationFidelity.Partial : source.Fidelity, UserPreparationApplied = true };
    }
    internal async ValueTask<IAsyncDisposable?> EnterFinalGuardAsync(CancellationToken token, Services.NativeHistoryConfigurationOperationGate.Lease? operation = null)
        => operation is not null && FinalGuardInsideOperation is not null ? await FinalGuardInsideOperation(operation, token).ConfigureAwait(false)
            : _finalGuard is null ? null : await _finalGuard(token).ConfigureAwait(false);

    internal async Task EnsureReadyAsync(
        VersionId versionId,
        MaterializationFidelity requiredFidelity,
        CancellationToken cancellationToken)
    {
        var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        var assessment = await _representations.AssessVersionAsync(
            versionId,
            allRepresentations,
            environment,
            AssessmentDepth.Deep,
            requiredFidelity,
            cancellationToken).ConfigureAwait(false);
        if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
            throw new InvalidOperationException($"Version {versionId} is not Ready with {requiredFidelity} fidelity.");
    }

    internal async Task<IDisposable> VerifyExactTreeAndLockAsync(SourceVersion version, string expectedDigest, CancellationToken token)
    {
        LegacyRecoveryPolicy.RequireKnownBoundary(version);
        var representations = await _history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var environment = await _environmentFactory(token).ConfigureAwait(false);
        var lease = await _representations.LockExactVersionAsync(version.VersionId, representations, environment, token, VerificationContext).ConfigureAwait(false);
        var staging = Path.Combine(_history.Repository.Paths.TransactionsRoot, "merge-verification", Guid.NewGuid().ToString("N"));
        try
        {
            await _representations.MaterializeAsync(lease.RepresentationId, representations, environment, MaterializationFidelity.Exact, staging, token, lease.VerificationContext).ConfigureAwait(false);
            var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(new(version.SourceId, staging, version.EffectiveSourceBoundary));
            if ((await Merge.MergeTreeManifest.ReadAsync(staging, include, token).ConfigureAwait(false)).Digest != expectedDigest)
                throw new IOException("Exact representation no longer matches the fixed Merge input.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
        finally { HistoryRestoreTransactionJournalStore.CleanupStaging([staging]); }
    }

    internal async Task<VersionAssessment> AssessVersionAsync(
        VersionId versionId,
        MaterializationFidelity requiredFidelity,
        AssessmentDepth depth,
        CancellationToken cancellationToken)
    {
        var allRepresentations = await _history.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        var environment = await _environmentFactory(cancellationToken).ConfigureAwait(false);
        return await _representations.AssessVersionAsync(
            versionId,
            allRepresentations,
            environment,
            depth,
            requiredFidelity,
            cancellationToken, VerificationContext).ConfigureAwait(false);
    }

    internal async Task<HistoryWorkspace> RequireExpectedWorkspaceAsync(
        HistoryWorkspace expected,
        CancellationToken cancellationToken)
    {
        var load = await _history.WorkspaceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Value is null || !HistoryRestoreTransactionJournalStore.WorkspaceEquals(load.Value, expected))
            throw new InvalidOperationException("Workspace changed before restore apply.");
        return load.Value;
    }

    internal async Task<HistoryRestoreResult> ExecuteMutationAsync(
        IReadOnlyList<PreparedRestoreSource> prepared,
        HistoryWorkspace currentWorkspace,
        HistoryWorkspace desiredWorkspace,
        CancellationToken cancellationToken,
        HistoryCommitPack? commitPack = null,
        LocalReplicaCatalog? desiredCatalog = null,
        long expectedCatalogRevision = -1,
        Action? beforeCommit = null,
        HistoryRestorePreparationSession? preparationSession = null)
    {
        using var phase = Services.HistoryOperationDiagnostics.Current?.Measure("mutation-commit-cleanup");
        foreach (var item in prepared)
        {
            if (item.ApplyMode == HistoryRestoreApplyMode.Clean)
                LegacyRecoveryPolicy.RequireKnownBoundary(item.Version);
            LegacyRecoveryPolicy.ValidateOverlay(item.Version, item.StagingDirectory, item.Binding.TargetDirectory);
        }
        if (preparationSession is not null)
        {
            if (commitPack is not null) throw new InvalidOperationException("Merge Apply retains its existing compatibility backend.");
            return await ExecuteTargetVolumeMutationAsync(preparationSession, prepared, currentWorkspace, desiredWorkspace, cancellationToken).ConfigureAwait(false);
        }
        var transactionId = commitPack?.TransactionId ?? HistoryTransactionId.New();
        var journal = new HistoryRestoreTransactionJournal(
            transactionId,
            HistoryRestoreTransactionPhase.Prepared,
            currentWorkspace,
            desiredWorkspace,
            prepared.Select(item => item.StagingDirectory).ToImmutableArray(),
            [],
            [], [], commitPack is null ? null : new HistoryPackCodec().Encode(commitPack), desiredCatalog, expectedCatalogRevision);
        _journals.Save(journal);
        var snapshots = new List<HistoryRestoreRollbackSnapshot>();
        var applied = new List<SourceId>();
        var readLocks = new List<FileStream>();
        void ReleaseReads() { foreach (var file in readLocks) file.Dispose(); readLocks.Clear(); }
        async Task VerifyOriginalAsync()
        {
            foreach (var item in prepared.Where(p => p.ExpectedOriginalTreeDigest is not null))
            {
                var snapshot = snapshots.Single(s => s.SourceId == item.Binding.SourceId);
                var tree = snapshot.HadOriginalTarget
                    ? await Merge.MergeTreeManifest.ReadAsync(snapshot.RollbackDirectory,
                        FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(item.Binding), cancellationToken).ConfigureAwait(false)
                    : Merge.MergeTreeManifest.Empty;
                if (tree.Digest != item.ExpectedOriginalTreeDigest) throw new IOException("Working files changed after protection and before mutation.");
            }
        }
        try
        {
            foreach (var item in prepared)
                snapshots.Add(_mutation.PlanRollback(item.Binding, transactionId));
            journal = journal with
            {
                Phase = HistoryRestoreTransactionPhase.Mutating,
                RollbackSnapshots = snapshots.ToImmutableArray()
            };
            _journals.Save(journal);
            foreach (var snapshot in snapshots)
            {
                journal = journal with { StartedSources = journal.StartedSources.Add(snapshot.SourceId) };
                _journals.Save(journal);
                await _mutation.PrepareRollbackAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in prepared.Where(p => p.ExpectedOriginalTreeDigest is not null))
            {
                var snapshot = snapshots.Single(s => s.SourceId == item.Binding.SourceId);
                if (!snapshot.HadOriginalTarget) continue;
                var directories = new Stack<string>(); directories.Push(snapshot.RollbackDirectory);
                while (directories.TryPop(out var directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Rollback tree contains a link.");
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Rollback tree contains a link.");
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                        else readLocks.Add(new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                }
            }
            await VerifyOriginalAsync().ConfigureAwait(false);
            for (int index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                await _mutation.ApplyAsync(
                    item.Binding,
                    item.StagingDirectory,
                    item.ApplyMode,
                    snapshots[index],
                    cancellationToken).ConfigureAwait(false);
                applied.Add(item.Binding.SourceId);
                journal = journal with { AppliedSources = applied.ToImmutableArray() };
                _journals.Save(journal);
            }

            await VerifyOriginalAsync().ConfigureAwait(false);
            foreach (var item in prepared.Where(p => p.ExpectedResultTreeDigest is not null))
            {
                var snapshot = snapshots.Single(s => s.SourceId == item.Binding.SourceId);
                var boundary = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(item.Binding);
                bool IncludeResult(string path) => (snapshot.NewTargetOwnershipMarker is null
                        || !StringComparer.Ordinal.Equals(path.Replace('\\', '/'), snapshot.NewTargetOwnershipMarker))
                    && boundary(path);
                if ((await Merge.MergeTreeManifest.ReadAsync(item.Binding.TargetDirectory,
                    IncludeResult, cancellationToken).ConfigureAwait(false)).Digest != item.ExpectedResultTreeDigest)
                    throw new IOException("Applied Merge state differs from the verified result.");
            }
            if (commitPack is not null)
            {
                beforeCommit?.Invoke();
                if (PackPublisher is { } publish) await publish(commitPack, cancellationToken).ConfigureAwait(false);
                else await _history.Repository.CommitAsync(commitPack, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            journal = journal with { Phase = HistoryRestoreTransactionPhase.WorkspaceApplying };
            _journals.Save(journal);
            await _journals.CompleteLocalStateAsync(journal, commitPack is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
            journal = journal with { Phase = HistoryRestoreTransactionPhase.WorkspaceApplied };
            _journals.Save(journal);
            ReleaseReads();
            return await CompleteCommittedAsync(journal, snapshots, applied).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReleaseReads();
            if (commitPack is not null)
            {
                try
                {
                    if (_journals.IsPackCommitted(journal))
                    {
                        await _journals.CompleteLocalStateAsync(journal, CancellationToken.None).ConfigureAwait(false);
                        return await CompleteCommittedAsync(journal, snapshots, applied).ConfigureAwait(false);
                    }
                }
                catch (Exception recovery)
                {
                    return new(HistoryRestoreStatus.CommittedRecoveryRequired,
                        $"Durable Merge requires local recovery: {recovery.Message}", false, applied.ToImmutableArray());
                }
            }
            var workspaceLoad = await _history.WorkspaceStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (workspaceLoad.Value is not null
                && HistoryRestoreTransactionJournalStore.WorkspaceEquals(workspaceLoad.Value, desiredWorkspace))
            {
                var committed = await CompleteCommittedAsync(journal, snapshots, applied).ConfigureAwait(false);
                return committed with
                {
                    Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
                    Diagnostic = string.IsNullOrWhiteSpace(committed.Diagnostic)
                        ? $"Restore committed; post-commit recovery handled: {ex.Message}"
                        : committed.Diagnostic
                };
            }

            bool rollbackFailed = false;
            foreach (var snapshot in snapshots.Where(s => journal.StartedSources.Contains(s.SourceId)).Reverse())
            {
                try { await _mutation.RollbackAsync(snapshot, CancellationToken.None).ConfigureAwait(false); }
                catch { rollbackFailed = true; }
            }
            HistoryRestoreTransactionJournalStore.CleanupStaging(journal.StagingDirectories);
            if (!rollbackFailed)
                _journals.Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
            return new(
                rollbackFailed
                    ? HistoryRestoreStatus.MutationFailedRecoveryRequired
                    : HistoryRestoreStatus.MutationFailedRolledBack,
                ex.Message,
                false,
                applied.ToImmutableArray());
        }
    }

    private async Task<HistoryRestoreResult> CompleteCommittedAsync(
        HistoryRestoreTransactionJournal journal,
        IReadOnlyList<HistoryRestoreRollbackSnapshot> snapshots,
        IReadOnlyList<SourceId> applied)
    {
        try
        {
            foreach (var snapshot in snapshots)
                await _mutation.CommitAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            HistoryRestoreTransactionJournalStore.CleanupStaging(journal.StagingDirectories);
            _journals.Save(journal with { Phase = HistoryRestoreTransactionPhase.Complete });
        }
        catch (Exception ex)
        {
            // The Workspace is already durable. Keep the journal incomplete so startup recovery
            // can retry idempotent snapshot cleanup without reverting committed Source data.
            HistoryRestoreTransactionJournalStore.CleanupStaging(journal.StagingDirectories);
            return new(
                HistoryRestoreStatus.CommittedWithPostActionWarning,
                $"Restore committed; rollback snapshot cleanup is deferred: {ex.Message}",
                true,
                applied.ToImmutableArray());
        }

        try
        {
            await _history.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            await _history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.LocalStateChanged);
            return new(HistoryRestoreStatus.Committed, string.Empty, true, applied.ToImmutableArray());
        }
        catch (Exception ex)
        {
            return new(
                HistoryRestoreStatus.CommittedWithPostActionWarning,
                $"Restore committed; local health refresh is deferred: {ex.Message}",
                true,
                applied.ToImmutableArray());
        }
    }

    private static HistoryRestoreResult Blocked(string diagnostic)
        => new(HistoryRestoreStatus.BlockedBeforeMutation, diagnostic, false, []);

    private static MaterializationFidelity RequiredFidelity(HistoryRestoreApplyMode applyMode)
        => applyMode == HistoryRestoreApplyMode.Clean
            ? MaterializationFidelity.Exact
            : MaterializationFidelity.Partial;

    internal sealed record PreparedRestoreSource(
        HistoryRestoreSourceBinding Binding,
        SourceVersion Version,
        MaterializationFidelity Fidelity,
        HistoryRestoreApplyMode ApplyMode,
        string StagingDirectory, string? ExpectedOriginalTreeDigest = null, string? ExpectedResultTreeDigest = null,
        bool UserPreparationApplied = false);
}
