using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

public sealed record ProjectSourceAttention(string ConfigId, string SourceId, string Label);
internal sealed record ProjectProtectionSummary(string Text, IReadOnlyList<ProjectSourceAttention> Issues);

// Only queries an already-open index; never scans payloads or probes a remote.
internal static class ProjectProtectionSummaryService
{
    internal static async Task<ProjectProtectionSummary> ReadAsync(BackupConfig config, bool includeSourceResults, CancellationToken token)
    {
        var issues = new List<ProjectSourceAttention>();
        var names = config.SourceFolders.Where(f => Guid.TryParse(f.Id, out _)).ToDictionary(f => Guid.Parse(f.Id), f => f.DisplayName);
        var projectName = config.Name;
        var schedule = config.Automation.AutoBackupEnabled;
        var cloudConfigured = config.Cloud.Enabled || !string.IsNullOrEmpty(config.Cloud.RcloneConfigPath);
        string text;
        if (config.SourceFolders.Count == 0) text = I18n.GetString("HomeProtection_NoSources");
        else if (!NativeHistoryCoreGateway.TryGetRuntime(new(config.Id), out var runtime) || runtime is null)
            text = I18n.GetString("HomeProtection_Unconfirmed");
        else
        {
            var versions = await runtime.Query.GetAllVersionsAsync(token).ConfigureAwait(false);
            var lastRun = await runtime.Query.GetLatestRunAsync(token).ConfigureAwait(false);
            if (runtime.Health != HistoryRuntimeHealth.Ready) text = I18n.GetString("HomeProtection_RecoveryNeeded");
            else if (versions.Count == 0) text = I18n.GetString("HomeProtection_NoVersion");
            else
            {
                var latest = versions.OrderByDescending(v => v.CreatedAtUtc).First();
                text = I18n.Format("HomeProtection_Committed", versions.Count, UserDisplayFormatter.LongDateTime(latest.CreatedAtUtc.LocalDateTime));
                if (latest.CaptureScope == CaptureScope.PartialSource || latest.Diagnostics.Any(d => d.Severity is HistoryDiagnosticSeverity.Warning or HistoryDiagnosticSeverity.Error))
                    text = I18n.GetString("HomeProtection_Warnings") + " · " + text;
            }
            if (lastRun is not null)
            {
                text += "\n" + I18n.Format("HomeProtection_LastRun", I18n.GetString("HomeProtection_Run" + lastRun.Outcome),
                    UserDisplayFormatter.LongDateTime(lastRun.CompletedAtUtc.LocalDateTime), lastRun.SourceResults.Length);
                foreach (var result in lastRun.SourceResults)
                {
                    var name = names.GetValueOrDefault(result.SourceId.Value) ?? result.SourceId.ToString();
                    var outcome = I18n.GetString("HomeProtection_Source" + result.Outcome);
                    if (result.Outcome is BackupRunSourceOutcome.Failed or BackupRunSourceOutcome.Unavailable or BackupRunSourceOutcome.CarriedForward
                        || result.Diagnostics.Any(d => d.Severity is HistoryDiagnosticSeverity.Warning or HistoryDiagnosticSeverity.Error))
                        issues.Add(new(config.Id, result.SourceId.ToString(), I18n.Format("HomeProtection_IssueLabel", projectName, name, outcome)));
                    if (includeSourceResults) text += "\n" + name + ": " + outcome + " · " + result.VersionId;
                }
                if (!includeSourceResults && issues.Count > 0)
                    text += "\n" + I18n.Format("HomeProtection_SourceFailures", string.Join(", ", issues.Select(i => i.Label)));
            }
        }
        text += "\n" + I18n.GetString(schedule ? "HomeProtection_DesktopScheduleOn" : "HomeProtection_DesktopScheduleOff");
        text += "\n" + I18n.GetString(cloudConfigured ? "HomeProtection_CloudUnconfirmed" : "HomeProtection_CloudNotConfigured");
        return new(text, issues);
    }
}
