using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FolderRewind.Plugin.Abstractions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum HistoryConfigurationRepairStatus
{
    Succeeded = 0,
    StaleConfig = 1,
    Conflict = 2,
    SaveFailed = 3,
    ConfirmationRequired = 4
}

internal sealed record HistoryConfigurationRepairResult(
    HistoryConfigurationRepairStatus Status,
    string Diagnostic)
{
    public bool Succeeded => Status == HistoryConfigurationRepairStatus.Succeeded;
}

internal static class HistorySourceBindingRepairService
{
    public static async Task<HistoryConfigurationRepairResult> RestoreMissingBindingAsync(
        BackupConfig config,
        MissingHistoricalSource missing,
        string confirmedPath,
        string expectedConfigRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(missing);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, cancellationToken);
        if (!IsCurrent(config) || !StringComparer.Ordinal.Equals(config.ConfigRevision, expectedConfigRevision))
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Config changed; rebuild the Checkout plan.");
        if (config.SourceFolders.Any(folder => Guid.TryParse(folder.Id, out var id) && id == missing.SourceId.Value))
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Historical Source binding already exists; rebuild the Checkout plan.");
        if (ConfigService.CurrentConfig.BackupConfigs.Where(current => !ReferenceEquals(current, config))
            .SelectMany(current => current.SourceFolders)
            .Any(folder => Guid.TryParse(folder.Id, out var id) && id == missing.SourceId.Value))
            return new(HistoryConfigurationRepairStatus.Conflict, I18n.GetString("Config_SourceIdDuplicate"));

        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(confirmedPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(HistoryConfigurationRepairStatus.Conflict, ex.Message);
        }
        if (config.SourceFolders.Any(folder => PathsOverlap(folder.Path, normalizedPath)))
            return new(HistoryConfigurationRepairStatus.Conflict, "Confirmed path overlaps an existing Source binding.");

