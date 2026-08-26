using System.Collections.Concurrent;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

public sealed class PluginRuntimeManager
{
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly CapabilityRegistry _registry = new();
    private readonly ConcurrentDictionary<PluginId, PluginRuntimeSession> _sessions = new();
    private readonly ConcurrentDictionary<PluginId, (PluginRuntimeState State, string Error, bool RequiresRestart)> _status = new();
    private readonly ConcurrentQueue<RestartRetainedPlugin> _restartRetained = new();
    private readonly TimeSpan _deactivationGracePeriod;

    public PluginRuntimeManager(
        bool safeMode = false,
        TimeSpan? deactivationGracePeriod = null)
    {
        var gracePeriod = deactivationGracePeriod ?? TimeSpan.FromSeconds(5);
        if (gracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(deactivationGracePeriod));
        IsSafeMode = safeMode;
        _deactivationGracePeriod = gracePeriod;
    }

    public bool IsSafeMode { get; }
    public int RestartRetainedCount => _restartRetained.Count;

    public PluginRuntimeSnapshot GetSnapshot(PluginId pluginId)
    {
        if (_sessions.TryGetValue(pluginId, out var session))
        {
            var sessionStatus = GetStatus(pluginId);
            return new PluginRuntimeSnapshot(
                pluginId,
                session.State,
                session.ActiveLeases,
                sessionStatus.Error,
                sessionStatus.RequiresRestart);
        }

        return _status.TryGetValue(pluginId, out var status)
            ? new PluginRuntimeSnapshot(pluginId, status.State, 0, status.Error, status.RequiresRestart)
            : new PluginRuntimeSnapshot(pluginId, PluginRuntimeState.Inactive, 0, string.Empty);
    }

    public PluginCapabilityLease<TCapability>? TryAcquire<TCapability>(
        PluginId pluginId,
        CancellationToken operationCancellation = default)
        where TCapability : class, IPluginCapability
        => _sessions.TryGetValue(pluginId, out var session)
            ? session.TryAcquire<TCapability>(operationCancellation)
            : null;

    public PluginCapabilityLease<TCapability>? TryAcquire<TCapability>(
        PluginId pluginId,
        Func<TCapability, bool> selector,
        CancellationToken operationCancellation = default)
        where TCapability : class, IPluginCapability
    {
        ArgumentNullException.ThrowIfNull(selector);
        return _sessions.TryGetValue(pluginId, out var session)
            ? session.TryAcquire(selector, operationCancellation)
            : null;
    }

    /// <summary>
    /// Runtime 完成逻辑隔离不代表 collectible ALC 已物理释放；若 Host 检测到程序集仍被引用，
    /// 必须禁止同一 PluginId 在当前进程重新激活，避免旧实例与新实例并存。
    /// </summary>
    public void MarkPhysicalUnloadFailed(PluginId pluginId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _status.AddOrUpdate(
            pluginId,
            _ =>
            {
                var state = _sessions.TryGetValue(pluginId, out var session)
                    ? session.State
                    : PluginRuntimeState.Inactive;
                return (state, message, true);
            },
            (_, current) =>
            {
                var combined = string.IsNullOrWhiteSpace(current.Error)
                    ? message
                    : current.Error.Contains(message, StringComparison.Ordinal)
                        ? current.Error
                        : $"{current.Error} {message}";
                return (current.State, combined, true);
            });
    }

