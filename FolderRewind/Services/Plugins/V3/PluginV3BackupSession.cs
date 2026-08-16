using System.Collections.ObjectModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Services.Plugins.V3;

internal sealed class PluginV3BackupSession : IAsyncDisposable
{
    private PluginCapabilityLease<IBackupConsistencyCapability>? _capabilityLease;
    private IConsistencyLease? _consistencyLease;
    private readonly PluginId? _pluginId;
    private readonly ConfigSnapshot? _configSnapshot;
    private readonly FolderSnapshot? _folderSnapshot;
    private readonly ConsistencyIntent _intent;
    private readonly List<PluginDiagnostic> _diagnostics;

    private PluginV3BackupSession(
        BackupConfig config,
        ManagedFolder folder,
        OperationResolution resolution,
        IReadOnlyList<PluginDiagnostic> diagnostics,
        PluginId? pluginId = null,
        ConfigSnapshot? configSnapshot = null,
        FolderSnapshot? folderSnapshot = null,
        ConsistencyIntent intent = ConsistencyIntent.Prefer)
    {
        EffectiveConfig = config;
        EffectiveFolder = folder;
        Resolution = resolution;
        _diagnostics = diagnostics.ToList();
        _pluginId = pluginId;
        _configSnapshot = configSnapshot;
        _folderSnapshot = folderSnapshot;
        _intent = intent;
    }

    public BackupConfig EffectiveConfig { get; }
    public ManagedFolder EffectiveFolder { get; }
    public OperationResolution Resolution { get; }
    public IReadOnlyList<PluginDiagnostic> Diagnostics => _diagnostics;
    public bool IsBlocked => Resolution.Readiness == OperationReadiness.Blocked;
    public string SourcePath => _consistencyLease?.SourcePath ?? EffectiveFolder.Path;

