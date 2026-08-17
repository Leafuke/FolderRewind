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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cleanup = staged is null
                    ? PluginCleanupResult.Completed
                    : await CleanupAsync(staged.Plugin, staged.Lifetime).ConfigureAwait(false);
                _status[candidate.PluginId] = cleanup.TimedOut
                    ? (PluginRuntimeState.Failed, "Plugin cleanup exceeded the deactivation grace period.", true)
                    : (PluginRuntimeState.Inactive, string.Empty, false);
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Canceled,
                    cleanup.TimedOut ? PluginRuntimeState.Failed : PluginRuntimeState.Inactive,
                    RuntimeDiagnostic.Warning("runtime.activation_canceled", candidate.PluginId, "Activation was canceled before commit.")) with
                {
                    RequiresRestart = cleanup.TimedOut
                };
            }
            catch (Exception ex)
            {
                var cleanup = staged is null
                    ? PluginCleanupResult.Completed
                    : await CleanupAsync(staged.Plugin, staged.Lifetime).ConfigureAwait(false);
                var requiresRestart = cleanup.TimedOut || ex is PluginStageCleanupTimeoutException;
                _status[candidate.PluginId] = (PluginRuntimeState.Failed, ex.Message, requiresRestart);
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Failed,
                    PluginRuntimeState.Failed,
                    RuntimeDiagnostic.Error("runtime.activation_failed", candidate.PluginId, ex.Message)) with
                {
                    RequiresRestart = requiresRestart
                };
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

                try
                {
                    await candidate.Store.CommitAsync(staged.Commit, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    oldSession.ResumeActive();
                    throw;
                }

                _registry.Commit(staged.Registrations);
                var newSession = staged.CreateSession();
                _sessions[candidate.PluginId] = newSession;
                _status[candidate.PluginId] = (PluginRuntimeState.Active, string.Empty, false);

                oldSession.BeginDeactivation();
                var cleanup = await CleanupAsync(oldSession.Plugin, oldSession.Lifetime).ConfigureAwait(false);
                if (cleanup.TimedOut)
                {
                    const string message = "The previous plugin instance exceeded the deactivation grace period.";
                    _status[candidate.PluginId] = (PluginRuntimeState.Active, message, true);
                    return new PluginRuntimeTransitionResult(
                        true,
                        OperationOutcome.SuccessWithWarnings,
                        PluginRuntimeState.Active,
                        [RuntimeDiagnostic.Warning("runtime.previous_deactivation_timeout", candidate.PluginId, message)],
                        RequiresRestart: true);
                }
                if (cleanup.Exception is not null)
                {
                    return new PluginRuntimeTransitionResult(
                        true,
                        OperationOutcome.SuccessWithWarnings,
                        PluginRuntimeState.Active,
                        [RuntimeDiagnostic.Warning("runtime.previous_deactivation_failed", candidate.PluginId, cleanup.Exception.Message)]);
                }
                return PluginRuntimeTransitionResult.Completed(PluginRuntimeState.Active);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var cleanup = staged is null
                    ? PluginCleanupResult.Completed
                    : await CleanupAsync(staged.Plugin, staged.Lifetime).ConfigureAwait(false);
                oldSession.ResumeActive();
                if (cleanup.TimedOut)
                {
                    _status[candidate.PluginId] = (
                        PluginRuntimeState.Active,
                        "Replacement candidate cleanup exceeded the deactivation grace period.",
                        true);
                }
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Canceled,
                    PluginRuntimeState.Active,
                    RuntimeDiagnostic.Warning("runtime.replace_canceled", candidate.PluginId, "The existing runtime session remains active.")) with
                {
                    RequiresRestart = cleanup.TimedOut
                };
            }
            catch (Exception ex)
            {
                var cleanup = staged is null
                    ? PluginCleanupResult.Completed
                    : await CleanupAsync(staged.Plugin, staged.Lifetime).ConfigureAwait(false);
                var requiresRestart = cleanup.TimedOut || ex is PluginStageCleanupTimeoutException;
                oldSession.ResumeActive();
                _status[candidate.PluginId] = (PluginRuntimeState.Active, ex.Message, requiresRestart);
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Failed,
                    PluginRuntimeState.Active,
                    RuntimeDiagnostic.Error("runtime.replace_failed", candidate.PluginId, ex.Message)) with
                {
                    RequiresRestart = requiresRestart
                };
            }
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
            if (cleanup.TimedOut || alreadyRequiresRestart)
            {
                var message = cleanup.TimedOut
                    ? "Plugin deactivation exceeded the deactivation grace period."
                    : "The plugin is logically inactive but a previously retained instance still requires a Host restart.";
                session.MarkFailed();
                _status[pluginId] = (PluginRuntimeState.Failed, message, true);
                return new PluginRuntimeTransitionResult(
                    true,
                    OperationOutcome.SuccessWithWarnings,
                    PluginRuntimeState.Failed,
                    [RuntimeDiagnostic.Warning(
                        cleanup.TimedOut ? "runtime.deactivation_timeout" : "runtime.restart_required",
                        pluginId,
                        message)],
                    RequiresRestart: true);
            }
            if (cleanup.Exception is not null)
            {
                session.MarkFailed();
                _status[pluginId] = (PluginRuntimeState.Failed, cleanup.Exception.Message, false);
                return new PluginRuntimeTransitionResult(
                    true,
                    OperationOutcome.SuccessWithWarnings,
                    PluginRuntimeState.Failed,
                    [RuntimeDiagnostic.Warning("runtime.deactivation_failed", pluginId, cleanup.Exception.Message)]);
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
            if (cleanup.TimedOut) throw new PluginStageCleanupTimeoutException(ex);
            throw;
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
            lifetime.Dispose();
            return new PluginCleanupResult(false, ex);
        }

        if (await Task.WhenAny(cleanupTask, Task.Delay(_deactivationGracePeriod)).ConfigureAwait(false) != cleanupTask)
        {
            BeginCancellation(cleanupCancellation);
            _ = cleanupTask.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _restartRetained.Enqueue(new RestartRetainedPlugin(plugin, lifetime, cleanupCancellation, cleanupTask));
            return new PluginCleanupResult(true, null);
        }

        try
        {
            await cleanupTask.ConfigureAwait(false);
            return PluginCleanupResult.Completed;
        }
        catch (Exception ex)
        {
            return new PluginCleanupResult(false, ex);
        }
        finally
        {
            cleanupCancellation.Dispose();
            lifetime.Dispose();
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

    private sealed record PluginCleanupResult(bool TimedOut, Exception? Exception)
    {
        public static PluginCleanupResult Completed { get; } = new(false, null);
    }

    private sealed record RestartRetainedPlugin(
        IFolderRewindPlugin Plugin,
        CancellationTokenSource Lifetime,
        CancellationTokenSource CleanupCancellation,
        Task CleanupTask);

    private sealed class PluginStageCleanupTimeoutException(Exception innerException)
        : Exception("Plugin staging failed and candidate cleanup exceeded the deactivation grace period.", innerException);

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
