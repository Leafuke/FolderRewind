using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

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
    public static HistoryConfigurationRepairResult RestoreMissingBinding(
        BackupConfig config,
        MissingHistoricalSource missing,
        string confirmedPath,
        string expectedConfigRevision)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(missing);
        if (!StringComparer.Ordinal.Equals(config.ConfigRevision, expectedConfigRevision))
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Config changed; rebuild the Checkout plan.");
        if (config.SourceFolders.Any(folder => Guid.TryParse(folder.Id, out var id) && id == missing.SourceId.Value))
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Historical Source binding already exists; rebuild the Checkout plan.");

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
        config.SourceFolders.Add(folderToAdd);
        config.ConfigRevision = Guid.NewGuid().ToString("N");
        var save = ConfigService.SaveWithResult();
        if (save.Success) return new(HistoryConfigurationRepairStatus.Succeeded, string.Empty);

        config.SourceFolders.Remove(folderToAdd);
        config.ConfigRevision = previousRevision;
        return new(HistoryConfigurationRepairStatus.SaveFailed, save.ErrorMessage ?? "Config save failed.");
    }

    public static HistoryConfigurationRepairResult RestoreHistoricalBoundary(
        BackupConfig config,
        HistorySourceBoundaryMismatch mismatch,
        string expectedConfigRevision,
        bool acceptConfigWideFilterImpact)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(mismatch);
        if (!acceptConfigWideFilterImpact)
            return new(HistoryConfigurationRepairStatus.ConfirmationRequired, "Config-wide filter impact must be confirmed.");
        if (!StringComparer.Ordinal.Equals(config.ConfigRevision, expectedConfigRevision))
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Config changed; rebuild the Checkout plan.");
        var folder = config.SourceFolders.SingleOrDefault(item =>
            Guid.TryParse(item.Id, out var id) && id == mismatch.SourceId.Value);
        if (folder is null)
            return new(HistoryConfigurationRepairStatus.StaleConfig, "Source binding is missing; rebuild the Checkout plan.");

        var previousScope = folder.SourceScope;
        var previousFilters = config.Filters;
        var previousRevision = config.ConfigRevision;
        var boundary = mismatch.HistoricalBoundary;
        folder.SourceScope = new BackupSourceScope
        {
            Mode = boundary.ScopeMode == EffectiveBoundaryScopeMode.Include
                ? BackupSourceScopeMode.Include
                : BackupSourceScopeMode.All,
            IncludePatterns = new ObservableCollection<string>(boundary.ScopeRules)
        };
        config.Filters = new FilterSettings
        {
            BackupFilterMode = boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist
                ? BackupFilterMode.Whitelist
                : BackupFilterMode.Blacklist,
            BackupWhitelist = boundary.FilterMode == EffectiveBoundaryFilterMode.Whitelist
                ? new ObservableCollection<string>(boundary.FilterRules)
                : new ObservableCollection<string>(),
            Blacklist = boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist
                ? new ObservableCollection<string>(boundary.FilterRules)
                : new ObservableCollection<string>(),
            UseRegex = boundary.UseRegex,
            RestoreWhitelist = new ObservableCollection<string>(previousFilters.RestoreWhitelist)
        };
        config.ConfigRevision = Guid.NewGuid().ToString("N");
        var save = ConfigService.SaveWithResult();
        if (save.Success) return new(HistoryConfigurationRepairStatus.Succeeded, string.Empty);

        folder.SourceScope = previousScope;
        config.Filters = previousFilters;
        config.ConfigRevision = previousRevision;
        return new(HistoryConfigurationRepairStatus.SaveFailed, save.ErrorMessage ?? "Config save failed.");
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
