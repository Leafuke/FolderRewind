using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class SpatialWorkspaceViewModel
{
    private readonly List<SpatialScopeSelection> _scopeSelections = [];
    private SpatialScopeResult? _scopePreview;
    private static string ParametersDigest(IEnumerable<KeyValuePair<string, string>> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values.OrderBy(p => p.Key, StringComparer.Ordinal)))));
    private void LoadScopeSelection()
    {
        var folder = _scopeDraft!.Draft.SourceFolders.Single(f => Guid.Parse(f.Id) == _navigation.FolderId);
        var scope = folder.BackupScopeOverride ?? _scopeDraft.Draft.BackupScope;
        if (scope?.SpatialSelection is { } metadata && metadata.ParametersDigest == ParametersDigest(scope.Parameters))
            _scopeSelections.AddRange(metadata.Selections);
        else if (scope?.IsPluginScopeEnabled == true || folder.SourceScope.IsPartial)
            Details = I18n.GetString("SpatialWorkspace_LegacyScope");
        UpdateScopeSummary();
    }
    private void EditScopeArea(SpatialScopeSelection area, bool additive, bool remove)
    {
        if (!additive && !remove) _scopeSelections.Clear();
        if (remove)
        {
            var remaining = _scopeSelections.SelectMany(s => s.WorldId != area.WorldId || s.LayerId != area.LayerId ? new[] { s }
                : SpatialSelectionGeometry.Subtract(s.Bounds, area.Bounds).Select(b => s with { Bounds = b })).ToArray();
            if (remaining.Length > 128) throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_SelectionLimit"));
            _scopeSelections.Clear(); _scopeSelections.AddRange(remaining);
        }
        else
        {
            if (_scopeSelections.Count >= 128) { Error = I18n.GetString("SpatialWorkspace_SelectionLimit"); Notify(); return; }
            _scopeSelections.Add(area);
        }
        _scopePreview = null; UpdateScopeSummary();
    }
    private void UpdateScopeSummary()
    {
        Counts = I18n.Format("SpatialWorkspace_ScopeCount", _scopeSelections.Count);
        Items.Clear();
        for (var i = 0; i < _scopeSelections.Count; i++)
        {
            var selection = _scopeSelections[i]; var b = selection.Bounds;
            var title = I18n.Format("SpatialWorkspace_AreaTitle", i + 1, b.MinX, b.MinY, b.MaxX, b.MaxY);
            var details = I18n.GetString("SpatialWorkspace_ScopeLegend");
            if (_scopePreview is not null) details += "\n\n" + I18n.GetString("SpatialWorkspace_ScopeIncludes") + "\n" + string.Join("\n", _scopePreview.IncludePatterns);
            Items.Add(new(new("scope-" + i, title, details, "Selected", false, [new(selection.WorldId, selection.LayerId, b)])));
        }
        Notify(); OverlayChanged?.Invoke();
    }
    private async Task<SpatialScopeResult> ResolveScopeAsync(CancellationToken token, IReadOnlyList<SpatialScopeSelection>? requested = null)
    {
        var selections = (requested ?? _scopeSelections).ToArray();
        if (selections.Length == 0) throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_EmptySelection"));
        var source = new SpatialPreviewSource(_origin, _origin.Folders.Single(f => f.FolderId == _navigation.FolderId), Guid.NewGuid());
        return await Task.Run(async () =>
        {
            using var lease = PluginV3RuntimeService.Runtime.TryAcquire<ISpatialScopeSelectionCapability>(new(_origin.Kind.OwnerId.Value), c => c.Kind == _origin.Kind, token)
                ?? throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_ScopeUnavailable"));
            using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
            var result = await lease.Capability.ResolveSelectionAsync(new(source, selections), lease.Context);
            token.ThrowIfCancellationRequested();
            if (result.ScopeId.OwnerId != _origin.Kind.OwnerId || result.EffectiveSelections.Count > 128 || result.Parameters.Count > 128)
                throw new InvalidOperationException("Invalid spatial scope response.");
            _ = BackupSourceScopePatternSet.NormalizeAndValidate(result.IncludePatterns);
            return result with { Parameters = result.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                EffectiveSelections = result.EffectiveSelections.ToArray(), IncludePatterns = result.IncludePatterns.ToArray(), Diagnostics = result.Diagnostics.ToArray() };
        }, token);
    }
    private Task EditScopeAreaAsync(SpatialScopeSelection area, bool additive, bool remove) => RunAsync(async token =>
    {
        var previous = _scopeSelections.ToArray();
        try
        {
            var normalized = await ResolveScopeAsync(token, [area]);
            if (!additive && !remove) _scopeSelections.Clear();
            foreach (var selection in normalized.EffectiveSelections) EditScopeArea(selection, true, remove);
            if (_scopeSelections.Count > 0)
            {
                _scopePreview = await ResolveScopeAsync(token);
                _scopeSelections.Clear(); _scopeSelections.AddRange(_scopePreview.EffectiveSelections);
            }
            UpdateScopeSummary();
        }
        catch { _scopeSelections.Clear(); _scopeSelections.AddRange(previous); UpdateScopeSummary(); throw; }
    });
    public Task PreviewScopeAsync() => RunAsync(async token =>
    {
        _scopePreview = await ResolveScopeAsync(token); _scopeSelections.Clear(); _scopeSelections.AddRange(_scopePreview.EffectiveSelections);
        var previewConfig = ConfigSnapshotMapper.Copy(_scopeDraft!.Draft);
        var previewFolder = previewConfig.SourceFolders.Single(f => Guid.Parse(f.Id) == _navigation.FolderId);
        previewFolder.BackupScopeOverride = new() { OwnerId = _scopePreview.ScopeId.OwnerId.Value, ScopeId = _scopePreview.ScopeId.ScopeId, Parameters = _scopePreview.Parameters.ToDictionary(p => p.Key, p => p.Value) };
        var boundary = await Task.Run(async () => await HistorySourceBoundaryResolver.ResolveAsync(previewConfig, previewFolder, token), token);
        if (boundary.IsBlocked) throw new InvalidOperationException(string.Join("\n", boundary.Diagnostics.Select(d => d.Arguments.TryGetValue("error", out var reason) ? reason : d.Code)));
        Details = I18n.GetString("SpatialWorkspace_ScopeIncludes") + "\n" + string.Join("\n", boundary.EffectiveFolder.SourceScope.IncludePatterns)
            + "\n\n" + I18n.GetString("SpatialWorkspace_ScopeFilters") + "\n" + string.Join("\n", boundary.Boundary.FilterRules);
        UpdateScopeSummary();
    });
    public Task SaveScopeAsync() => RunAsync(async token =>
    {
        var preview = await ResolveScopeAsync(token);
        var draft = _scopeDraft!.Draft;
        var folder = draft.SourceFolders.Single(f => Guid.Parse(f.Id) == _navigation.FolderId);
        var prior = folder.BackupScopeOverride;
        folder.BackupScopeOverride = new()
        {
            OwnerId = preview.ScopeId.OwnerId.Value, ScopeId = preview.ScopeId.ScopeId,
            Parameters = preview.Parameters.ToDictionary(p => p.Key, p => p.Value),
            SpatialSelection = new(ParametersDigest(preview.Parameters), preview.EffectiveSelections.ToImmutableArray())
        };
        if (draft.ArtifactTransformPolicy?.ExperimentalEnabled == true && !ExperimentalArtifactPolicyService.SupportsConfiguration(draft))
        { folder.BackupScopeOverride = prior; throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_P3Conflict")); }
        var resolution = await Task.Run(async () => await HistorySourceBoundaryResolver.ResolveAsync(draft, folder, token), token);
        if (resolution.IsBlocked) { folder.BackupScopeOverride = prior; throw new InvalidOperationException(string.Join("\n", resolution.Diagnostics.Select(d => d.Arguments.TryGetValue("error", out var reason) ? reason : d.Code))); }
        await CommitScopeAsync(token);
    });
    public Task ResetScopeAsync(bool inherit) => RunAsync(async token =>
    {
        var folder = _scopeDraft!.Draft.SourceFolders.Single(f => Guid.Parse(f.Id) == _navigation.FolderId);
        folder.BackupScopeOverride = inherit ? null : new BackupScopeSettings();
        if (_scopeDraft.Draft.ArtifactTransformPolicy?.ExperimentalEnabled == true && !ExperimentalArtifactPolicyService.SupportsConfiguration(_scopeDraft.Draft))
            throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_P3Conflict"));
        await CommitScopeAsync(token); _scopeSelections.Clear(); LoadScopeSelection();
    });
    private async Task CommitScopeAsync(CancellationToken token)
    {
        var result = await _scopeDraft!.CommitAsync(token);
        if (!result.Success) throw new InvalidOperationException(result.ErrorMessage);
        await ClosePanesAsync();
        _config = ConfigSnapshotMapper.Copy(_scopeDraft.LastCommittedSnapshot!); _origin = PluginV3ModelMapper.ToSnapshot(_config);
        await OpenPanesAsync(token);
        Details = I18n.GetString("SpatialWorkspace_ScopeSaved"); Notify();
    }
}
