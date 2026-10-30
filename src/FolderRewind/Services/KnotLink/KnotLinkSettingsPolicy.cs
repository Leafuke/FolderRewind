using System;
using System.Linq;

namespace FolderRewind.Services;

internal enum KnotLinkConnectionStatus { Disabled, NotInitialized, Connected, Partial, Failed }

internal static class KnotLinkSettingsPolicy
{
    public static string? Validate(string? host, params string?[] identifiers)
    {
        if (!string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
            return "Settings_KnotLink_InvalidHost";
        if (identifiers.Any(value => value?.Any(char.IsControl) == true))
            return "Settings_KnotLink_InvalidIdentifier";
        return null;
    }

    public static KnotLinkConnectionStatus GetStatus(bool enabled, bool initialized, bool receiver, bool sender)
        => !enabled ? KnotLinkConnectionStatus.Disabled
        : !initialized ? KnotLinkConnectionStatus.NotInitialized
        : receiver && sender ? KnotLinkConnectionStatus.Connected
        : receiver || sender ? KnotLinkConnectionStatus.Partial
        : KnotLinkConnectionStatus.Failed;
}
