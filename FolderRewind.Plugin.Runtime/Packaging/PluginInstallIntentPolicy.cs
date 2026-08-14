namespace FolderRewind.Plugin.Runtime.Packaging;

public static class PluginInstallIntentPolicy
{
    public static bool ResolveAfterInstall(bool isUpdate, bool existingEnabledIntent)
        => isUpdate && existingEnabledIntent;
}