        var previousRevision = config.ConfigRevision;
        var folderToAdd = new ManagedFolder
        {
            Id = missing.SourceId.ToString(),
            Path = normalizedPath,
            DisplayName = missing.Descriptor.DisplayName,
            SourceScope = new BackupSourceScope()
        };
        var installed = JsonSerializer.Serialize(folderToAdd, AppJsonContext.Default.ManagedFolder);
        var revision = Guid.NewGuid().ToString("N");
        return await PersistAsync(() =>
        {
            config.SourceFolders.Add(folderToAdd);
            config.ConfigRevision = revision;
        }, () =>
        {
            // A later edit to this new source owns it now; never remove that edited source.
            var unchanged = JsonSerializer.Serialize(folderToAdd, AppJsonContext.Default.ManagedFolder) == installed;
            if (unchanged) config.SourceFolders.Remove(folderToAdd);
            RestoreRevision(config, revision, previousRevision, unchanged);
        }, cancellationToken);
    }

    public static async Task<HistoryConfigurationRepairResult> RestoreHistoricalBoundaryAsync(
        BackupConfig config,
        HistorySourceBoundaryMismatch mismatch,
        string expectedConfigRevision,
        bool acceptConfigWideFilterImpact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(mismatch);
        if (!acceptConfigWideFilterImpact)
            return new(HistoryConfigurationRepairStatus.ConfirmationRequired, "Config-wide filter impact must be confirmed.");
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id, cancellationToken);
        if (!IsCurrent(config) || !StringComparer.Ordinal.Equals(config.ConfigRevision, expectedConfigRevision))
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Config changed; rebuild the Checkout plan.");
        var folder = config.SourceFolders.SingleOrDefault(item =>
            Guid.TryParse(item.Id, out var id) && id == mismatch.SourceId.Value);
        if (folder is null)
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Source binding is missing; rebuild the Checkout plan.");

        var previousScope = folder.SourceScope;
        var previousFilters = config.Filters;
        var previousRevision = config.ConfigRevision;
        var boundary = mismatch.HistoricalBoundary;
        var scope = new BackupSourceScope
        {
            Mode = boundary.ScopeMode == EffectiveBoundaryScopeMode.Include
                ? BackupSourceScopeMode.Include
                : BackupSourceScopeMode.All,
            IncludePatterns = new ObservableCollection<string>(boundary.ScopeRules)
        };
        // Save captures normalize the live model. Compare the same persisted spelling on rollback.
        ConfigService.NormalizeSourceScopeForPersistence(scope);
        var filters = new FilterSettings
        {
            BackupFilterMode = boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist
                ? BackupFilterMode.Whitelist
                : BackupFilterMode.Blacklist,
            BackupWhitelist = boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist
                ? new ObservableCollection<PathFilterRule>(boundary.Rules)
                : new ObservableCollection<PathFilterRule>(),
            Blacklist = boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist
                ? new ObservableCollection<PathFilterRule>(boundary.Rules)
                : new ObservableCollection<PathFilterRule>(),
            UseRegex = boundary.UseRegex,
            RestoreWhitelist = new ObservableCollection<PathFilterRule>(previousFilters.RestoreWhitelist),
            SchemaExtensions = new(previousFilters.SchemaExtensions)
        };
        var expectedFilters = JsonSerializer.Serialize(filters, AppJsonContext.Default.FilterSettings);
        var mode = scope.Mode;
        var rules = scope.IncludePatterns.ToArray();
        var blacklist = filters.Blacklist.ToArray();
        var whitelist = filters.BackupWhitelist.ToArray();
        var revision = Guid.NewGuid().ToString("N");
        return await PersistAsync(() =>
        {
            folder.SourceScope = scope;
            config.Filters = filters;
            config.ConfigRevision = revision;
        }, () =>
        {
            var scopeUnchanged = ReferenceEquals(folder.SourceScope, scope) && scope.Mode == mode
                && scope.IncludePatterns.SequenceEqual(rules);
            if (scopeUnchanged) folder.SourceScope = previousScope;
            else if (ReferenceEquals(folder.SourceScope, scope))
            {
                if (scope.Mode == mode) scope.Mode = previousScope.Mode;
                if (scope.IncludePatterns.SequenceEqual(rules)) scope.IncludePatterns = previousScope.IncludePatterns;
            }
            var filtersUnchanged = ReferenceEquals(config.Filters, filters)
                && JsonSerializer.Serialize(filters, AppJsonContext.Default.FilterSettings) == expectedFilters;
            if (filtersUnchanged) config.Filters = previousFilters;
            else if (ReferenceEquals(config.Filters, filters))
            {
                // Restore only fields still equal to our applied values, preserving concurrent edits.
                if (filters.BackupFilterMode == (boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist
                    ? BackupFilterMode.Whitelist : BackupFilterMode.Blacklist)) filters.BackupFilterMode = previousFilters.BackupFilterMode;
                if (filters.UseRegex == boundary.UseRegex) filters.UseRegex = previousFilters.UseRegex;
                if (filters.Blacklist.SequenceEqual(blacklist)) filters.Blacklist = previousFilters.Blacklist;
                if (filters.BackupWhitelist.SequenceEqual(whitelist)) filters.BackupWhitelist = previousFilters.BackupWhitelist;
            }
            RestoreRevision(config, revision, previousRevision, scopeUnchanged && filtersUnchanged);
        }, cancellationToken);
    }

    private static bool IsCurrent(BackupConfig config)
        => ConfigService.CurrentConfig.BackupConfigs.Any(current => ReferenceEquals(current, config));

    private static void RestoreRevision(BackupConfig config, string applied, string previous, bool fullyRestored)
    {
        if (config.ConfigRevision == applied)
            config.ConfigRevision = fullyRestored ? previous : Guid.NewGuid().ToString("N");
    }

    private static async Task<HistoryConfigurationRepairResult> PersistAsync(Action apply, Action rollback, CancellationToken token)
    {
        try
        {
            await ConfigEditTransaction.ApplyAsync(apply, rollback, () => ConfigService.SaveAsync(), "Config save failed.", token);
            return new(HistoryConfigurationRepairStatus.Succeeded, string.Empty);
        }
        catch (IOException error)
        {
            return new(HistoryConfigurationRepairStatus.SaveFailed,
                error is ConfigEditRollbackException ? error.ToString() : error.Message);
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return first.Equals(second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
