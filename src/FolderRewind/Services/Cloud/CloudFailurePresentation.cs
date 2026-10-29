using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FolderRewind.Services;

internal static class CloudFailurePresentation
{
    internal static string Kind(Exception error) => error switch
    {
        TimeoutException => "Timeout",
        OperationCanceledException => "Timeout",
        UnauthorizedAccessException => "Permission",
        _ => Kind(RcloneConnectionDiagnostics.Code(error), error.Message)
    };

    internal static string Kind(string code, string diagnostic)
    {
        var text = code + " " + diagnostic;
        if (text.Contains("tool-unavailable", StringComparison.Ordinal)) return "Tool";
        if (text.Contains("source-config-unavailable", StringComparison.Ordinal) || text.Contains("credentials-missing", StringComparison.Ordinal)
            || text.Contains("snapshot-", StringComparison.Ordinal)) return "Configuration";
        if (Regex.IsMatch(text, @"\b(?:401|Unauthorized|invalid_grant)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Authentication";
        if (Regex.IsMatch(text, @"\b(?:403|Forbidden|permission denied|access denied)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Permission";
        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("deadline exceeded", StringComparison.OrdinalIgnoreCase)) return "Timeout";
        if (text.Contains("connectex", StringComparison.OrdinalIgnoreCase) || text.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such host", StringComparison.OrdinalIgnoreCase) || text.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"\b(?:502|503)\b", RegexOptions.CultureInvariant)) return "Offline";
        if (text.Contains("payload-", StringComparison.Ordinal) || text.Contains("manifest-invalid", StringComparison.Ordinal)
            || text.Contains("intent-reconciliation", StringComparison.Ordinal) || text.Contains("metadata-sync", StringComparison.Ordinal)) return "History";
        if (text.Contains("remote-mismatch", StringComparison.Ordinal) || text.Contains("target-changed", StringComparison.Ordinal)
            || text.Contains("[connection.", StringComparison.Ordinal)) return "Connection";
        return "Unknown";
    }

    internal static string Message(string kind) => I18n.GetString("CloudFailure_" + kind);
}
