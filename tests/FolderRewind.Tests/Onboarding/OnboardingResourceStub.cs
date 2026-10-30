namespace FolderRewind.Services;

// Linked non-WinUI services test diagnostic identities; localized display is checked by the app build.
internal static class I18n
{
    public static string GetString(string key) => key;
    public static string Format(string key, params object[] arguments) => key + ": " + string.Join("; ", arguments);
}
