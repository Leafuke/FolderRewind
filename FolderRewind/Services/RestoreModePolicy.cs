namespace FolderRewind.Services;

internal static class RestoreModePolicy
{
    public static bool UseOverwrite(bool requestedClean)
        => !requestedClean;
}
