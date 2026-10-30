using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace FolderRewind.Services;

/// <summary>Copies operation-owned diagnostics once; presentation never retains a plugin-owned dictionary.</summary>
internal static class OperationDiagnosticFormatter
{
    internal static IReadOnlyList<PluginDiagnostic> Snapshot(IEnumerable<PluginDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PluginDiagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            var arguments = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in diagnostic.Arguments) arguments.Add(pair.Key, pair.Value);
            var copy = diagnostic with { Arguments = new ReadOnlyDictionary<string, string>(arguments) };
            if (seen.Add(Identity(copy))) result.Add(copy);
        }
        return result.AsReadOnly();
    }

    internal static IReadOnlyList<PluginDiagnostic> CaptureWarnings(IEnumerable<PluginDiagnostic> plugin,
        IEnumerable<HistoryDiagnostic> history)
    {
        var originals = plugin.Where(item => item.Severity == DiagnosticSeverity.Warning).ToArray();
        var projected = history.Where(item => item.Severity == HistoryDiagnosticSeverity.Warning
            && !originals.Any(original => original.Code == item.Code
                && original.Arguments.GetValueOrDefault("message", original.Code) == item.Message))
            .Select(item => new PluginDiagnostic(item.Code, DiagnosticSeverity.Warning, "Capture", "folderrewind.core",
                new Dictionary<string, string> { ["message"] = item.Message }));
        return Snapshot(originals.Concat(projected));
    }

    internal static IReadOnlyList<PluginDiagnostic> CaptureErrors(IEnumerable<PluginDiagnostic> plugin,
        IEnumerable<HistoryDiagnostic> history) => Snapshot(plugin.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Concat(history.Where(d => d.Severity == HistoryDiagnosticSeverity.Error)
                .Select(d => new PluginDiagnostic(d.Code, DiagnosticSeverity.Error, "Capture", "folderrewind.core",
                    new Dictionary<string, string> { ["message"] = d.Message }))));

    internal static string Identity(PluginDiagnostic diagnostic)
    {
        var key = new StringBuilder();
        void Add(string value) => key.Append(value.Length).Append(':').Append(value);
        Add(diagnostic.Owner); Add(diagnostic.Capability); Add(diagnostic.Code); Add(diagnostic.Severity.ToString());
        foreach (var pair in diagnostic.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { Add(pair.Key); Add(pair.Value); }
        return key.ToString();
    }

    internal static string Describe(PluginDiagnostic diagnostic)
    {
        var key = diagnostic.Code switch
        {
            "minerewind.world_lock_probe_failed" => "OperationDiagnostic_WorldLockProbeFailed",
            "minerewind.restore_knotlink_unavailable" => "OperationDiagnostic_RestoreKnotLinkUnavailable",
            "minerewind.restore_handshake_unavailable" => "OperationDiagnostic_RestoreHandshakeUnavailable",
            "minerewind.restore_world_exit_timeout" => "OperationDiagnostic_RestoreExitTimeout",
            "minerewind.restore_world_still_occupied" => "OperationDiagnostic_RestoreWorldOccupied",
            "minerewind.restore_multiple_active_worlds" => "OperationDiagnostic_RestoreMultipleWorlds",
            "minerewind.restore_coordination_failed" => "OperationDiagnostic_RestoreCoordinationFailed",
            "minerewind.restore_rejoin_failed" => "OperationDiagnostic_RestoreRejoinFailed",
            "minerewind.consistency_raw_source" => "OperationDiagnostic_ConsistencyRawSource",
            "minerewind.consistency_handshake_unavailable" => "OperationDiagnostic_ConsistencyHandshakeUnavailable",
            "minerewind.consistency_save_timeout" => "OperationDiagnostic_ConsistencySaveTimeout",
            "minerewind.consistency_coordination_failed" => "OperationDiagnostic_ConsistencyCoordinationFailed",
            "minerewind.snapshot_fallback" => "OperationDiagnostic_SnapshotFallback",
            "minerewind.snapshot_links_skipped" => "OperationDiagnostic_SnapshotLinksSkipped",
            "plugin.backup_raw_fallback_requires_complete_full" => "OperationDiagnostic_RawFallbackBlocked",
            "plugin.backup_consistency_fallback" => "OperationDiagnostic_ConsistencyFallback",
            "plugin.backup_consistency_cleanup_failed" => "OperationDiagnostic_ConsistencyCleanupFailed",
            "backup.file_too_small" => "OperationDiagnostic_BackupFileTooSmall",
            "backup.history_recovery_required" => "OperationDiagnostic_BackupHistoryRecovery",
            "backup.post_commit_finalization_failed" => "OperationDiagnostic_BackupFinalizationFailed",
            "backup.post_commit_config_save_failed" => "OperationDiagnostic_BackupConfigSaveFailed",
            "backup.post_commit_cloud_failed" => "OperationDiagnostic_BackupCloudFailed",
            "backup.post_commit_retention_failed" => "OperationDiagnostic_BackupRetentionFailed",
            "backup.warning_unspecified" => "OperationDiagnostic_BackupWarningUnspecified",
            _ => null
        };
        return key is null ? I18n.GetString("OperationDiagnostic_Unknown")
            : diagnostic.Code == "minerewind.world_lock_probe_failed"
                ? I18n.Format(key, diagnostic.Arguments.GetValueOrDefault("path", "session.lock")) : I18n.GetString(key);
    }

    internal static string Summary(IEnumerable<PluginDiagnostic> diagnostics)
        => string.Join(Environment.NewLine, Snapshot(diagnostics).OrderByDescending(item => item.Severity).Select(Describe));

    internal static string Technical(PluginDiagnostic diagnostic)
    {
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        return $"code={Escape(diagnostic.Code)}; severity={diagnostic.Severity}; owner={Escape(diagnostic.Owner)}; capability={Escape(diagnostic.Capability)}; "
            + string.Join("; ", diagnostic.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value)));
    }

    internal static string Details(IEnumerable<PluginDiagnostic> diagnostics)
        => string.Join(Environment.NewLine + Environment.NewLine, Snapshot(diagnostics).Select(item => Describe(item) + Environment.NewLine + Technical(item)));

    internal static string TaskDetails(BackupTask task)
    {
        var sections = new List<string>();
        void Add(string key, string text) { if (!string.IsNullOrWhiteSpace(text)) sections.Add(I18n.GetString(key) + Environment.NewLine + text); }
        Add("BackupTasks_DetailsErrors", task.ErrorDiagnostics.Count > 0 ? Details(task.ErrorDiagnostics) : task.ErrorMessage);
        Add("BackupTasks_DetailsWarnings", Details(task.WarningDiagnostics));
        Add("BackupTasks_DetailsLog", task.Log);
        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    internal static void Log(IEnumerable<PluginDiagnostic> diagnostics, string context, string source, LogLevel? levelOverride = null)
    {
        foreach (var diagnostic in Snapshot(diagnostics))
            LogService.Log(Describe(diagnostic) + " " + context + "; " + Technical(diagnostic), levelOverride ?? diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => LogLevel.Error,
                DiagnosticSeverity.Warning => LogLevel.Warning,
                _ => LogLevel.Info
            }, source);
    }
}