    public static async ValueTask<PluginV3BackupSession> PrepareAsync(
        BackupConfig originalConfig,
        ManagedFolder originalFolder,
        CancellationToken cancellationToken = default)
    {
        var (config, folder) = PluginV3ModelMapper.CloneForOperation(originalConfig, originalFolder);
        var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
        var folderId = Guid.Parse(folder.Id);
        var folderSnapshot = configSnapshot.Folders.Single(value => value.FolderId == folderId);
        var owner = configSnapshot.Kind.OwnerId;
        var isCore = string.Equals(owner.Value, "folderrewind.core", StringComparison.Ordinal);
        var pluginId = new PluginId(owner.Value);
        var runtime = PluginV3RuntimeService.Runtime;
        var runtimeState = isCore ? PluginRuntimeState.Active : runtime.GetSnapshot(pluginId).State;
        var declaration = isCore
            ? new ConfigKindDeclaration(
                configSnapshot.Kind,
                new LocalizedText("Folder", new Dictionary<string, string>()),
                new LocalizedText("Folder", new Dictionary<string, string>()),
                string.Empty,
                BackupFallbackPolicy.Block,
                RestoreCoordinationPolicy.None)
            : FindInstalledKind(configSnapshot.Kind)
              ?? new ConfigKindDeclaration(
                  configSnapshot.Kind,
                  new LocalizedText(configSnapshot.Kind.KindId, new Dictionary<string, string>()),
                  new LocalizedText(string.Empty, new Dictionary<string, string>()),
                  string.Empty,
                  BackupFallbackPolicy.Block,
                  RestoreCoordinationPolicy.Required);

        using var scopeProbe = isCore ? null : runtime.TryAcquire<IBackupScopeCapability>(pluginId, cancellationToken);
        using var consistencyProbe = isCore ? null : runtime.TryAcquire<IBackupConsistencyCapability>(pluginId, cancellationToken);
        var providerScopeSelected = config.BackupScope?.IsPluginScopeEnabled == true;
        var intent = config.ConsistencyIntent == PersistedConsistencyIntent.Require
            ? ConsistencyIntent.Require
            : ConsistencyIntent.Prefer;
        var resolution = PluginOperationResolver.Resolve(new PluginOperationResolutionRequest(
            declaration,
            PluginOperationKind.Backup,
            isCore ? PluginRuntimeState.Active : runtimeState,
            providerScopeSelected,
            scopeProbe is not null,
            intent,
            isCore || consistencyProbe is not null,
            false));
        var diagnostics = resolution.Diagnostics.ToList();

        var partial = folder.SourceScope.IsPartial || providerScopeSelected;
        if (resolution.Readiness == OperationReadiness.Degraded
            && (config.Archive.Mode != BackupMode.Full || partial))
        {
            diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_raw_fallback_requires_complete_full",
                DiagnosticSeverity.Error,
                "Backup",
                owner.Value,
                new Dictionary<string, string>()));
            resolution = new OperationResolution(OperationReadiness.Blocked, diagnostics);
        }
        var session = new PluginV3BackupSession(
            config,
            folder,
            resolution,
            diagnostics,
            isCore || runtimeState != PluginRuntimeState.Active ? null : pluginId,
            configSnapshot,
            folderSnapshot,
            intent);
        if (session.IsBlocked || isCore || runtimeState != PluginRuntimeState.Active)
        {
            return session;
        }

        using (var filePolicyLease = runtime.TryAcquire<IFilePolicyCapability>(pluginId, cancellationToken))
        {
            if (filePolicyLease is not null && filePolicyLease.Capability.Kind == configSnapshot.Kind)
            {
                var policy = await filePolicyLease.Capability.ResolveAsync(
                    new FilePolicyRequest(configSnapshot, folderSnapshot),
                    filePolicyLease.Context).ConfigureAwait(false);
                MergeFilePolicy(config.Filters, policy);
                diagnostics.AddRange(policy.Diagnostics);
            }
        }

        if (providerScopeSelected)
        {
            if (scopeProbe is null || scopeProbe.Capability.Kind != configSnapshot.Kind)
            {
                return Block(session, "plugin.backup_scope_mismatch", owner.Value);
            }
            var backupScope = config.BackupScope ?? new BackupScopeSettings();
            var descriptor = scopeProbe.Capability.Scopes.SingleOrDefault(candidate =>
                string.Equals(candidate.Id.OwnerId.Value, backupScope.OwnerId, StringComparison.Ordinal)
                && string.Equals(candidate.Id.ScopeId, backupScope.ScopeId, StringComparison.Ordinal));
            if (descriptor is null
                || !TryBuildScopeParameters(
                    descriptor.FormSchema,
                    backupScope.Parameters ?? new Dictionary<string, string>(),
                    out var parameters))
            {
                return Block(session, "plugin.backup_scope_parameters_invalid", owner.Value);
            }
            var scope = await scopeProbe.Capability.ResolveAsync(
                new BackupScopeRequest(
                    configSnapshot,
                    folderSnapshot,
                    new BackupScopeId(new OwnerId(backupScope.OwnerId), backupScope.ScopeId),
                    parameters),
                scopeProbe.Context).ConfigureAwait(false);
            diagnostics.AddRange(scope.Diagnostics);
            if (scope.Readiness == OperationReadiness.Blocked)
            {
                return Block(session, "plugin.backup_scope_blocked", owner.Value);
            }
            var patterns = BackupSourceScopePatternSet.NormalizeAndValidate(scope.IncludePatterns);
            folder.SourceScope = new BackupSourceScope
            {
                Mode = BackupSourceScopeMode.Include,
                IncludePatterns = new ObservableCollection<string>(patterns)
            };
        }

        return session;
    }

    private static ConfigKindDeclaration? FindInstalledKind(ConfigKindRef kind)
        => PluginV3RuntimeService.FindKind(kind)
           ?? PluginV3PackageService.GetInstalledConfigKinds()
               .Where(pair => pair.PluginId.Value == kind.OwnerId.Value)
               .Select(pair => pair.Kind)
               .SingleOrDefault(candidate => candidate.Kind == kind);

    private static bool TryBuildScopeParameters(
        JsonElement schema,
        IReadOnlyDictionary<string, string> values,
        out IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var properties = schema.ValueKind == JsonValueKind.Object
                         && schema.TryGetProperty("properties", out var propertyElement)
                         && propertyElement.ValueKind == JsonValueKind.Object
            ? propertyElement
            : default;
        foreach (var pair in values)
        {
            var type = properties.ValueKind == JsonValueKind.Object
                       && properties.TryGetProperty(pair.Key, out var definition)
                       && definition.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : "string";
            switch (type)
            {
                case "boolean" when bool.TryParse(pair.Value, out var boolean):
                    result[pair.Key] = JsonSerializer.SerializeToElement(boolean);
                    break;
                case "integer" when long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                    result[pair.Key] = JsonSerializer.SerializeToElement(integer);
                    break;
                case "boolean" or "integer":
                    parameters = new Dictionary<string, JsonElement>();
                    return false;
                default:
                    result[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
                    break;
            }
        }
        parameters = result;
        return true;
    }

    public async ValueTask AcquireConsistencyAsync(CancellationToken cancellationToken = default)
    {
        if (_pluginId is null || _configSnapshot is null || _folderSnapshot is null) return;
        var consistencyLease = PluginV3RuntimeService.Runtime.TryAcquire<IBackupConsistencyCapability>(
            _pluginId.Value,
            cancellationToken);
        if (consistencyLease is null || consistencyLease.Capability.Kind != _configSnapshot.Kind)
        {
            consistencyLease?.Dispose();
            if (_intent == ConsistencyIntent.Require)
                throw new InvalidOperationException("The required consistency capability became unavailable.");
            _diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_consistency_fallback",
                DiagnosticSeverity.Warning,
                "BackupConsistency",
                _pluginId.Value.Value,
                new Dictionary<string, string>()));
            return;
        }
        if (consistencyLease is not null)
        {
            try
            {
                _consistencyLease = await consistencyLease.Capability.AcquireAsync(
                    new BackupConsistencyRequest(_configSnapshot, _folderSnapshot, _intent),
                    consistencyLease.Context).ConfigureAwait(false);
                _capabilityLease = consistencyLease;
                _diagnostics.AddRange(_consistencyLease.Diagnostics);
            }
            catch when (_intent == ConsistencyIntent.Prefer)
            {
                consistencyLease.Dispose();
                _diagnostics.Add(new PluginDiagnostic(
                    "plugin.backup_consistency_fallback",
                    DiagnosticSeverity.Warning,
                    "BackupConsistency",
                    _pluginId.Value.Value,
                    new Dictionary<string, string>()));
            }
        }
    }

    public async ValueTask CompleteCaptureAsync()
    {
        var lease = Interlocked.Exchange(ref _consistencyLease, null);
        if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _capabilityLease, null)?.Dispose();
    }

    public async ValueTask DisposeAsync() => await CompleteCaptureAsync().ConfigureAwait(false);

    private static PluginV3BackupSession Block(
        PluginV3BackupSession session,
        string code,
        string owner)
    {
        var diagnostics = session.Diagnostics.Append(new PluginDiagnostic(
            code,
            DiagnosticSeverity.Error,
            "BackupScope",
            owner,
            new Dictionary<string, string>())).ToArray();
        return new PluginV3BackupSession(
            session.EffectiveConfig,
            session.EffectiveFolder,
            new OperationResolution(OperationReadiness.Blocked, diagnostics),
            diagnostics);
    }

    private static void MergeFilePolicy(FilterSettings filters, FilePolicyResult policy)
    {
        filters.Blacklist ??= new ObservableCollection<string>();
        filters.BackupWhitelist ??= new ObservableCollection<string>();
        foreach (var exclusion in policy.RequiredExclusions.Where(BackupSourceScopePatternSet.IsSafeRelativePattern))
        {
            BackupFilterRulePolicy.AddDistinct(filters.Blacklist, exclusion);
        }
        if (filters.BackupFilterMode == BackupFilterMode.Whitelist)
        {
            foreach (var inclusion in policy.RequiredInclusions.Where(BackupSourceScopePatternSet.IsSafeRelativePattern))
            {
                BackupFilterRulePolicy.AddDistinct(filters.BackupWhitelist, inclusion);
            }
        }
    }
}
