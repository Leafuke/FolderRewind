namespace FolderRewind.Services;

internal static class RestoreModePolicy
{
    public static bool UseOverwrite(bool isPartialBackup, bool requestedClean)
        => isPartialBackup || !requestedClean;
}
