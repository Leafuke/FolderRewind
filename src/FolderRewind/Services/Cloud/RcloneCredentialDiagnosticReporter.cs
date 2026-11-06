using System;

namespace FolderRewind.Services;

internal static class RcloneCredentialDiagnosticReporter
{
    internal static void Report(Exception error, string source)
        => Report(RcloneConnectionDiagnostics.Capture(error), source);
    internal static void Report(RcloneFailureDetails details, string source)
        => LogService.LogWarning(details.ToLogString(), source);
}