    public async ValueTask<PluginRuntimeTransitionResult> ActivateAsync(
        PluginActivationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (IsSafeMode)
        {
            return PluginRuntimeTransitionResult.Rejected(
                OperationOutcome.Blocked,
                PluginRuntimeState.Inactive,
                RuntimeDiagnostic.Error("runtime.safe_mode", candidate.PluginId, "Safe Mode blocks all plugin loading."));
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.TryGetValue(candidate.PluginId, out var priorStatus) && priorStatus.RequiresRestart)
            {
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Blocked,
                    priorStatus.State,
                    RuntimeDiagnostic.Error(
                        "runtime.restart_required",
                        candidate.PluginId,
                        "The retained plugin instance requires a Host restart before activation.")) with
                {
                    RequiresRestart = true
                };
            }
            if (_sessions.ContainsKey(candidate.PluginId))
            {
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Blocked,
                    GetSnapshot(candidate.PluginId).State,
                    RuntimeDiagnostic.Error("runtime.already_active", candidate.PluginId, "The plugin already has an active runtime session."));
            }

            _status[candidate.PluginId] = (PluginRuntimeState.Activating, string.Empty, false);
            StagedPlugin? staged = null;
            try
            {
                staged = await StageAsync(candidate, replacing: null, cancellationToken).ConfigureAwait(false);
                await candidate.Store.CommitAsync(staged.Commit, cancellationToken).ConfigureAwait(false);
                _registry.Commit(staged.Registrations);
                var session = staged.CreateSession();
                if (!_sessions.TryAdd(candidate.PluginId, session))
                {
                    throw new InvalidOperationException("A runtime session appeared during activation commit.");
                }
                _status[candidate.PluginId] = (PluginRuntimeState.Active, string.Empty, false);
                return PluginRuntimeTransitionResult.Completed(PluginRuntimeState.Active);
            }
            catch (Exception ex)
            {
                // StageAsync 报告 PluginStageFailureException 时已拥有 cleanup；只有取得 staged
                // candidate 后发生的外层失败，才由本次转换负责清理。
                var stageFailure = ex as PluginStageFailureException;
                var primary = stageFailure?.PrimaryFailure ?? ex;
                var cleanup = stageFailure?.Cleanup
                    ?? (staged is null
                        ? PluginCleanupResult.Completed
                        : await CleanupAsync(staged.Plugin, staged.Lifetime).ConfigureAwait(false));
                var canceled = primary is OperationCanceledException && cancellationToken.IsCancellationRequested;
                var state = cleanup.RequiresRestart
                    ? PluginRuntimeState.Failed
                    : canceled ? PluginRuntimeState.Inactive : PluginRuntimeState.Failed;
                var message = CombineFailureMessage(primary, cleanup);
                _status[candidate.PluginId] = (state, message, cleanup.RequiresRestart);
                return CreateFailureTransition(
                    candidate.PluginId,
                    canceled ? OperationOutcome.Canceled : OperationOutcome.Failed,
                    state,
                    primary,
                    cleanup,
                    "runtime.activation_canceled",
                    "runtime.activation_failed",
                    "Activation was canceled before commit.");
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask<PluginRuntimeTransitionResult> ReplaceAsync(
        PluginActivationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (IsSafeMode)
        {
            return PluginRuntimeTransitionResult.Rejected(
                OperationOutcome.Blocked,
                PluginRuntimeState.Inactive,
                RuntimeDiagnostic.Error("runtime.safe_mode", candidate.PluginId, "Safe Mode blocks all plugin loading."));
        }

        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.TryGetValue(candidate.PluginId, out var priorStatus) && priorStatus.RequiresRestart)
            {
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Blocked,
                    priorStatus.State,
                    RuntimeDiagnostic.Error(
                        "runtime.restart_required",
                        candidate.PluginId,
                        "The retained plugin instance requires a Host restart before replacement.")) with
                {
                    RequiresRestart = true
                };
            }
            if (!_sessions.TryGetValue(candidate.PluginId, out var oldSession))
            {
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Blocked,
                    PluginRuntimeState.Inactive,
                    RuntimeDiagnostic.Error("runtime.not_active", candidate.PluginId, "There is no active session to replace."));
            }

            StagedPlugin? staged = null;
            try
            {
                var drained = oldSession.BeginDrain();
                try
                {
                    await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    oldSession.ResumeActive();
                    return PluginRuntimeTransitionResult.Rejected(
                        OperationOutcome.Canceled,
                        PluginRuntimeState.Active,
                        RuntimeDiagnostic.Warning("runtime.drain_canceled", candidate.PluginId, "The existing runtime session remains active."));
                }

                staged = await StageAsync(candidate, candidate.PluginId, cancellationToken).ConfigureAwait(false);
                await candidate.Store.CommitAsync(staged.Commit, cancellationToken).ConfigureAwait(false);

                _registry.Commit(staged.Registrations);
                _sessions[candidate.PluginId] = staged.CreateSession();
                _status[candidate.PluginId] = (PluginRuntimeState.Active, string.Empty, false);
            }
            catch (Exception ex)
            {
                // 提交前失败时，候选实例仍由本次转换负责清理，旧会话恢复为 Active。
                var stageFailure = ex as PluginStageFailureException;
                var primary = stageFailure?.PrimaryFailure ?? ex;
                var cleanup = stageFailure?.Cleanup
                    ?? (staged is null
                        ? PluginCleanupResult.Completed
                        : await CleanupAsync(staged.Plugin, staged.Lifetime).ConfigureAwait(false));
                oldSession.ResumeActive();

                var canceled = primary is OperationCanceledException && cancellationToken.IsCancellationRequested;
                var message = CombineFailureMessage(primary, cleanup);
                _status[candidate.PluginId] = (PluginRuntimeState.Active, message, cleanup.RequiresRestart);
                return CreateFailureTransition(
                    candidate.PluginId,
                    canceled ? OperationOutcome.Canceled : OperationOutcome.Failed,
                    PluginRuntimeState.Active,
                    primary,
                    cleanup,
                    "runtime.replace_canceled",
                    "runtime.replace_failed",
                    "The existing runtime session remains active.");
            }

            try
            {
                oldSession.BeginDeactivation();
            }
            catch (Exception ex)
            {
                // 新实例已经提交，绝不能再清理它；保留无法安全停用的旧实例直到 Host 重启。
                BeginCancellation(oldSession.Lifetime);
                oldSession.MarkFailed();
                _restartRetained.Enqueue(new RestartRetainedPlugin(
                    oldSession.Plugin,
                    oldSession.Lifetime,
                    null,
                    null,
                    ex));
                var failedCleanup = new PluginCleanupResult(PluginCleanupOutcome.Failed, ex);
                _status[candidate.PluginId] = (PluginRuntimeState.Active, ex.Message, true);
                return new PluginRuntimeTransitionResult(
                    true,
                    OperationOutcome.SuccessWithWarnings,
                    PluginRuntimeState.Active,
                    CreateCleanupDiagnostics(candidate.PluginId, failedCleanup, "runtime.previous_deactivation"),
                    RequiresRestart: true);
            }

            var previousCleanup = await CleanupAsync(oldSession.Plugin, oldSession.Lifetime).ConfigureAwait(false);
            if (previousCleanup.IsUnsafe)
            {
                var message = previousCleanup.Outcome == PluginCleanupOutcome.TimedOut
                    ? "The previous plugin instance exceeded the deactivation grace period."
                    : CombineFailureMessage(
                        previousCleanup.Exception ?? new InvalidOperationException("The previous plugin cleanup failed."),
                        previousCleanup);
                _status[candidate.PluginId] = (PluginRuntimeState.Active, message, true);
                return new PluginRuntimeTransitionResult(
                    true,
                    OperationOutcome.SuccessWithWarnings,
                    PluginRuntimeState.Active,
                    CreateCleanupDiagnostics(candidate.PluginId, previousCleanup, "runtime.previous_deactivation"),
                    RequiresRestart: true);
            }
            return PluginRuntimeTransitionResult.Completed(PluginRuntimeState.Active);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async ValueTask<PluginRuntimeTransitionResult> DeactivateAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(pluginId, out var session))
            {
                if (_status.TryGetValue(pluginId, out var retained) && retained.RequiresRestart)
                {
                    return new PluginRuntimeTransitionResult(
                        true,
                        OperationOutcome.SuccessWithWarnings,
                        retained.State,
                        [RuntimeDiagnostic.Warning(
                            "runtime.restart_required",
                            pluginId,
                            "The plugin is logically inactive but its retained instance requires a Host restart.")],
                        RequiresRestart: true);
                }
                _status[pluginId] = (PluginRuntimeState.Inactive, string.Empty, false);
                return PluginRuntimeTransitionResult.Completed(PluginRuntimeState.Inactive);
            }

            var drained = session.BeginDrain();
            try
            {
                await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                session.ResumeActive();
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Canceled,
                    PluginRuntimeState.Active,
                    RuntimeDiagnostic.Warning("runtime.drain_canceled", pluginId, "The runtime session remains active."));
            }

            session.BeginDeactivation();
            _registry.Remove(pluginId);
            _sessions.TryRemove(pluginId, out _);
            var alreadyRequiresRestart = _status.TryGetValue(pluginId, out var priorStatus)
                && priorStatus.RequiresRestart;
            var cleanup = await CleanupAsync(session.Plugin, session.Lifetime).ConfigureAwait(false);
            if (cleanup.IsUnsafe || alreadyRequiresRestart)
            {
                session.MarkFailed();
                var message = cleanup.IsUnsafe
                    ? CombineFailureMessage(
                        cleanup.Exception ?? new InvalidOperationException("Plugin deactivation exceeded the deactivation grace period."),
                        cleanup)
                    : "The plugin is logically inactive but a previously retained instance still requires a Host restart.";
                _status[pluginId] = (PluginRuntimeState.Failed, message, true);
                var diagnostics = cleanup.IsUnsafe
                    ? CreateCleanupDiagnostics(pluginId, cleanup, "runtime.deactivation")
                    : [RuntimeDiagnostic.Warning("runtime.restart_required", pluginId, message)];
                if (cleanup.IsUnsafe && alreadyRequiresRestart)
                {
                    diagnostics = diagnostics
                        .Append(RuntimeDiagnostic.Warning(
                            "runtime.restart_required",
                            pluginId,
                            "A previously retained plugin instance also requires a Host restart."))
                        .ToArray();
                }
                return new PluginRuntimeTransitionResult(
                    true,
                    OperationOutcome.SuccessWithWarnings,
                    PluginRuntimeState.Failed,
                    diagnostics,
                    RequiresRestart: true);
            }
            _status[pluginId] = (PluginRuntimeState.Inactive, string.Empty, false);
            return PluginRuntimeTransitionResult.Completed(PluginRuntimeState.Inactive);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async ValueTask<StagedPlugin> StageAsync(
        PluginActivationCandidate candidate,
        PluginId? replacing,
        CancellationToken cancellationToken)
    {
        ValidateCandidate(candidate);
        if (candidate.Manifest is not null)
        {
            candidate = candidate with
            {
                HostServices = new DeclaredPluginHostServices(
                    candidate.HostServices,
                    candidate.Manifest.RequestedHostServices)
            };
        }
        var settings = CloneSettings(candidate.Settings);
        var configs = candidate.Configs.Select(CloneConfig).ToArray();
        var plugin = candidate.Factory() ?? throw new InvalidOperationException("Plugin factory returned null.");
        var lifetime = new CancellationTokenSource();
        var context = new TemporaryActivationContext(candidate.PluginId, settings, configs);
        try
        {
            var activationResult = await plugin.ActivateAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Plugin activation returned null.");
            var registrations = CapabilityRegistrationSet.Create(candidate.PluginId, context.Capabilities);
            if (candidate.Manifest is not null)
            {
                PluginManifestContractValidator.ValidateRuntime(candidate.Manifest, registrations);
            }
            _registry.Validate(registrations, replacing);
            var patches = await StageProviderStatePatchesAsync(
                candidate,
                configs,
                registrations,
                activationResult.Patch,
                lifetime.Token,
                cancellationToken).ConfigureAwait(false);
            return new StagedPlugin(
                candidate,
                plugin,
                registrations,
                lifetime,
                new PluginActivationCommit(candidate.PluginId, settings, patches));
        }
        catch (Exception ex)
        {
            var cleanup = await CleanupAsync(plugin, lifetime).ConfigureAwait(false);
            // 激活失败与 cleanup 失败是两个独立事实；同时保留，避免丢失主因或重启门禁。
            throw new PluginStageFailureException(ex, cleanup);
        }
    }

    private static async ValueTask<IReadOnlyList<ProviderStatePatch>> StageProviderStatePatchesAsync(
        PluginActivationCandidate candidate,
        IReadOnlyList<ConfigSnapshot> configs,
        CapabilityRegistrationSet registrations,
        PluginActivationPatch activationPatch,
        CancellationToken pluginLifetime,
        CancellationToken cancellationToken)
    {
        var snapshots = FlattenStates(configs).ToDictionary(StateKey);
        var patches = new Dictionary<(ProviderStateLocation, StateOwnerId), ProviderStatePatch>();
        foreach (var patch in activationPatch.ProviderStatePatches ?? throw new InvalidOperationException("Activation state patches cannot be null."))
        {
            AddValidatedPatch(patch, snapshots, patches);
        }

        var invocation = new PluginInvocationContext(
            candidate.PluginId,
            new ActivationHostServices(candidate.HostServices),
            cancellationToken,
            pluginLifetime);
        foreach (var migrator in registrations.Capabilities.OfType<IProviderStateMigrationCapability>())
        {
            foreach (var state in snapshots.Values.Where(state => state.StateOwnerId == migrator.StateOwnerId))
            {
                if (state.SchemaVersion > migrator.CurrentSchemaVersion)
                {
                    throw new InvalidOperationException(
                        $"Provider state {StateKey(state)} is newer than plugin schema {migrator.CurrentSchemaVersion}.");
                }

                if (state.SchemaVersion == migrator.CurrentSchemaVersion)
                {
                    continue;
                }

                var patch = await migrator.MigrateAsync(state, invocation).ConfigureAwait(false);
                if (patch.SchemaVersion != migrator.CurrentSchemaVersion)
                {
                    throw new InvalidOperationException("Provider state migrator did not return its declared current schema version.");
                }
                AddValidatedPatch(patch, snapshots, patches);
            }
        }

        return patches.Values.ToArray();
    }

    private static void AddValidatedPatch(
        ProviderStatePatch patch,
        IReadOnlyDictionary<(ProviderStateLocation, StateOwnerId), ProviderStateSnapshot> snapshots,
        IDictionary<(ProviderStateLocation, StateOwnerId), ProviderStatePatch> patches)
    {
        var key = (patch.Location, patch.StateOwnerId);
        if (!snapshots.TryGetValue(key, out var current))
        {
            throw new InvalidOperationException($"Provider state patch targets unknown state {key}.");
        }
        if (patch.ExpectedSchemaVersion != current.SchemaVersion)
        {
            throw new InvalidOperationException($"Provider state patch expected schema {patch.ExpectedSchemaVersion}, but current is {current.SchemaVersion}.");
        }
        if (patch.SchemaVersion < patch.ExpectedSchemaVersion || patch.Data.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Provider state patch has an invalid schema version or data payload.");
        }
        if (patches.ContainsKey(key))
        {
            throw new InvalidOperationException($"More than one provider state patch targets {key}.");
        }
        patches.Add(key, patch with { Data = patch.Data.Clone() });
    }

    private static IEnumerable<ProviderStateSnapshot> FlattenStates(IEnumerable<ConfigSnapshot> configs)
    {
        foreach (var config in configs)
        {
            foreach (var state in config.ProviderStates.Values) yield return state;
            foreach (var folder in config.Folders)
            {
                foreach (var state in folder.ProviderStates.Values) yield return state;
            }
        }
    }

    private static (ProviderStateLocation, StateOwnerId) StateKey(ProviderStateSnapshot state)
        => (state.Location, state.StateOwnerId);

    private static void ValidateCandidate(PluginActivationCandidate candidate)
    {
        if (candidate.Settings.PluginId != candidate.PluginId)
        {
            throw new InvalidOperationException("Settings snapshot belongs to a different plugin.");
        }
        ArgumentNullException.ThrowIfNull(candidate.Factory);
        ArgumentNullException.ThrowIfNull(candidate.Configs);
        ArgumentNullException.ThrowIfNull(candidate.HostServices);
        ArgumentNullException.ThrowIfNull(candidate.Store);
        if (candidate.Manifest is not null)
        {
            PluginManifestContractValidator.ValidateStatic(candidate.Manifest, candidate.PluginId);
        }
    }

    private static PluginSettingsSnapshot CloneSettings(PluginSettingsSnapshot settings)
        => new(settings.PluginId, settings.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));

    private static ConfigSnapshot CloneConfig(ConfigSnapshot config)
    {
        if (string.IsNullOrWhiteSpace(config.ConfigId))
        {
            throw new InvalidOperationException("Config snapshots require an identity.");
        }

        return new ConfigSnapshot(
            config.ConfigId,
            config.Revision,
            config.Kind,
            config.Name,
            config.Folders.Select(folder =>
            {
                if (folder.FolderId == Guid.Empty)
                {
                    throw new InvalidOperationException("Folder snapshots require a non-empty GUID.");
                }
                return new FolderSnapshot(
                    folder.FolderId,
                    folder.Path,
                    folder.DisplayName,
                    CloneStates(folder.ProviderStates, new ProviderStateLocation(config.ConfigId, folder.FolderId)));
            }).ToArray(),
            CloneStates(config.ProviderStates, new ProviderStateLocation(config.ConfigId, null)));
    }

    private static IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> CloneStates(
        IReadOnlyDictionary<StateOwnerId, ProviderStateSnapshot> states,
        ProviderStateLocation expectedLocation)
    {
        var clone = new Dictionary<StateOwnerId, ProviderStateSnapshot>();
        foreach (var (owner, state) in states)
        {
            if (owner != state.StateOwnerId || state.Location != expectedLocation)
            {
                throw new InvalidOperationException("Provider state identity or location does not match its snapshot container.");
            }
            if (state.SchemaVersion < 0 || state.Data.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("Provider state snapshot has an invalid schema version or payload.");
            }
            clone.Add(owner, CloneState(state));
        }
        return clone;
    }

    private static ProviderStateSnapshot CloneState(ProviderStateSnapshot state)
        => state with { Data = state.Data.Clone() };

    private PluginRuntimeTransitionResult CreateFailureTransition(
        PluginId pluginId,
        OperationOutcome outcome,
        PluginRuntimeState state,
        Exception primaryFailure,
        PluginCleanupResult cleanup,
        string canceledCode,
        string failedCode,
        string canceledMessage)
    {
        var diagnostics = new List<PluginDiagnostic>
        {
            outcome == OperationOutcome.Canceled
                ? RuntimeDiagnostic.Warning(canceledCode, pluginId, canceledMessage)
                : RuntimeDiagnostic.Error(failedCode, pluginId, primaryFailure.Message)
        };
        if (cleanup.IsUnsafe)
        {
            diagnostics.AddRange(CreateCleanupDiagnostics(pluginId, cleanup, "runtime.candidate_cleanup"));
        }

        return new PluginRuntimeTransitionResult(
            false,
            outcome,
            state,
            diagnostics,
            RequiresRestart: cleanup.RequiresRestart);
    }

    private static IReadOnlyList<PluginDiagnostic> CreateCleanupDiagnostics(
        PluginId pluginId,
        PluginCleanupResult cleanup,
        string codePrefix)
    {
        var message = cleanup.Outcome == PluginCleanupOutcome.TimedOut
            ? "Plugin cleanup exceeded the deactivation grace period."
            : cleanup.Exception?.Message ?? "Plugin cleanup failed.";
        var code = cleanup.Outcome == PluginCleanupOutcome.TimedOut
            ? $"{codePrefix}_timeout"
            : $"{codePrefix}_failed";
        return [RuntimeDiagnostic.Warning(code, pluginId, message)];
    }

    private static string CombineFailureMessage(Exception primaryFailure, PluginCleanupResult cleanup)
    {
        if (!cleanup.IsUnsafe)
        {
            return primaryFailure.Message;
        }

        if (ReferenceEquals(primaryFailure, cleanup.Exception))
        {
            return primaryFailure.Message;
        }

        var cleanupMessage = cleanup.Outcome == PluginCleanupOutcome.TimedOut
            ? "Plugin cleanup exceeded the deactivation grace period."
            : cleanup.Exception?.Message ?? "Plugin cleanup failed.";
        return $"{primaryFailure.Message} Cleanup: {cleanupMessage}";
    }

    private async ValueTask<PluginCleanupResult> CleanupAsync(
        IFolderRewindPlugin plugin,
        CancellationTokenSource lifetime)
    {
        BeginCancellation(lifetime);
        var cleanupCancellation = new CancellationTokenSource();
        Task cleanupTask;
        try
        {
            cleanupTask = plugin.DeactivateAsync(cleanupCancellation.Token).AsTask();
        }
        catch (Exception ex)
        {
            cleanupCancellation.Dispose();
            _restartRetained.Enqueue(new RestartRetainedPlugin(plugin, lifetime, null, null, ex));
            return new PluginCleanupResult(PluginCleanupOutcome.Failed, ex);
        }

        if (await Task.WhenAny(cleanupTask, Task.Delay(_deactivationGracePeriod)).ConfigureAwait(false) != cleanupTask)
        {
            BeginCancellation(cleanupCancellation);
            _ = cleanupTask.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _restartRetained.Enqueue(new RestartRetainedPlugin(plugin, lifetime, cleanupCancellation, cleanupTask, null));
            return new PluginCleanupResult(PluginCleanupOutcome.TimedOut, null);
        }

        var retainLifetime = false;
        try
        {
            await cleanupTask.ConfigureAwait(false);
            return PluginCleanupResult.Completed;
        }
        catch (Exception ex)
        {
            // DeactivateAsync fault 意味着实例无法证明可安全释放，保留其 lifetime 直到 Host 重启。
            cleanupCancellation.Dispose();
            _restartRetained.Enqueue(new RestartRetainedPlugin(plugin, lifetime, null, null, ex));
            retainLifetime = true;
            return new PluginCleanupResult(PluginCleanupOutcome.Failed, ex);
        }
        finally
        {
            if (!retainLifetime)
            {
                cleanupCancellation.Dispose();
                lifetime.Dispose();
            }
        }
    }

    private static void BeginCancellation(CancellationTokenSource source)
    {
        _ = source.CancelAsync().ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private (string Error, bool RequiresRestart) GetStatus(PluginId pluginId)
        => _status.TryGetValue(pluginId, out var status)
            ? (status.Error, status.RequiresRestart)
            : (string.Empty, false);

    private enum PluginCleanupOutcome
    {
        Completed,
        Failed,
        TimedOut
    }

    private sealed record PluginCleanupResult(PluginCleanupOutcome Outcome, Exception? Exception)
    {
        public static PluginCleanupResult Completed { get; } = new(PluginCleanupOutcome.Completed, null);
        public bool IsUnsafe => Outcome != PluginCleanupOutcome.Completed;
        public bool RequiresRestart => IsUnsafe;
    }

    private sealed record RestartRetainedPlugin(
        IFolderRewindPlugin Plugin,
        CancellationTokenSource Lifetime,
        CancellationTokenSource? CleanupCancellation,
        Task? CleanupTask,
        Exception? CleanupException);

    private sealed class PluginStageFailureException(Exception primaryFailure, PluginCleanupResult cleanup)
        : Exception("Plugin staging failed.", primaryFailure)
    {
        public Exception PrimaryFailure { get; } = primaryFailure;
        public PluginCleanupResult Cleanup { get; } = cleanup;
    }

    private sealed record StagedPlugin(
        PluginActivationCandidate Candidate,
        IFolderRewindPlugin Plugin,
        CapabilityRegistrationSet Registrations,
        CancellationTokenSource Lifetime,
        PluginActivationCommit Commit)
    {
        public PluginRuntimeSession CreateSession()
            => new(Candidate.PluginId, Plugin, Registrations, Candidate.HostServices, Lifetime);
    }
}
