using FolderRewind.History.Domain;
using FolderRewind.History.Migration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services;

/// <summary>Device-local dismissal state, separate from migration evidence and archive choices.</summary>
public sealed class LegacyMigrationNoticeState(string configDirectory, HistoryConfigId configId)
{
    private string StatePath => Path.Combine(new LegacyTakeoverService(configDirectory, configId).Root, "dismissed-notice.v1.txt");

    public static int PendingCount(LegacyTakeoverReport report)
        => report.Items.Count(item => item.Status != "RestrictedReady");

    public static bool HasReport(LegacyTakeoverReport report)
        => report.Items.Length > 0 || HasReportProblem(report);

    private static bool HasReportProblem(LegacyTakeoverReport report)
        => report.InputStatus == "Unreadable"
            || report.OperationStatus is "NeedsAttention" or "Failed" or "BindingPersistenceFailed"
            || !string.IsNullOrWhiteSpace(report.Diagnostic);

    public static string[] AttentionKeys(LegacyTakeoverReport report)
    {
        var keys = report.Items.Where(item => item.Status != "RestrictedReady")
            .Select(item => Hash("record:" + item.OriginKey + ":" + item.Status)).ToList();
        if (HasReportProblem(report))
            keys.Add(Hash("report:" + report.InputStatus + ":" + report.OperationStatus));
        return keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public bool ShouldShow(IReadOnlyCollection<string> attentionKeys)
    {
        if (attentionKeys.Count == 0) return false;
        var dismissed = ReadDismissedKeys();
        return attentionKeys.Any(key => !dismissed.Contains(key));
    }

    public void Dismiss(IReadOnlyCollection<string> attentionKeys)
    {
        var dismissed = ReadDismissedKeys();
        dismissed.UnionWith(attentionKeys);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", dismissed.Order(StringComparer.Ordinal)));
        AtomicFileService.Write(StatePath, stream => stream.Write(bytes));
    }

    private HashSet<string> ReadDismissedKeys()
    {
        try
        {
            return File.Exists(StatePath)
                ? new(File.ReadAllLines(StatePath), StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unavailable preference must not hide a migration problem.
            return new(StringComparer.Ordinal);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
